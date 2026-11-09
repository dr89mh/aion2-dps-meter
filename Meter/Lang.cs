using System.Globalization;

namespace DpsMeter.Meter;

/// <summary>Current UI language: "de" or "en"; the default follows the Windows display language.</summary>
public static class L
{
    public static readonly string[] Languages = ["en", "de"];

    private static volatile string _lang = Default;

    public static string Default => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de" ? "de" : "en";

    public static string Lang
    {
        get => _lang;
        set => _lang = value is "de" or "en" ? value : Default;
    }

    public static bool De => _lang == "de";

    /// <summary>The English or the German text, depending on <see cref="Lang"/>.</summary>
    public static string T(string en, string de) => De ? de : en;

    public static CultureInfo Culture => De ? CultureInfo.GetCultureInfo("de-DE") : CultureInfo.GetCultureInfo("en-US");

    public static string AppName => T("DPS Meter", "DPS-Meter");
}

/// <summary>Number and time formats for the meter.</summary>
public static class Fmt
{
    /// <summary>12.345.678 → "12,3 Mio" (de) / "12.3M" (en); below 10,000 the full number.</summary>
    public static string Short(double v)
    {
        var c = L.Culture;
        var a = Math.Abs(v);
        if (a >= 1_000_000_000) return (v / 1_000_000_000).ToString(a >= 10_000_000_000 ? "0.0" : "0.00", c) + L.T("B", " Mrd");
        if (a >= 1_000_000) return (v / 1_000_000).ToString(a >= 10_000_000 ? "0.0" : "0.00", c) + L.T("M", " Mio");
        if (a >= 10_000) return (v / 1_000).ToString(a >= 100_000 ? "0" : "0.0", c) + L.T("k", " Tsd");
        return v.ToString("#,0", c);
    }

    /// <summary>Like <see cref="Short"/> but compact for narrow columns ("12,3M" in both languages).</summary>
    public static string Compact(double v)
    {
        var c = L.Culture;
        var a = Math.Abs(v);
        if (a >= 1_000_000_000) return (v / 1_000_000_000).ToString("0.0", c) + "B";
        if (a >= 1_000_000) return (v / 1_000_000).ToString(a >= 10_000_000 ? "0.0" : "0.00", c) + "M";
        if (a >= 10_000) return (v / 1_000).ToString(a >= 100_000 ? "0" : "0.0", c) + "k";
        return v.ToString("#,0", c);
    }

    public static string Full(long v) => v.ToString("#,0", L.Culture);

    public static string Pct(double share) => (share * 100).ToString(share >= 0.995 || share == 0 ? "0" : "0.0", L.Culture) + " %";

    /// <summary>"2:05" / "1:02:05".</summary>
    public static string Duration(long ms)
    {
        var s = Math.Max(0, ms) / 1000;
        return s >= 3600 ? $"{s / 3600}:{s / 60 % 60:00}:{s % 60:00}" : $"{s / 60}:{s % 60:00}";
    }
}
