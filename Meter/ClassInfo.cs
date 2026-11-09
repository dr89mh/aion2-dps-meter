using System.Drawing;

namespace DpsMeter.Meter;

/// <summary>Aion 2 classes by the first two digits of their skill codes (10 = Elementalist spirit).</summary>
public static class ClassInfo
{
    public const int Spirit = 10;

    public static string Name(int id) => id switch
    {
        11 => "Gladiator",
        12 => L.T("Templar", "Templer"),
        13 => L.T("Assassin", "Assassine"),
        14 => L.T("Ranger", "Jäger"),
        15 => L.T("Sorcerer", "Zauberer"),
        10 or 16 => L.T("Elementalist", "Elementarist"),
        17 => L.T("Cleric", "Kleriker"),
        18 => L.T("Chanter", "Kantor"),
        19 => "Brawler",
        _ => L.T("Unknown", "Unbekannt"),
    };

    /// <summary>Three-letter tag for narrow rows.</summary>
    public static string Tag(int id) => id switch
    {
        11 => "GLA",
        12 => "TEM",
        13 => "ASN",
        14 => "RNG",
        15 => "SOR",
        10 or 16 => "ELE",
        17 => "CLR",
        18 => "CHN",
        19 => "BRW",
        _ => "?",
    };

    public static Color Color(int id) => id switch
    {
        11 => System.Drawing.Color.FromArgb(217, 164, 65),  // amber
        12 => System.Drawing.Color.FromArgb(91, 141, 239),  // blue
        13 => System.Drawing.Color.FromArgb(181, 101, 217), // purple
        14 => System.Drawing.Color.FromArgb(107, 191, 89),  // green
        15 => System.Drawing.Color.FromArgb(79, 195, 217),  // cyan
        10 or 16 => System.Drawing.Color.FromArgb(224, 123, 57), // orange
        17 => System.Drawing.Color.FromArgb(232, 220, 150), // pale gold
        18 => System.Drawing.Color.FromArgb(69, 179, 157),  // teal
        19 => System.Drawing.Color.FromArgb(224, 90, 90),   // red
        _ => System.Drawing.Color.FromArgb(138, 144, 160),  // grey
    };

    public static bool IsHealer(int id) => id is 17 or 18;
}
