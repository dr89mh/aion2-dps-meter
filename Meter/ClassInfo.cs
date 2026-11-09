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

    /// <summary>One colour per class, taken from the colour of its icon in the game; neighbours that are close in the
    /// game (Assassin/Ranger, Cleric/Chanter) are pulled further apart.</summary>
    public static Color Color(int id) => id switch
    {
        11 => System.Drawing.Color.FromArgb(72, 170, 200),  // steel cyan
        12 => System.Drawing.Color.FromArgb(88, 128, 240),  // blue
        13 => System.Drawing.Color.FromArgb(92, 196, 76),   // green
        14 => System.Drawing.Color.FromArgb(40, 160, 120),  // jade
        15 => System.Drawing.Color.FromArgb(150, 100, 230), // violet
        10 or 16 => System.Drawing.Color.FromArgb(205, 75, 190), // magenta
        17 => System.Drawing.Color.FromArgb(232, 198, 80),  // gold
        18 => System.Drawing.Color.FromArgb(230, 130, 50),  // orange
        19 => System.Drawing.Color.FromArgb(215, 60, 60),   // red
        _ => System.Drawing.Color.FromArgb(138, 144, 160),  // grey
    };

    /// <summary>Embedded resource with the class icon of the game (assets/classes), or null.</summary>
    public static string? Icon(int id) => id switch
    {
        11 => "classes/gladiator.png",
        12 => "classes/templar.png",
        13 => "classes/assassin.png",
        14 => "classes/ranger.png",
        15 => "classes/sorcerer.png",
        10 or 16 => "classes/elementalist.png",
        17 => "classes/cleric.png",
        18 => "classes/chanter.png",
        19 => "classes/brawler.png",
        _ => null,
    };

    public static bool IsHealer(int id) => id is 17 or 18;
}
