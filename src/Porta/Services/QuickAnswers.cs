using System.Globalization;

namespace Porta.Services;

public enum QuickKind { Math, Unit, Currency }

public abstract record QuickOutcome;

/// <summary>One answer for the top row. <see cref="CopyText"/> is null when there is nothing to copy (a message).</summary>
public sealed record QuickAnswer(QuickKind Kind, string Title, string Subtitle, string? CopyText) : QuickOutcome;

/// <summary>A currency conversion that still needs exchange rates (which may need a network request).</summary>
public sealed record PendingCurrency(CurrencyRequest Request) : QuickOutcome;

/// <summary>
/// Decides whether what was typed is clearly a calculation or conversion, and answers it: maths, then units
/// (both fully local), then currency (needs rates, so it is returned unanswered). Anything else gives null.
/// </summary>
public static class QuickAnswers
{
    public static QuickOutcome? Evaluate(string query, CultureInfo culture)
    {
        var text = query.Trim();

        switch (Calculator.TryEvaluate(text, culture))
        {
            case Calculator.Value v:
                var number = QuickNumbers.Format(v.Number, culture);
                if (number is null) return new QuickAnswer(QuickKind.Math, "Too large to show", text, null);
                return new QuickAnswer(QuickKind.Math, number, $"{text} · Calculation",
                    QuickNumbers.Format(v.Number, culture, grouping: false));
            case Calculator.Failure f:
                return new QuickAnswer(QuickKind.Math, f.Message, text, null);
        }

        if (UnitConverter.TryConvert(text, culture) is { } c)
        {
            var from = QuickNumbers.Format(c.From, culture);
            var result = QuickNumbers.Format(c.Result, culture, 8);
            if (from is null || result is null) return null;
            var copy = QuickNumbers.Format(c.Result, culture, 8, grouping: false);
            return new QuickAnswer(QuickKind.Unit, $"{result} {c.ToLabel}", $"{from} {c.FromLabel} in {c.ToLabel} · Conversion", $"{copy} {c.ToLabel}");
        }

        if (CurrencyRates.TryParse(text, culture) is { } currency) return new PendingCurrency(currency);
        return null;
    }
}
