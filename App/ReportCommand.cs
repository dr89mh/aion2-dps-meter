using CubeRadar.Core.Capture;
using DpsMeter.Meter;

namespace DpsMeter.App;

/// <summary><c>DpsMeter bericht &lt;file.pcapng …&gt; [--skills] [--min 30]</c>: replays recordings as fast as possible and prints every
/// fight with its players, and for boss fights the damage on the boss against its HP (a check of the counting).</summary>
public static class ReportCommand
{
    public static int Run(string[] args, TextWriter w)
    {
        var files = new List<string>();
        var skills = false;
        var detailed = false;
        var minSeconds = 20;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--skills") skills = true;
            else if (args[i] == "--diag") detailed = true;
            else if (args[i] == "--min" && i + 1 < args.Length && int.TryParse(args[++i], out var m)) minSeconds = m;
            else files.Add(args[i]);
        }
        if (files.Count == 0)
        {
            w.WriteLine(L.T("Usage: DpsMeter report <file.pcapng …> [--skills] [--min <seconds>]",
                "Aufruf: DpsMeter bericht <datei.pcapng …> [--skills] [--min <sekunden>]"));
            return 2;
        }
        var data = GameData.Load();
        foreach (var file in files)
        {
            var engine = new MeterEngine(data) { MaxFights = 10_000, Detailed = detailed };
            using (var pipe = new Pipeline(engine, tickWithPackets: true))
            {
                var started = DateTime.Now;
                CaptureFileSource.ReadAll(file, raw => pipe.OnPacket(raw, "replay"));
                w.WriteLine($"== {Path.GetFileName(file)}: {pipe.Packets:#,0} " + L.T("packets", "Pakete") +
                            $", {(DateTime.Now - started).TotalSeconds:0.0} s");
            }
            Print(engine, data, w, skills, minSeconds);
        }
        return 0;
    }

    private static void Print(MeterEngine engine, GameData data, TextWriter w, bool skills, int minSeconds)
    {
        var fights = engine.AllFights();
        var shown = fights.Where(f => f.HasBoss || f.DurationMs(0) >= minSeconds * 1000L).ToList();
        w.WriteLine(L.T($"{fights.Count} fights, {shown.Count} shown (bosses and ≥ {minSeconds} s)",
            $"{fights.Count} Kämpfe, {shown.Count} gezeigt (Bosse und ≥ {minSeconds} s)"));
        foreach (var f in shown)
        {
            var seconds = f.DurationMs(0) / 1000.0;
            var total = f.Players.Values.Sum(c => c.Damage);
            var start = DateTimeOffset.FromUnixTimeMilliseconds(f.StartMs).ToLocalTime();
            w.WriteLine();
            w.WriteLine($"#{f.Id} {start:HH:mm:ss}  {f.Title("?")}  [{data.ZoneName(f.Zone) ?? f.Zone.ToString()}]  " +
                        $"{Fmt.Duration(f.DurationMs(0))}  Σ {Fmt.Full(total)}  {Fmt.Full((long)(total / seconds))}/s");
            foreach (var en in f.Enemies.Values.Where(e => e.Boss).OrderByDescending(e => e.Damage))
            {
                var lost = en.MaxHp > 0 && en.Hp >= 0 ? en.MaxHp - en.Hp : 0;
                // a wipe resets the boss to full HP: no check then
                var check = lost > en.MaxHp / 50 ? $"  {L.T("check", "Abgleich")} {en.Damage / (double)lost:P1} " +
                                       L.T("of the HP lost", "der verlorenen HP") : "";
                w.WriteLine($"   Boss {en.Name} ({en.Code}): HP {Fmt.Full(en.MaxHp)}, " + L.T("left", "übrig") +
                            $" {Fmt.Full(Math.Max(0, en.Hp))}{(en.Dead ? " †" : "")}, " +
                            L.T("damage", "Schaden") + $" {Fmt.Full(en.Damage)}{check}");
            }
            foreach (var c in f.Players.Values.OrderByDescending(c => c.Damage))
            {
                var share = total > 0 ? c.Damage / (double)total : 0;
                w.WriteLine($"   {(c.IsSelf ? "*" : " ")}{engine.PlayerName(c),-22} {ClassInfo.Tag(c.Class),-4}" +
                            $"{Fmt.Full(c.Damage),14} {Fmt.Full((long)(c.Damage / seconds)),10}/s {share,7:P1}" +
                            $"   {L.T("heal", "Heil")} {Fmt.Full(c.Healing),11}   {L.T("taken", "erlitten")} {Fmt.Full(c.Taken),11}" +
                            (c.Deaths > 0 ? $"  †{c.Deaths}" : ""));
                if (!skills) continue;
                foreach (var s in c.Dealt.Values.OrderByDescending(s => s.Total).Take(25))
                {
                    var hits = Math.Max(1, s.Hits);
                    w.WriteLine($"        {engine.SkillLabel(s.Code, s.Periodic, Mode.Damage),-34} {Fmt.Full(s.Total),13}" +
                                $" {s.Total / (double)Math.Max(1, c.Damage),7:P1}  ×{s.Hits,-4} {L.T("crit", "krit")} {s.Crits / (double)hits,5:P0}" +
                                $"  {L.T("back", "Rücken")} {s.Back / (double)hits,4:P0}  perf {s.Perfect / (double)hits,4:P0}" +
                                $"  {L.T("dbl", "Wucht")} {s.Double / (double)hits,4:P0}  {L.T("multi", "mehrf")} {s.Multi / (double)hits,4:P0}" +
                                $"  max {Fmt.Full(s.Max)}{(s.Misses > 0 ? $"  miss {s.Misses}" : "")}  [{s.Code}]");
                }
            }
        }
        w.WriteLine();
        foreach (var (k, v) in engine.Counters.Where(kv => !kv.Key.Contains(" · ")).OrderBy(kv => kv.Key)) w.WriteLine($"   {k}: {v:#,0}");
        foreach (var g in engine.Counters.Where(kv => kv.Key.Contains(" · ")).GroupBy(kv => kv.Key[..kv.Key.IndexOf(" · ")]))
        {
            w.WriteLine("   -- " + g.Key);
            foreach (var (k, v) in g.OrderByDescending(kv => kv.Value).Take(12)) w.WriteLine($"      {v,12:#,0}  {k[(k.IndexOf(" · ") + 3)..]}");
        }
    }
}
