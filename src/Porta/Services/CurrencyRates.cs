using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Porta.Services;

/// <summary>A parsed "5 usd in eur", waiting for exchange rates.</summary>
public sealed record CurrencyRequest(double Amount, string From, string To, string Expression);

/// <summary>Euro-based exchange rates and the day the European Central Bank published them.</summary>
public sealed record RateTable(string Date, IReadOnlyDictionary<string, double> PerEuro);

/// <summary>
/// Currency conversion for the "quick answer" row, using the free Frankfurter service (European Central Bank
/// reference rates, no key). Nothing is fetched unless "Online currency rates" is on AND a currency conversion
/// has been typed; the table is then kept in memory for 12 hours.
/// </summary>
public static class CurrencyRates
{
    /// <summary>The currencies Frankfurter publishes, so "5 abc in def" never looks like a currency query.</summary>
    public static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        "AUD", "BRL", "CAD", "CHF", "CNY", "CZK", "DKK", "EUR", "GBP", "HKD", "HUF", "IDR", "ILS", "INR", "ISK",
        "JPY", "KRW", "MXN", "MYR", "NOK", "NZD", "PHP", "PLN", "RON", "SEK", "SGD", "THB", "TRY", "USD", "ZAR",
    };

    /// <summary>The rate source; settable only so the offline case can be tested.</summary>
    internal static string Endpoint { get; set; } = "https://api.frankfurter.dev/v1/latest?base=EUR";
    private static readonly TimeSpan FreshFor = TimeSpan.FromHours(12);
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(1);

    private static readonly HttpClient Http = CreateClient();
    private static readonly SemaphoreSlim OneFetch = new(1, 1);
    private static readonly object Gate = new();
    private static RateTable? _table;
    private static DateTime _fetchedAt;
    private static DateTime _failedAt;

    private static readonly Regex Pattern = new(
        @"^(?<n>\d[\d.,]*|[.,]\d+)\s*(?<from>[a-z]{3})\s+(?:in|to|as|into|->|=>|=)\s+(?<to>[a-z]{3})$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var version = typeof(CurrencyRates).Assembly.GetName().Version?.ToString(3) ?? "0";
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Porta/{version} (https://github.com/stephenstirling/porta)");
        return http;
    }

    /// <summary>"5 usd in eur" with two known currency codes, or null. Nothing is sent by parsing.</summary>
    public static CurrencyRequest? TryParse(string query, CultureInfo culture)
    {
        var text = query.Trim();
        if (text.Length is < 8 or > 60) return null;
        var m = Pattern.Match(text);
        if (!m.Success) return null;
        var from = m.Groups["from"].Value.ToUpperInvariant();
        var to = m.Groups["to"].Value.ToUpperInvariant();
        if (!Known.Contains(from) || !Known.Contains(to)) return null;
        var amount = QuickNumbers.Parse(m.Groups["n"].Value, culture);
        if (amount is null || !double.IsFinite(amount.Value)) return null;
        return new CurrencyRequest(amount.Value, from, to, text);
    }

    /// <summary>The rates if they were fetched within the last 12 hours; makes no request.</summary>
    public static bool TryGetFresh(out RateTable table)
    {
        lock (Gate)
        {
            if (_table is not null && DateTime.UtcNow - _fetchedAt < FreshFor) { table = _table; return true; }
        }
        table = null!;
        return false;
    }

    /// <summary>
    /// Fresh rates, fetching them if needed. If the fetch fails (offline, service down) the older table is used
    /// when there is one (its date is shown), otherwise null; a failure is not retried for a minute.
    /// Throws only when cancelled.
    /// </summary>
    public static async Task<RateTable?> GetAsync(CancellationToken ct)
    {
        if (TryGetFresh(out var fresh)) return fresh;
        await OneFetch.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (TryGetFresh(out fresh)) return fresh;
            lock (Gate)
            {
                if (DateTime.UtcNow - _failedAt < RetryAfter) return _table;
            }

            try
            {
                using var doc = JsonDocument.Parse(await Http.GetStringAsync(Endpoint, ct).ConfigureAwait(false));
                var root = doc.RootElement;
                var rates = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { ["EUR"] = 1 };
                foreach (var p in root.GetProperty("rates").EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetDouble(out var rate) && double.IsFinite(rate) && rate > 0)
                        rates[p.Name] = rate;
                var date = root.TryGetProperty("date", out var d) && d.GetString() is { Length: <= 10 } s ? s : "";
                if (rates.Count < 5) throw new InvalidOperationException("too few rates");

                var table = new RateTable(date, rates);
                lock (Gate) { _table = table; _fetchedAt = DateTime.UtcNow; _failedAt = default; }
                return table;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or
                                       InvalidOperationException or KeyNotFoundException or NotSupportedException)
            {
                Debug.WriteLine("Porta: couldn't get exchange rates: " + ex.Message);
                lock (Gate) { _failedAt = DateTime.UtcNow; return _table; }
            }
        }
        finally { OneFetch.Release(); }
    }

    /// <summary>Forgets the rates, for when the switch is turned off.</summary>
    public static void Clear()
    {
        lock (Gate) { _table = null; _fetchedAt = default; _failedAt = default; }
    }

    /// <summary>The converted answer, or null when the table has no rate for one of the currencies.</summary>
    public static QuickAnswer? Convert(CurrencyRequest request, RateTable table, CultureInfo culture)
    {
        if (!table.PerEuro.TryGetValue(request.From, out var from) || !table.PerEuro.TryGetValue(request.To, out var to)) return null;
        var result = request.Amount / from * to;
        if (!double.IsFinite(result)) return new QuickAnswer(QuickKind.Currency, "Too large to show", request.Expression, null);

        var shown = Math.Abs(result) >= 0.01 || result == 0
            ? result.ToString("#,0.00", culture)
            : QuickNumbers.Format(result, culture, 3)!;
        var copy = Math.Abs(result) >= 0.01 || result == 0
            ? result.ToString("0.00", culture)
            : QuickNumbers.Format(result, culture, 3, grouping: false)!;
        var amount = QuickNumbers.Format(request.Amount, culture) ?? "?";
        var source = table.Date.Length > 0 ? $"European Central Bank rates, {table.Date}" : "European Central Bank rates";
        return new QuickAnswer(QuickKind.Currency, $"{shown} {request.To}", $"{amount} {request.From} in {request.To} · {source}", $"{copy} {request.To}");
    }
}
