using CubeRadar.Core.Capture;
using CubeRadar.Core.Dump;
using CubeRadar.Core.Protocol;
using DpsMeter.Protocol;

namespace DpsMeter.App;

/// <summary><c>DpsMeter spur &lt;file&gt; &lt;entity …&gt;</c>: every decoded event of these entities, spawns with their bytes
/// (for working out the protocol).</summary>
public static class TraceCommand
{
    public static int Run(string[] args, TextWriter w)
    {
        if (args.Length < 2) return 2;
        var ids = args[1..].Select(uint.Parse).ToHashSet();
        var events = new List<GameEvent>();
        var hits = new Dictionary<uint, int>();
        using var session = new DumpSession(OpcodeBook.Empty, new DumpOptions { WriteHeartbeats = false }, jsonlPath: null);
        session.Tap = (GameFlow flow, in FrameMeta meta, ReadOnlySpan<byte> body) =>
        {
            events.Clear();
            CombatDecoder.Decode(meta.Opcode, body, flow.TimeMs, events);
            var o = 0;
            if (meta.Opcode == Op.Spawn && Wire.TryVarint(body, ref o, out var id) && ids.Contains(id))
                w.WriteLine($"{T(flow.TimeMs)} 41 36 [{body.Length}] {Wire.Hex(body, 400)}");
            foreach (var e in events)
            {
                var mine = e switch
                {
                    HitEvent h => ids.Contains(h.Actor) || ids.Contains(h.Target),
                    SpawnEvent s => ids.Contains(s.Entity) || ids.Contains(s.Owner),
                    KillEvent k => ids.Contains(k.Entity) || ids.Contains(k.Killer),
                    PlayerEvent p => ids.Contains(p.Entity),
                    SelfEvent s => ids.Contains(s.Entity),
                    CastEvent c => ids.Contains(c.Actor) || (c.Skill / 10_000 is 1528 or 1715 or 1539),
                    _ => false,
                };
                if (!mine) continue;
                if (e is HitEvent hit)
                {
                    var key = hit.Actor;
                    hits[key] = hits.GetValueOrDefault(key) + 1;
                    if (hits[key] > 6) continue;
                }
                w.WriteLine($"{T(e.TimeMs)} {e}");
            }
        };
        CaptureFileSource.ReadAll(args[0], raw => session.OnPacket(raw, "replay"));
        foreach (var (k, v) in hits) w.WriteLine($"actor {k}: {v} hits");
        return 0;
    }

    private static string T(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().ToString("HH:mm:ss.fff");
}
