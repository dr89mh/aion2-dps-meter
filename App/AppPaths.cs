namespace DpsMeter.App;

public static class AppPaths
{
    public static string Root { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DpsMeter");

    public static string Settings => Path.Combine(Root, "einstellungen.json");

    public static string ErrorLog => Path.Combine(Root, "fehler.log");

    /// <summary>Skill icons from the game data CDN (see <see cref="SkillIcons"/>).</summary>
    public static string Icons => Path.Combine(Root, "icons");
}
