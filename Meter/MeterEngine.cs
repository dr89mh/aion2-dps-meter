using CubeRadar.Core.Protocol;
using DpsMeter.Protocol;

namespace DpsMeter.Meter;

public sealed record FightInfo(int Id, string Title, string Zone, long StartMs, long DurationMs, bool Active, bool Boss,
    long Damage, double? BossHp, int Players);

public sealed record RowView(uint Entity, string Name, int Class, bool IsSelf, long Value, double PerSec, double Share, int Deaths);

public sealed record FightView(FightInfo Info, Mode Mode, IReadOnlyList<RowView> Rows, long Total, double PerSec);

public sealed record SkillRow(uint Code, string Name, bool Periodic, long Total, double Share, double PerSec, int Hits,
    int Crits, int Back, int Front, int Perfect, int Double, int Multi, int Misses, int Blocks, int Parries, long Max, long Min)
{
    public double Avg => Hits == 0 ? 0 : Total / (double)Hits;
    public int Attempts => Hits + Misses;
    public double Rate(int n) => Hits == 0 ? 0 : n / (double)Hits;
}

public sealed record DetailView(FightInfo Fight, Mode Mode, uint Entity, string Name, int Class, bool IsSelf, long Total,
    double PerSec, double Share, long Damage, long Healing, long Taken, int Deaths, SkillRow Sum, IReadOnlyList<SkillRow> Skills,
    double[] Series, IReadOnlyList<RowView> Ranking);

/// <summary>
/// Game events in, fights out. Counted are the user (with summons) always, every player inside an instance (dungeon or
/// raid: the party), and in the open world other players only on enemies the user's fight already has.
/// A fight starts with the first counted hit (dealt or taken), a boss always gets a fight of its own, and it ends a few
/// seconds after its enemies (or its bosses) are dead, or when it goes quiet. Thread-safe: capture thread writes, UI reads.
/// </summary>
public sealed class MeterEngine(GameData data)
{
    /// <summary>View ids: the running (or last) fight, and all fights since the reset together.</summary>
    public const int Follow = 0, Total = -1;

    private readonly object _gate = new();
    private readonly Entities _e = new();
    private readonly List<Fight> _fights = new();
    private readonly Dictionary<uint, long> _dead = new();
    private Fight? _cur;
    private int _nextId = 1;
    private long _now;

    /// <summary>A fight without bosses ends after this long without a counted hit.</summary>
    public long QuietMs { get; set; } = 8_000;

    /// <summary>A boss fight ends after this long without a counted hit (a wipe; phases without damage stay one fight).</summary>
    public long BossQuietMs { get; set; } = 25_000;

    /// <summary>After the last enemy (or boss) died the fight ends this much later unless the next pull comes first.</summary>
    public long KillGraceMs { get; set; } = 3_000;

    public int MaxFights { get; set; } = 60;

    /// <summary>Diagnostic counters (amounts per decision), for the text report.</summary>
    public Dictionary<string, long> Counters { get; } = new();

    /// <summary>Counters also per skill and actor (report).</summary>
    public bool Detailed { get; set; }

    private HitEvent? _hit;

    private void Count(string key, long amount)
    {
        if (Detailed && _hit is { } h && !key.StartsWith("counted"))
        {
            var k = $"{key} · {data.SkillName(h.Skill) ?? "?"} [{h.Skill}] actor {h.Actor}{(_e.IsSummon(h.Actor) ? "s" : "")}";
            Counters[k] = Counters.GetValueOrDefault(k) + amount;
        }
        Counters[key] = Counters.GetValueOrDefault(key) + amount;
        Counters[key + " #"] = Counters.GetValueOrDefault(key + " #") + 1;
    }

    public uint Zone { get { lock (_gate) return _e.Zone; } }

    public bool SelfKnown { get { lock (_gate) return _e.Self != 0; } }

    public bool InFight { get { lock (_gate) return _cur is { Active: true }; } }

    public void OnEvent(GameEvent ev)
    {
        lock (_gate)
        {
            if (ev.TimeMs > _now) _now = ev.TimeMs;
            switch (ev)
            {
                case ZoneEvent z when z.Zone != _e.Zone:
                    End();
                    _e.SetZone(z.Zone);
                    _dead.Clear();
                    break;
                case SelfEvent s:
                    _e.SetSelf(s.Entity);
                    break;
                case PlayerEvent p:
                    _e.Named(p.Entity, p.Name, p.Faction);
                    break;
                case SpawnEvent s:
                    _e.Spawned(s);
                    _dead.Remove(s.Entity);
                    MergeSummon(s.Entity);
                    break;
                case CastEvent c:
                    _e.Cast(c);
                    break;
                case HpEvent h:
                    _e.Hp(h.Entity, h.Hp);
                    if (_cur is not null && _cur.Enemies.TryGetValue(h.Entity, out var en)) en.Hp = h.Hp;
                    break;
                case KillEvent k:
                    Kill(k);
                    break;
                case HitEvent h:
                    Hit(h);
                    break;
            }
            CheckEnd(_now);
        }
    }

    /// <summary>Clock tick without packets (live: wall clock), ends a fight that went quiet.</summary>
    public void Tick(long nowMs)
    {
        lock (_gate)
        {
            if (nowMs > _now) _now = nowMs;
            CheckEnd(_now);
        }
    }

    /// <summary>Forget every fight (the entities stay known).</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _cur = null;
            _fights.Clear();
        }
    }

    /// <summary>End the running fight now (the next hit starts a new one).</summary>
    public void Split()
    {
        lock (_gate) End();
    }

    // ----- events -------------------------------------------------------------------------------------------------

    private void Kill(KillEvent k)
    {
        if (k.Killer != 0 && k.KillerName is { } name) _e.Named(k.Killer, name, PlayerFaction.Unknown);
        if (_e.IsPlayer(k.Entity))
        {
            if (_cur is { Active: true } f && f.Players.TryGetValue(k.Entity, out var c)) c.Deaths++;
            return;
        }
        _dead[k.Entity] = k.TimeMs;
        if (_cur is { Active: true } fight && fight.Enemies.TryGetValue(k.Entity, out var en))
        {
            en.Dead = true;
            en.Hp = 0;
            fight.LastKillMs = k.TimeMs;
        }
    }

    private void Hit(HitEvent h)
    {
        _hit = h;
        var src = _e.Source(h.Actor, h.Skill, h.TimeMs, UniqueOfClass);
        if (h.Actor == h.Target)
        {
            // self-casts: only heals count
            if (src is { } self && ((h.Flags & HitFlags.HealTick) != 0 || data.IsHeal(h.Skill))) Heal(self, h);
            else Count("ignored: self-cast", h.Amount);
            return;
        }
        var targetPlayer = _e.IsPlayer(h.Target);
        if ((h.Flags & HitFlags.HealTick) != 0)
        {
            if (src is { } healer && _e.NpcOf(h.Target) is null) Heal(healer, h);
            return;
        }
        if (src is { } s)
        {
            if (Skills.IsSpiritLink(h.Skill))
            {
                Count("ignored: spirit link", h.Amount);
                return;
            }
            if (data.IsHeal(h.Skill))
            {
                if (_e.NpcOf(h.Target) is null) Heal(s, h);
                return;
            }
            if (_e.IsSummon(h.Target))
            {
                Count("ignored: player on summon", h.Amount);
                return;
            }
            if (targetPlayer)
            {
                // no friendly fire: a player skill on a player is PvP only when the factions are known to differ
                if (!Hostile(s, h.Target))
                {
                    Count("ignored: player on player", h.Amount);
                    return;
                }
                if (Friendly(s)) Damage(s, h, pvp: true);
                else if (Friendly(h.Target)) Taken(h.Target, h);
                return;
            }
            Damage(s, h, pvp: false);
            return;
        }
        // not a player's action: a monster hitting someone
        if (targetPlayer && !Skills.IsPlayerSkill(h.Skill)) Taken(h.Target, h);
        else if (_e.IsSummon(h.Actor) && Skills.IsPlayerSkill(h.Skill)) Count("lost: summon without owner", h.Amount);
        else if (Skills.IsPlayerSkill(h.Skill)) Count("lost: player skill, actor unknown", h.Amount);
        else if (!targetPlayer && !_e.IsSummon(h.Target)) Count("ignored: npc on npc/unknown", h.Amount);
    }

    private bool Instance => GameData.IsInstance(_e.Zone);

    private void Damage(uint src, HitEvent h, bool pvp)
    {
        var running = _cur is { Active: true } ? _cur : null;
        var known = running is not null && running.Enemies.ContainsKey(h.Target);
        if (src != _e.Self && !Instance && !known)
        {
            Count("ignored: other player, not our enemy", h.Amount);
            return;
        }
        // ticks on a dead enemy, and hits on it for 10 s, never start or revive anything
        if (!known && _dead.TryGetValue(h.Target, out var died) && (h.Periodic || h.TimeMs - died < 10_000))
        {
            Count("ignored: hit on dead enemy", h.Amount);
            return;
        }
        Count(h.Periodic ? "counted: damage ticks" : "counted: damage hits", h.Amount);

        var npc = _e.NpcOf(h.Target);
        var boss = npc is not null && data.IsBoss(npc.Code);
        if (running is not null && boss && !known && !running.HasBoss && running.Players.Count > 0)
        {
            End(); // a boss always gets a fight of its own
            running = null;
        }
        var f = running ?? Start(h.TimeMs);
        var en = EnemyOf(f, h.Target, npc, pvp);
        if (en.Dead && !h.Periodic && h.TimeMs - en.LastMs > 10_000) en.Dead = false; // a new monster on the same id
        en.LastMs = h.TimeMs;
        en.Damage += h.Amount;
        CombatantOf(f, src).Add(Mode.Damage, h.Skill, h.Amount, h.Flags, f.Second(h.TimeMs));
        if (!en.Dead && h.TimeMs > f.LastMs) f.LastMs = h.TimeMs;
    }

    private void Taken(uint victim, HitEvent h)
    {
        var running = _cur is { Active: true } ? _cur : null;
        if (victim != _e.Self && !Instance && running?.Players.ContainsKey(victim) != true) return;
        if (_dead.ContainsKey(h.Actor) && running?.Enemies.ContainsKey(h.Actor) != true) return;
        var f = running ?? Start(h.TimeMs);
        var en = EnemyOf(f, h.Actor, _e.NpcOf(h.Actor), pvp: _e.IsPlayer(h.Actor));
        en.LastMs = h.TimeMs;
        CombatantOf(f, victim).Add(Mode.Taken, h.Skill, h.Amount, h.Flags, f.Second(h.TimeMs));
        if (h.Amount > 0 && h.TimeMs > f.LastMs) f.LastMs = h.TimeMs;
    }

    private void Heal(uint healer, HitEvent h)
    {
        if (_cur is not { Active: true } f)
        {
            Count("ignored: heal outside a fight", h.Amount);
            return; // heals never start a fight
        }
        Count("counted: heal", h.Amount);
        if (healer != _e.Self && !Instance && !f.Players.ContainsKey(healer)) return;
        CombatantOf(f, healer).Add(Mode.Healing, h.Skill, h.Amount, h.Flags, f.Second(h.TimeMs));
    }

    private bool Friendly(uint player) =>
        player == _e.Self || _e.PlayerInfo(player) is { Faction: not PlayerFaction.Unknown } p &&
        _e.PlayerInfo(_e.Self) is { Faction: not PlayerFaction.Unknown } me && p.Faction == me.Faction;

    private bool Hostile(uint a, uint b) =>
        _e.PlayerInfo(a) is { Faction: not PlayerFaction.Unknown } pa && _e.PlayerInfo(b) is { Faction: not PlayerFaction.Unknown } pb &&
        pa.Faction != pb.Faction;

    /// <summary>The one player of a class in the running fight (for a summon whose owner link is missing).</summary>
    private uint? UniqueOfClass(int cls)
    {
        if (_cur is not { Active: true } f) return _e.Self != 0 && _e.Class(_e.Self) == cls ? _e.Self : null;
        uint? found = null;
        foreach (var c in f.Players.Values)
        {
            if (_e.Class(c.Entity) != cls) continue;
            if (found is not null) return null;
            found = c.Entity;
        }
        return found;
    }

    /// <summary>A summon whose hits came before its spawn showed whose it is: its numbers go to its player.</summary>
    private void MergeSummon(uint entity)
    {
        if (_cur is null || !_cur.Players.TryGetValue(entity, out var orphan) || _e.OwnerOf(entity) is not { } owner) return;
        _cur.Players.Remove(entity);
        CombatantOf(_cur, owner).Absorb(orphan);
        Count("merged: summon after its spawn", orphan.Damage);
    }

    // ----- fights ---------------------------------------------------------------------------------------------------

    private Fight Start(long t)
    {
        var f = new Fight(_nextId++, t, _e.Zone);
        _cur = f;
        _fights.Add(f);
        while (_fights.Count > MaxFights) _fights.RemoveAt(0);
        return f;
    }

    private void End()
    {
        if (_cur is not { Active: true } f) return;
        f.Active = false;
        foreach (var c in f.Players.Values)
        {
            c.Name = _e.Name(c.Entity) ?? c.Name;
            if (_e.Class(c.Entity) is > 0 and var cls) c.Class = cls;
        }
        _cur = null;
    }

    private void CheckEnd(long now)
    {
        if (_cur is not { Active: true } f) return;
        var bosses = f.Enemies.Values.Where(e => e.Boss).ToList();
        var done = bosses.Count > 0 ? bosses.All(e => e.Dead) : f.Enemies.Count > 0 && f.Enemies.Values.All(e => e.Dead);
        if (done && now - Math.Max(f.LastKillMs, f.LastMs) >= KillGraceMs) End();
        else if (now - f.LastMs > (bosses.Count > 0 ? BossQuietMs : QuietMs)) End();
    }

    private Enemy EnemyOf(Fight f, uint entity, Entities.Npc? npc, bool pvp)
    {
        if (!f.Enemies.TryGetValue(entity, out var en))
        {
            f.Enemies[entity] = en = new Enemy(entity);
            if (npc is not null)
            {
                en.Code = npc.Code;
                en.Name = data.NpcName(npc.Code);
                en.Boss = data.IsBoss(npc.Code);
                en.MaxHp = npc.MaxHp;
                en.Hp = npc.Hp;
            }
            else if (pvp) en.Name = _e.Name(entity);
        }
        return en;
    }

    private Combatant CombatantOf(Fight f, uint entity)
    {
        if (!f.Players.TryGetValue(entity, out var c))
        {
            f.Players[entity] = c = new Combatant(entity) { IsSelf = entity == _e.Self };
        }
        if (_e.Class(entity) is > 0 and var cls) c.Class = cls;
        if (entity == _e.Self) c.IsSelf = true;
        return c;
    }

    // ----- views (UI thread) -----------------------------------------------------------------------------------------

    public List<FightInfo> Fights()
    {
        lock (_gate)
        {
            var list = new List<FightInfo>(_fights.Count);
            for (var i = _fights.Count - 1; i >= 0; i--) list.Add(Info(_fights[i]));
            return list;
        }
    }

    public FightView? View(int id, Mode mode)
    {
        lock (_gate)
        {
            var f = Resolve(id);
            return f is null ? null : ViewOf(f, mode);
        }
    }

    public DetailView? Detail(int id, uint entity, string? name, Mode mode)
    {
        lock (_gate)
        {
            var f = Resolve(id);
            if (f is null) return null;
            var c = f.Players.GetValueOrDefault(entity);
            if (c is null && name is not null) c = f.Players.Values.FirstOrDefault(p => NameOf(p) == name);
            if (c is null) return null;
            var view = ViewOf(f, mode);
            var info = view.Info;
            var seconds = Math.Max(1.0, info.DurationMs / 1000.0);
            var total = c.Value(mode);
            var rows = new List<SkillRow>();
            var sum = new SkillStats(0, false);
            foreach (var s in c.Table(mode).Values)
            {
                sum.Merge(s);
                rows.Add(Row(s, Label(s.Code, s.Periodic, mode), total, seconds));
            }
            rows.Sort((a, b) => b.Total.CompareTo(a.Total));
            var series = c.PerSecond[(int)mode];
            var length = (int)Math.Max(1, Math.Ceiling(info.DurationMs / 1000.0));
            var arr = new double[Math.Max(length, series.Count)];
            for (var i = 0; i < series.Count; i++) arr[i] = series[i];
            var share = view.Total > 0 ? total / (double)view.Total : 0;
            return new DetailView(info, mode, c.Entity, NameOf(c), ClassOf(c), c.IsSelf, total, total / seconds, share, c.Damage,
                c.Healing, c.Taken, c.Deaths, Row(sum, "", total, seconds), rows, arr, view.Rows);
        }
    }

    private static SkillRow Row(SkillStats s, string name, long total, double seconds) =>
        new(s.Code, name, s.Periodic, s.Total, total > 0 ? s.Total / (double)total : 0, s.Total / seconds, s.Hits, s.Crits,
            s.Back, s.Front, s.Perfect, s.Double, s.Multi, s.Misses, s.Blocks, s.Parries, s.Max, s.Min == long.MaxValue ? 0 : s.Min);

    private string Label(uint code, bool periodic, Mode mode)
    {
        var name = data.SkillName(code)
                   ?? (Skills.IsTheostone(code) ? L.T("Theostone #", "Theostein #") + code % 1000
                       : Skills.IsNpcSkill(code) ? L.T("Attack", "Angriff") + " #" + code % 1000
                       : "Skill #" + code);
        return periodic ? name + (mode == Mode.Healing ? " (HoT)" : " (DoT)") : name;
    }

    private Fight? Resolve(int id) => id switch
    {
        Follow => _cur ?? (_fights.Count > 0 ? _fights[^1] : null),
        Total => _fights.Count > 0 ? Merged() : null,
        _ => _fights.FirstOrDefault(f => f.Id == id),
    };

    private FightView ViewOf(Fight f, Mode mode)
    {
        var info = Info(f);
        var seconds = Math.Max(1.0, info.DurationMs / 1000.0);
        var players = f.Players.Values.Where(c => c.Value(mode) > 0 || (mode == Mode.Damage && c.Dealt.Count > 0)).ToList();
        var total = players.Sum(c => c.Value(mode));
        var rows = new List<RowView>();
        long otherValue = 0;
        var others = 0;
        foreach (var c in players)
        {
            var share = total > 0 ? c.Value(mode) / (double)total : 0;
            // skill effects whose player is unknown (their cast was not in the stream): one line instead of many
            if (!c.IsSelf && c.Name is null && _e.Name(c.Entity) is null && share < 0.01 && f.Players.Count > 2)
            {
                otherValue += c.Value(mode);
                others++;
                continue;
            }
            rows.Add(new RowView(c.Entity, NameOf(c), ClassOf(c), c.IsSelf, c.Value(mode), c.Value(mode) / seconds, share, c.Deaths));
        }
        rows.Sort((a, b) => b.Value != a.Value ? b.Value.CompareTo(a.Value) : string.Compare(a.Name, b.Name, StringComparison.CurrentCulture));
        if (others > 0)
            rows.Add(new RowView(0, L.T($"Others ({others})", $"Sonstige ({others})"), 0, false, otherValue, otherValue / seconds,
                total > 0 ? otherValue / (double)total : 0, 0));
        return new FightView(info, mode, rows, total, total / seconds);
    }

    private FightInfo Info(Fight f)
    {
        var zone = f.Id == Total ? "" : data.ZoneName(f.Zone)
            ?? (GameData.IsInstance(f.Zone) ? L.T("Instance ", "Instanz ") + f.Zone : L.T("Open world", "Offene Welt"));
        var title = f.Id == Total
            ? L.T("All fights", "Alle Kämpfe") + $" ({_fights.Count})"
            : f.Title(L.T("Fight ", "Kampf ") + f.Id);
        double? bossHp = null;
        var boss = f.Enemies.Values.Where(e => e.Boss && e.MaxHp > 0).OrderByDescending(e => e.Damage).FirstOrDefault();
        if (boss is not null) bossHp = boss.Dead ? 0 : boss.Hp >= 0 ? Math.Clamp(boss.Hp / (double)boss.MaxHp, 0, 1) : null;
        return new FightInfo(f.Id, title, zone, f.StartMs, f.DurationMs(_now), f.Active, f.HasBoss, f.Players.Values.Sum(c => c.Damage),
            bossHp, f.Players.Count);
    }

    private string NameOf(Combatant c)
    {
        var name = c.Name ?? _e.Name(c.Entity);
        if (name is not null) return name;
        if (c.IsSelf || c.Entity == _e.Self) return L.T("You", "Du");
        return ClassInfo.Name(ClassOf(c)) + " #" + c.Entity;
    }

    private int ClassOf(Combatant c) => c.Class > 0 ? c.Class : _e.Class(c.Entity);

    /// <summary>All fights as one: players by name, durations added up, the per-second series one after another.</summary>
    private Fight Merged()
    {
        var all = new Fight(Total, 0, 0) { Active = false };
        long offset = 0;
        foreach (var f in _fights)
        {
            var duration = f.DurationMs(_now);
            foreach (var c in f.Players.Values)
            {
                var name = NameOf(c);
                var m = all.Players.Values.FirstOrDefault(p => p.Name == name);
                if (m is null)
                {
                    m = new Combatant(c.Entity) { Name = name, Class = ClassOf(c), IsSelf = c.IsSelf };
                    all.Players[c.Entity + (uint)all.Players.Count * 10_000_000u] = m;
                }
                m.IsSelf |= c.IsSelf;
                m.Damage += c.Damage;
                m.Healing += c.Healing;
                m.Taken += c.Taken;
                m.Deaths += c.Deaths;
                for (var k = 0; k < 3; k++)
                {
                    var src = c.Table((Mode)k);
                    var dst = m.Table((Mode)k);
                    foreach (var (key, s) in src)
                    {
                        if (!dst.TryGetValue(key, out var d)) dst[key] = d = new SkillStats(s.Code, s.Periodic);
                        d.Merge(s);
                    }
                    var series = m.PerSecond[k];
                    var at = (int)(offset / 1000);
                    var mine = c.PerSecond[k];
                    while (series.Count < at + mine.Count) series.Add(0);
                    for (var i = 0; i < mine.Count; i++) series[at + i] += mine[i];
                }
            }
            foreach (var en in f.Enemies.Values)
                all.Enemies[en.Entity + (uint)all.Enemies.Count * 10_000_000u] = en;
            offset += duration;
        }
        all.LastMs = offset;
        return all;
    }

    // ----- offline (report) -----------------------------------------------------------------------------------------

    /// <summary>The fights for the text report; call only when no packets arrive any more.</summary>
    public IReadOnlyList<Fight> AllFights()
    {
        lock (_gate)
        {
            End();
            return _fights.ToList();
        }
    }

    public string PlayerName(Combatant c)
    {
        lock (_gate) return NameOf(c);
    }

    public string SkillLabel(uint code, bool periodic, Mode mode) => Label(code, periodic, mode);
}
