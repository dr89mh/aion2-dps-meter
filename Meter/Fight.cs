using DpsMeter.Protocol;

namespace DpsMeter.Meter;

public enum Mode { Damage, Healing, Taken }

/// <summary>Totals of one skill (a class skill family, or one exact code) of one combatant, direct hits and ticks apart.</summary>
public sealed class SkillStats(uint code, bool periodic)
{
    public uint Code { get; } = code;
    public bool Periodic { get; } = periodic;
    public long Total;
    public long Max;
    public long Min = long.MaxValue;
    public int Hits, Crits, Back, Front, Perfect, Double, Multi, Misses, Blocks, Parries;

    public void Add(long amount, HitFlags f)
    {
        if ((f & HitFlags.Miss) != 0)
        {
            Misses++;
            return;
        }
        Total += amount;
        Hits++;
        if (amount > Max) Max = amount;
        if (amount < Min) Min = amount;
        if ((f & HitFlags.Crit) != 0) Crits++;
        if ((f & HitFlags.Back) != 0) Back++;
        if ((f & HitFlags.Front) != 0) Front++;
        if ((f & HitFlags.Perfect) != 0) Perfect++;
        if ((f & HitFlags.Double) != 0) Double++;
        if ((f & HitFlags.Multi) != 0) Multi++;
        if ((f & HitFlags.Block) != 0) Blocks++;
        if ((f & HitFlags.Parry) != 0) Parries++;
    }

    public void Merge(SkillStats o)
    {
        Total += o.Total;
        Max = Math.Max(Max, o.Max);
        Min = Math.Min(Min, o.Min);
        Hits += o.Hits;
        Crits += o.Crits;
        Back += o.Back;
        Front += o.Front;
        Perfect += o.Perfect;
        Double += o.Double;
        Multi += o.Multi;
        Misses += o.Misses;
        Blocks += o.Blocks;
        Parries += o.Parries;
    }
}

/// <summary>One player of a fight (summons included).</summary>
public sealed class Combatant(uint entity)
{
    public uint Entity { get; } = entity;
    public string? Name;
    public int Class;
    public bool IsSelf;
    public long Damage, Healing, Taken;
    public int Deaths;
    public readonly Dictionary<(uint, bool), SkillStats> Dealt = new();
    public readonly Dictionary<(uint, bool), SkillStats> Healed = new();
    public readonly Dictionary<(uint, bool), SkillStats> Received = new();
    /// <summary>Amount per second of the fight, by mode.</summary>
    public readonly List<long>[] PerSecond = [new(), new(), new()];

    /// <summary>Takes over everything of <paramref name="o"/> (a summon that was taken for a player).</summary>
    public void Absorb(Combatant o)
    {
        Damage += o.Damage;
        Healing += o.Healing;
        Taken += o.Taken;
        Deaths += o.Deaths;
        for (var k = 0; k < 3; k++)
        {
            var dst = Table((Mode)k);
            foreach (var (key, s) in o.Table((Mode)k))
            {
                if (!dst.TryGetValue(key, out var d)) dst[key] = d = new SkillStats(s.Code, s.Periodic);
                d.Merge(s);
            }
            var series = PerSecond[k];
            var mine = o.PerSecond[k];
            while (series.Count < mine.Count) series.Add(0);
            for (var i = 0; i < mine.Count; i++) series[i] += mine[i];
        }
    }

    public long Value(Mode m) => m switch { Mode.Damage => Damage, Mode.Healing => Healing, _ => Taken };

    public Dictionary<(uint, bool), SkillStats> Table(Mode m) => m switch { Mode.Damage => Dealt, Mode.Healing => Healed, _ => Received };

    public void Add(Mode m, uint skill, long amount, HitFlags f, int second)
    {
        var key = (m == Mode.Taken ? skill : Skills.Family(skill), (f & (HitFlags.Tick | HitFlags.HealTick)) != 0);
        var table = Table(m);
        if (!table.TryGetValue(key, out var s)) table[key] = s = new SkillStats(key.Item1, key.Item2);
        s.Add(amount, f);
        if (amount <= 0) return;
        switch (m)
        {
            case Mode.Damage: Damage += amount; break;
            case Mode.Healing: Healing += amount; break;
            default: Taken += amount; break;
        }
        var series = PerSecond[(int)m];
        while (series.Count <= second) series.Add(0);
        series[second] += amount;
    }
}

/// <summary>An enemy of a fight.</summary>
public sealed class Enemy(uint entity)
{
    public uint Entity { get; } = entity;
    public uint Code;
    public string? Name;
    public bool Boss;
    public long MaxHp;
    public long Hp = -1;
    /// <summary>Damage the counted players dealt to it.</summary>
    public long Damage;
    public bool Dead;
    public long LastMs;
}

/// <summary>One fight: from the first counted hit to the last; ends when every enemy is dead or it goes quiet.</summary>
public sealed class Fight(int id, long startMs, uint zone)
{
    public int Id { get; } = id;
    public long StartMs { get; } = startMs;
    public uint Zone { get; } = zone;
    public long LastMs = startMs;
    public long LastKillMs;
    public bool Active = true;
    public readonly Dictionary<uint, Combatant> Players = new();
    public readonly Dictionary<uint, Enemy> Enemies = new();

    public bool HasBoss => Enemies.Values.Any(e => e.Boss);

    public long DurationMs(long now) => Math.Max(1000, (Active ? Math.Max(now, LastMs) : LastMs) - StartMs);

    public int Second(long t) => (int)Math.Clamp((t - StartMs) / 1000, 0, 36_000);

    public string Title(string fallback)
    {
        var bosses = Enemies.Values.Where(e => e.Boss && e.Name is not null).OrderByDescending(e => e.Damage)
            .Select(e => e.Name!).Distinct().ToList();
        if (bosses.Count > 0) return string.Join(" / ", bosses.Take(2)) + (bosses.Count > 2 ? " …" : "");
        var named = Enemies.Values.Where(e => e.Name is not null && e.Damage > 0).OrderByDescending(e => e.Damage).ToList();
        if (named.Count == 0) return fallback;
        var others = named.Select(e => e.Name).Distinct().Count() - 1;
        return named[0].Name + (others > 0 ? $" +{others}" : "");
    }
}
