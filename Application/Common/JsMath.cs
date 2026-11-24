using System.Globalization;

namespace ItrmTracker.Application.Common;

/// <summary>
/// Numeric helpers that reproduce JavaScript semantics exactly, so ported calculations give
/// bit-for-bit the same results as the original Node.js implementation.
/// </summary>
public static class JsMath
{
    /// <summary>JavaScript <c>Number.EPSILON</c> (not <see cref="double.Epsilon"/>, which is the smallest denormal).</summary>
    public const double NumberEpsilon = 2.220446049250313e-16;

    /// <summary>JavaScript <c>Math.round</c>: nearest integer, ties toward +infinity.</summary>
    public static double Round(double x)
    {
        if (double.IsNaN(x) || double.IsInfinity(x)) return x;
        var floor = Math.Floor(x);
        return x - floor >= 0.5 ? floor + 1 : floor;
    }

    /// <summary><c>Math.round((n + Number.EPSILON) * 100) / 100</c> – the engine's two-decimal rounding.</summary>
    public static double Round2(double n) => Round((n + NumberEpsilon) * 100) / 100;

    /// <summary>
    /// JavaScript <c>Number(value)</c> for user input: null/blank handled by the caller; whitespace is trimmed;
    /// anything that is not a finite decimal number yields NaN.
    /// </summary>
    public static double ToNumber(string? value)
    {
        if (value is null) return 0;
        var s = value.Trim();
        if (s.Length == 0) return 0;
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN;
    }

    /// <summary>JavaScript <c>String(number)</c> for the values this app produces (integers and short decimals).</summary>
    public static string ToJsString(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>JavaScript <c>Number.isInteger</c>.</summary>
    public static bool IsInteger(double value) => !double.IsNaN(value) && !double.IsInfinity(value) && Math.Floor(value) == value;
}

/// <summary>Date/time formatting identical to the legacy API (db/database.js normalizeRows).</summary>
public static class LegacyFormat
{
    /// <summary>DATE columns: <c>YYYY-MM-DD</c>.</summary>
    public static string? Date(DateOnly? d) => d?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>DATETIME2 columns: <c>YYYY-MM-DD HH:MM:SS</c> (UTC).</summary>
    public static string? DateTime(DateTime? d) => d?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>Parses a strict <c>YYYY-MM-DD</c> string (the only accepted input format, as in routes/validation.js).</summary>
    public static bool TryParseIsoDate(string? s, out DateOnly date)
    {
        date = default;
        if (string.IsNullOrEmpty(s) || s.Length != 10) return false;
        return DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    /// <summary>Lower-cased, trimmed comparison key (the <c>norm()</c> helper of the original code).</summary>
    public static string Norm(string? s) => (s ?? string.Empty).Trim().ToLowerInvariant();
}
