using System.Text.Json;
using DpsMeter.Meter;

namespace DpsMeter.App;

public sealed class Settings
{
    /// <summary>Meter window (screen pixels); X = -1: top right of the main screen.</summary>
    public int MeterX { get; set; } = -1;
    public int MeterY { get; set; } = -1;
    public int MeterW { get; set; } = 330;
    public int MeterH { get; set; } = 250;
    /// <summary>Skill detail window; X = -1: next to the meter.</summary>
    public int DetailX { get; set; } = -1;
    public int DetailY { get; set; } = -1;
    public int DetailW { get; set; } = 860;
    public int DetailH { get; set; } = 520;
    /// <summary>Window opacity 30..100 %.</summary>
    public int Opacity { get; set; } = 90;
    /// <summary>Text size factor 0.8..1.6.</summary>
    public double Scale { get; set; } = 1.0;
    /// <summary>"de" or "en"; empty = Windows display language.</summary>
    public string Language { get; set; } = "";
    /// <summary>Clicks go through the meter to the game (unlock with Ctrl+Alt+L or the tray menu).</summary>
    public bool Locked { get; set; }
    /// <summary>A fight without bosses ends after this many seconds without a hit.</summary>
    public int QuietSeconds { get; set; } = 8;
    /// <summary>"dps" per second or "total" amount as the main number of a row.</summary>
    public bool ShowTotal { get; set; }
    public bool Hidden { get; set; }
    /// <summary>Install new versions from GitHub by itself (never during a fight or inside an instance).</summary>
    public bool AutoUpdate { get; set; } = true;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(AppPaths.Settings))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(AppPaths.Settings), Json) ?? new Settings();
        }
        catch (Exception ex) when (ex is JsonException or IOException) { }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            File.WriteAllText(AppPaths.Settings, JsonSerializer.Serialize(this, Json));
        }
        catch (IOException) { }
    }

    public void Apply(MeterEngine engine)
    {
        engine.QuietMs = Math.Clamp(QuietSeconds, 3, 60) * 1000L;
        L.Lang = Language;
    }
}
