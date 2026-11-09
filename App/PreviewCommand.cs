using System.Drawing;
using System.Drawing.Imaging;
using CubeRadar.Core.Capture;
using DpsMeter.Meter;

namespace DpsMeter.App;

/// <summary><c>DpsMeter vorschau &lt;file.pcapng&gt; [--kampf N] [--spieler Name] [--out folder]</c>: replays a recording and
/// draws the meter and the skill details of a fight into PNG files (no window opens).</summary>
public static class PreviewCommand
{
    public static int Run(string[] args, TextWriter w)
    {
        string? file = null, player = null, outDir = null;
        var fightId = 0;
        var anonymous = false;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is "--anonym" or "--anonymous") anonymous = true;
            else if (args[i] is "--kampf" or "--fight" && i + 1 < args.Length) fightId = int.Parse(args[++i]);
            else if (args[i] is "--spieler" or "--player" && i + 1 < args.Length) player = args[++i];
            else if (args[i] == "--out" && i + 1 < args.Length) outDir = args[++i];
            else file ??= args[i];
        }
        if (file is null || !File.Exists(file))
        {
            w.WriteLine(L.T("Usage: DpsMeter preview <file.pcapng> [--fight N] [--player name] [--out folder] [--anonymous]",
                "Aufruf: DpsMeter vorschau <datei.pcapng> [--kampf N] [--spieler Name] [--out ordner] [--anonym]"));
            return 2;
        }
        outDir ??= Path.Combine(Path.GetDirectoryName(Path.GetFullPath(file))!, "vorschau");
        Directory.CreateDirectory(outDir);
        var engine = new MeterEngine(GameData.Load()) { MaxFights = 10_000 };
        using (var pipe = new Pipeline(engine, tickWithPackets: true))
            CaptureFileSource.ReadAll(file, raw => pipe.OnPacket(raw, "replay"));
        engine.Split();
        var fights = engine.Fights();
        if (fights.Count == 0)
        {
            w.WriteLine(L.T("No fight in the recording.", "Kein Kampf in der Aufnahme."));
            return 1;
        }
        var fight = fightId > 0 ? fights.FirstOrDefault(f => f.Id == fightId)
                    : fights.Where(f => f.Boss).OrderByDescending(f => f.Damage).FirstOrDefault() ?? fights[0];
        if (fight is null)
        {
            w.WriteLine(L.T($"No fight #{fightId}.", $"Kein Kampf #{fightId}."));
            return 1;
        }
        var view = engine.View(fight.Id, Mode.Damage)!;
        var row = view.Rows.FirstOrDefault(r => player is not null && r.Name.Equals(player, StringComparison.OrdinalIgnoreCase))
                  ?? view.Rows.FirstOrDefault(r => r.IsSelf) ?? view.Rows[0];
        if (anonymous)
        {
            Anonymize(engine);
            row = engine.View(fight.Id, Mode.Damage)!.Rows.First(r => r.Entity == row.Entity);
        }

        var settings = new Settings();
        using var meter = new MeterForm(engine, settings, () => null) { FightId = fight.Id };
        meter.Size = new Size(340, 300);
        meter.Refresh(force: true);
        Save(meter, meter.Draw, Path.Combine(outDir, "meter.png"), w);
        meter.Mode = Mode.Healing;
        meter.Refresh(force: true);
        Save(meter, meter.Draw, Path.Combine(outDir, "meter-heilung.png"), w);

        using var detail = new DetailForm(engine, settings) { Size = new Size(920, 560) };
        foreach (var (mode, name) in new[] { (Mode.Damage, "details.png"), (Mode.Taken, "details-erlitten.png") })
        {
            detail.ShowForPreview(fight.Id, row.Entity, row.Name, mode);
            Save(detail, detail.Draw, Path.Combine(outDir, name), w);
        }
        w.WriteLine($"#{fight.Id} {fight.Title}, {row.Name}");
        return 0;
    }

    private static readonly string[] Pseudonyms =
    [
        "Aerin", "Borka", "Cyrel", "Dravin", "Elyssa", "Faron", "Galen", "Hesra", "Ivor", "Jolan", "Kaelis", "Lyra", "Mirel",
        "Noxa", "Orin", "Pira", "Quell", "Rhea", "Soren", "Tavi", "Ulric", "Vela", "Wren", "Xara", "Yorin", "Zeph",
    ];

    /// <summary>Invented names for every player (for images that are shown to others); the same player keeps the same
    /// name in every fight.</summary>
    private static void Anonymize(MeterEngine engine)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in engine.AllFights())
            foreach (var c in f.Players.Values)
            {
                // unnamed ones ("Sorcerer #123") keep their label and stay in the "others" group
                if (c.Name is not { } real) continue;
                if (!names.TryGetValue(real, out var fake))
                    names[real] = fake = names.Count < Pseudonyms.Length ? Pseudonyms[names.Count] : $"{Pseudonyms[names.Count % Pseudonyms.Length]} {names.Count}";
                c.Name = fake;
            }
    }

    private static void Save(System.Windows.Forms.Form form, Action<Graphics> draw, string path, TextWriter w)
    {
        using var bmp = new Bitmap(form.ClientSize.Width, form.ClientSize.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp)) draw(g);
        bmp.Save(path, ImageFormat.Png);
        w.WriteLine(path);
    }
}
