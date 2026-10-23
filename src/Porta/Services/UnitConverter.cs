using System.Globalization;
using System.Text.RegularExpressions;

namespace Porta.Services;

/// <summary>
/// Unit conversions for the "quick answer" row: length, weight, temperature, volume, speed and data size, for
/// text like "72 f in c" or "5 km to mi". Fully local. Both units must be known and of the same kind, otherwise
/// there is no answer ("3d", "portal 2" or "5 km in kg" show nothing).
/// Volumes are US measures. KB, MB, GB and TB are decimal (1 GB = 1000 MB); KiB, MiB and GiB are binary.
/// </summary>
public static class UnitConverter
{
    public sealed record Conversion(double From, string FromLabel, double Result, string ToLabel);

    private enum Category { Length, Weight, Temperature, Volume, Speed, Data }

    private sealed record Unit(string Label, Category Category, double ToBase);

    private static readonly Dictionary<string, Unit> Units = Build();

    private static readonly Regex Leading = new(@"^(?<sign>[+-]?)(?<n>\d[\d.,]*|[.,]\d+)\s*(?<rest>.+)$", RegexOptions.Compiled);
    private static readonly Regex Separator = new(@"(?<=^|\s)(?:in|to|as|into)(?=\s|$)|->|=>|=", RegexOptions.Compiled);

    public static Conversion? TryConvert(string query, CultureInfo culture)
    {
        var text = query.Trim().ToLowerInvariant();
        if (text.Length < 4 || text.Length > 100) return null;
        text = text.Replace("fl. oz", "floz").Replace("fl oz", "floz").Replace("º", "°");

        var m = Leading.Match(text);
        if (!m.Success) return null;
        var number = QuickNumbers.Parse(m.Groups["n"].Value, culture);
        if (number is null) return null;
        double value = m.Groups["sign"].Value == "-" ? -number.Value : number.Value;

        var rest = m.Groups["rest"].Value;
        foreach (Match split in Separator.Matches(rest))
        {
            var left = Clean(rest[..split.Index]);
            var right = Clean(rest[(split.Index + split.Length)..]);
            if (!Units.TryGetValue(left, out var from) || !Units.TryGetValue(right, out var to)) continue;
            if (from.Category != to.Category) continue;

            double result;
            if (from.Category == Category.Temperature)
            {
                double celsius = ToCelsius(from.Label, value);
                if (celsius < -273.15) return null; // below absolute zero
                result = FromCelsius(to.Label, celsius);
            }
            else result = value * from.ToBase / to.ToBase;
            return double.IsFinite(result) ? new Conversion(value, from.Label, result, to.Label) : null;
        }
        return null;
    }

    private static string Clean(string unit) => unit.Trim().TrimEnd('.').Trim();

    private static double ToCelsius(string label, double v) => label switch
    {
        "°F" => (v - 32) * 5 / 9,
        "K" => v - 273.15,
        _ => v,
    };

    private static double FromCelsius(string label, double c) => label switch
    {
        "°F" => c * 9 / 5 + 32,
        "K" => c + 273.15,
        _ => c,
    };

    private static Dictionary<string, Unit> Build()
    {
        var map = new Dictionary<string, Unit>(StringComparer.OrdinalIgnoreCase);
        void Add(string label, Category category, double toBase, params string[] names)
        {
            var unit = new Unit(label, category, toBase);
            foreach (var name in names) map[name] = unit;
        }

        // Length (metres)
        Add("mm", Category.Length, 0.001, "mm", "millimeter", "millimeters", "millimetre", "millimetres");
        Add("cm", Category.Length, 0.01, "cm", "centimeter", "centimeters", "centimetre", "centimetres");
        Add("m", Category.Length, 1, "m", "meter", "meters", "metre", "metres");
        Add("km", Category.Length, 1000, "km", "kilometer", "kilometers", "kilometre", "kilometres");
        Add("in", Category.Length, 0.0254, "in", "inch", "inches");
        Add("ft", Category.Length, 0.3048, "ft", "foot", "feet");
        Add("yd", Category.Length, 0.9144, "yd", "yard", "yards");
        Add("mi", Category.Length, 1609.344, "mi", "mile", "miles");

        // Weight (kilograms)
        Add("mg", Category.Weight, 0.000001, "mg", "milligram", "milligrams");
        Add("g", Category.Weight, 0.001, "g", "gram", "grams");
        Add("kg", Category.Weight, 1, "kg", "kilogram", "kilograms", "kilo", "kilos");
        Add("t", Category.Weight, 1000, "t", "tonne", "tonnes");
        Add("oz", Category.Weight, 0.028349523125, "oz", "ounce", "ounces");
        Add("lb", Category.Weight, 0.45359237, "lb", "lbs", "pound", "pounds");
        Add("st", Category.Weight, 6.35029318, "st", "stone");

        // Temperature (converted through Celsius)
        Add("°C", Category.Temperature, 1, "c", "°c", "celsius", "centigrade");
        Add("°F", Category.Temperature, 1, "f", "°f", "fahrenheit");
        Add("K", Category.Temperature, 1, "k", "kelvin");

        // Volume (litres, US measures)
        Add("mL", Category.Volume, 0.001, "ml", "milliliter", "milliliters", "millilitre", "millilitres");
        Add("cL", Category.Volume, 0.01, "cl", "centiliter", "centiliters", "centilitre", "centilitres");
        Add("dL", Category.Volume, 0.1, "dl", "deciliter", "deciliters", "decilitre", "decilitres");
        Add("L", Category.Volume, 1, "l", "liter", "liters", "litre", "litres");
        Add("tsp", Category.Volume, 0.00492892159375, "tsp", "teaspoon", "teaspoons");
        Add("tbsp", Category.Volume, 0.01478676478125, "tbsp", "tablespoon", "tablespoons");
        Add("fl oz", Category.Volume, 0.0295735295625, "floz", "fluid ounce", "fluid ounces");
        Add("cup", Category.Volume, 0.2365882365, "cup", "cups");
        Add("pt", Category.Volume, 0.473176473, "pt", "pint", "pints");
        Add("qt", Category.Volume, 0.946352946, "qt", "quart", "quarts");
        Add("gal", Category.Volume, 3.785411784, "gal", "gallon", "gallons");

        // Speed (metres per second)
        Add("m/s", Category.Speed, 1, "m/s", "mps");
        Add("km/h", Category.Speed, 1000.0 / 3600, "km/h", "kmh", "kph", "kmph");
        Add("mph", Category.Speed, 0.44704, "mph", "mi/h");
        Add("kn", Category.Speed, 1852.0 / 3600, "kn", "kt", "knot", "knots");
        Add("ft/s", Category.Speed, 0.3048, "ft/s", "fps");

        // Data size (bytes): decimal KB/MB/GB/TB, binary KiB/MiB/GiB/TiB, and bits
        Add("bit", Category.Data, 0.125, "bit", "bits");
        Add("kbit", Category.Data, 125, "kbit", "kilobit", "kilobits");
        Add("Mbit", Category.Data, 125_000, "mbit", "megabit", "megabits");
        Add("Gbit", Category.Data, 125_000_000, "gbit", "gigabit", "gigabits");
        Add("byte", Category.Data, 1, "byte", "bytes");
        Add("KB", Category.Data, 1e3, "kb", "kilobyte", "kilobytes");
        Add("MB", Category.Data, 1e6, "mb", "megabyte", "megabytes");
        Add("GB", Category.Data, 1e9, "gb", "gigabyte", "gigabytes");
        Add("TB", Category.Data, 1e12, "tb", "terabyte", "terabytes");
        Add("PB", Category.Data, 1e15, "pb", "petabyte", "petabytes");
        Add("KiB", Category.Data, 1024, "kib", "kibibyte", "kibibytes");
        Add("MiB", Category.Data, 1024.0 * 1024, "mib", "mebibyte", "mebibytes");
        Add("GiB", Category.Data, 1024.0 * 1024 * 1024, "gib", "gibibyte", "gibibytes");
        Add("TiB", Category.Data, 1024.0 * 1024 * 1024 * 1024, "tib", "tebibyte", "tebibytes");

        return map;
    }
}
