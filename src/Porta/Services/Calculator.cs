using System.Globalization;
using System.Text.RegularExpressions;

namespace Porta.Services;

/// <summary>Reads and writes numbers the way people type them, in either decimal style.</summary>
public static class QuickNumbers
{
    private static readonly Regex Plain = new(@"^(?:\d[\d.,]*|[.,]\d+)$", RegexOptions.Compiled);

    /// <summary>
    /// "1.5", "1,5", "1,000", "1.000,50" and "1,000.50" all read sensibly. A single "." or "," is a decimal
    /// mark, except exactly three digits after the culture's own thousands mark ("1,000" in English, "1.000" in
    /// German), which is a thousands mark. Odd shapes ("1.2.3", "1,,5", a trailing mark) give null.
    /// </summary>
    public static double? Parse(string text, CultureInfo culture)
    {
        if (!Plain.IsMatch(text) || text[^1] is '.' or ',') return null;
        int dots = text.Count(c => c == '.'), commas = text.Count(c => c == ',');
        string normal;

        if (dots > 0 && commas > 0)
        {
            // Both marks: the last one is the decimal mark and appears once; the other groups thousands.
            char decimalMark = text.LastIndexOf('.') > text.LastIndexOf(',') ? '.' : ',';
            char group = decimalMark == '.' ? ',' : '.';
            if (text.Count(c => c == decimalMark) != 1) return null;
            var integerPart = text[..text.IndexOf(decimalMark)];
            if (!Regex.IsMatch(integerPart, $@"^\d{{1,3}}(?:\{group}\d{{3}})+$")) return null;
            normal = integerPart.Replace(group.ToString(), "") + "." + text[(text.IndexOf(decimalMark) + 1)..];
        }
        else if (dots + commas > 1)
        {
            // The same mark repeated can only be thousands marks: 1,000,000.
            char mark = dots > 0 ? '.' : ',';
            if (!Regex.IsMatch(text, $@"^\d{{1,3}}(?:\{mark}\d{{3}})+$")) return null;
            normal = text.Replace(mark.ToString(), "");
        }
        else if (dots + commas == 1)
        {
            char mark = dots == 1 ? '.' : ',';
            int at = text.IndexOf(mark);
            var before = text[..at];
            var after = text[(at + 1)..];
            var cultureGroup = culture.NumberFormat.NumberGroupSeparator.Replace(' ', ' ');
            bool isThousands = after.Length == 3 && before.Length is >= 1 and <= 3 && !before.StartsWith('0') &&
                               cultureGroup.Length == 1 && cultureGroup[0] == mark;
            normal = isThousands ? before + after : (before.Length == 0 ? "0" : before) + "." + after;
        }
        else normal = text;

        return double.TryParse(normal, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    /// <summary>
    /// The number for showing (or copying, without thousands marks). Whole numbers below 1e15 are exact;
    /// others keep <paramref name="significant"/> digits; very large or very small ones use E notation.
    /// Null for infinity or not-a-number.
    /// </summary>
    public static string? Format(double value, CultureInfo culture, int significant = 10, bool grouping = true)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return null;
        if (value == 0) return "0"; // also turns -0 into 0
        var abs = Math.Abs(value);
        if (abs >= 1e15 || abs < 1e-6)
            return value.ToString("0." + new string('#', significant - 1) + "E+0", culture);

        if (value == Math.Round(value)) return value.ToString(grouping ? "#,0" : "0", culture);
        var rounded = double.Parse(value.ToString("G" + significant, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        return rounded.ToString(grouping ? "#,0.###############" : "0.###############", culture);
    }
}

/// <summary>
/// Arithmetic for the "quick answer" row: + - * / ^, parentheses and percentages ("15% of 240", "200 + 10%").
/// Fully local. Only text that is clearly a calculation gives an answer; anything else returns null, so
/// "portal 2", "3d", "2024", "1-800", "10/22/2026" or a half-typed "5 +" show nothing.
/// </summary>
public static class Calculator
{
    public abstract record Outcome;
    public sealed record Value(double Number) : Outcome;
    /// <summary>It was clearly a calculation but can't be answered (divide by zero, too large...).</summary>
    public sealed record Failure(string Message) : Outcome;

    private const int MaxLength = 200;
    private const int MaxDepth = 40;

    private static readonly Regex DateLike = new(@"^\d{1,4}([/.\-])\d{1,2}\1\d{1,4}$", RegexOptions.Compiled);

    public static Outcome? TryEvaluate(string query, CultureInfo culture)
    {
        var text = query.Trim();
        if (text.Length < 3 || text.Length > MaxLength) return null;
        text = text.Replace('×', '*').Replace('÷', '/').Replace('−', '-').ToLowerInvariant();
        if (DateLike.IsMatch(text)) return null;

        var tokens = Tokenize(text, culture);
        if (tokens is null) return null;

        // "1-800", "3-4" and "2024-10" are far more likely a number, a range or a date than a subtraction.
        bool spaced = text.Any(char.IsWhiteSpace);
        var binary = tokens.Where(t => t.Kind == Kind.Op).Select(t => t.Op).ToList();
        if (!spaced && binary.Count > 0 && binary.All(o => o == '-') && !tokens.Any(t => t.Kind is Kind.Percent or Kind.Of)) return null;

        try
        {
            var parser = new Parser(tokens);
            var result = parser.ParseAll();
            if (result is null || !parser.UsedOperator) return null;
            if (double.IsNaN(result.Value.Number)) return new Failure("No real result");
            if (double.IsInfinity(result.Value.Number)) return new Failure("Too large to show");
            return new Value(result.Value.Number);
        }
        catch (CalcFailure ex)
        {
            return new Failure(ex.Message);
        }
    }

    private enum Kind { Number, Op, Open, Close, Percent, Of }
    private readonly record struct Token(Kind Kind, double Number = 0, char Op = '\0');

    private static List<Token>? Tokenize(string text, CultureInfo culture)
    {
        var tokens = new List<Token>();
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (char.IsAsciiDigit(c) || c is '.' or ',')
            {
                int start = i;
                while (i < text.Length && (char.IsAsciiDigit(text[i]) || text[i] is '.' or ',')) i++;
                var n = QuickNumbers.Parse(text[start..i], culture);
                if (n is null) return null;
                tokens.Add(new Token(Kind.Number, n.Value));
                continue;
            }
            switch (c)
            {
                case '+' or '-' or '*' or '/' or '^': tokens.Add(new Token(Kind.Op, Op: c)); i++; break;
                case '(': tokens.Add(new Token(Kind.Open)); i++; break;
                case ')': tokens.Add(new Token(Kind.Close)); i++; break;
                case '%': tokens.Add(new Token(Kind.Percent)); i++; break;
                default:
                    if (char.IsAsciiLetter(c))
                    {
                        int start = i;
                        while (i < text.Length && char.IsAsciiLetter(text[i])) i++;
                        if (text[start..i] != "of") return null; // any other word: not a calculation
                        tokens.Add(new Token(Kind.Of));
                        break;
                    }
                    return null;
            }
        }
        return tokens.Count > 0 ? tokens : null;
    }

    private sealed class CalcFailure(string message) : Exception(message);

    private readonly record struct Val(double Number, bool IsPercentLiteral);

    private sealed class Parser(List<Token> tokens)
    {
        private int _pos;
        private int _depth;
        public bool UsedOperator { get; private set; }

        private Token? Peek => _pos < tokens.Count ? tokens[_pos] : null;
        private bool IsOp(params char[] ops) => Peek is { Kind: Kind.Op } t && ops.Contains(t.Op);

        /// <summary>The value, or null when the text isn't a well-formed expression (a syntax error).</summary>
        public Val? ParseAll()
        {
            var v = Expr();
            return v is not null && _pos == tokens.Count ? v : null;
        }

        private Val? Expr()
        {
            var left = Term();
            if (left is null) return null;
            while (IsOp('+', '-'))
            {
                char op = tokens[_pos++].Op;
                var right = Term();
                if (right is null) return null;
                UsedOperator = true;
                // "200 + 10%" means 200 plus 10 percent of 200.
                double r = right.Value.IsPercentLiteral ? left.Value.Number * right.Value.Number : right.Value.Number;
                left = new Val(op == '+' ? left.Value.Number + r : left.Value.Number - r, false);
            }
            return left;
        }

        private Val? Term()
        {
            var left = Unary();
            if (left is null) return null;
            while (IsOp('*', '/'))
            {
                char op = tokens[_pos++].Op;
                var right = Unary();
                if (right is null) return null;
                UsedOperator = true;
                if (op == '/' && right.Value.Number == 0) throw new CalcFailure("Can't divide by zero");
                left = new Val(op == '*' ? left.Value.Number * right.Value.Number : left.Value.Number / right.Value.Number, false);
            }
            return left;
        }

        private Val? Unary()
        {
            if (IsOp('-', '+'))
            {
                char op = tokens[_pos++].Op;
                if (++_depth > MaxDepth) return null;
                var inner = Unary();
                _depth--;
                if (inner is null) return null;
                return new Val(op == '-' ? -inner.Value.Number : inner.Value.Number, false);
            }
            return Power();
        }

        private Val? Power()
        {
            var left = Postfix();
            if (left is null) return null;
            if (IsOp('^'))
            {
                _pos++;
                if (++_depth > MaxDepth) return null;
                var right = Unary(); // right-associative, and allows 2^-1
                _depth--;
                if (right is null) return null;
                UsedOperator = true;
                if (left.Value.Number == 0 && right.Value.Number < 0) throw new CalcFailure("Can't divide by zero");
                return new Val(Math.Pow(left.Value.Number, right.Value.Number), false);
            }
            return left;
        }

        private Val? Postfix()
        {
            var atom = Atom();
            if (atom is null) return null;
            if (Peek is { Kind: Kind.Percent })
            {
                _pos++;
                double fraction = atom.Value.Number / 100;
                if (Peek is { Kind: Kind.Of })
                {
                    _pos++;
                    if (++_depth > MaxDepth) return null;
                    var target = Unary();
                    _depth--;
                    if (target is null) return null;
                    UsedOperator = true;
                    return new Val(fraction * target.Value.Number, false);
                }
                return new Val(fraction, true);
            }
            return atom;
        }

        private Val? Atom()
        {
            if (Peek is not { } t) return null;
            if (t.Kind == Kind.Number) { _pos++; return new Val(t.Number, false); }
            if (t.Kind == Kind.Open)
            {
                _pos++;
                if (++_depth > MaxDepth) return null;
                var inner = Expr();
                _depth--;
                if (inner is null || Peek is not { Kind: Kind.Close }) return null;
                _pos++;
                return new Val(inner.Value.Number, false);
            }
            return null;
        }
    }
}
