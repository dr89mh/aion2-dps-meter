using CubeRadar.Core.Protocol;
using DpsMeter.Protocol;

namespace DpsMeter.Meter;

/// <summary>Who is who in the current zone: the user, players (name, class, faction), summons and their owners, NPCs.
/// Entity ids are only valid inside a zone; NPCs and summons are forgotten on a zone change, players are kept (the same
/// character keeps its id across zones, HamMeter §6.10) and renamed when a different name arrives.</summary>
public sealed class Entities
{
    public sealed class Player
    {
        public string? Name;
        public int Class;
        public PlayerFaction Faction;
    }

    public sealed class Npc
    {
        public uint Code;
        public long MaxHp;
        public long Hp = -1;
    }

    private readonly Dictionary<uint, Player> _players = new();
    private readonly Dictionary<string, uint> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, uint> _owner = new();
    private readonly Dictionary<uint, string> _ownerName = new();
    private readonly HashSet<uint> _summons = new();
    private readonly Dictionary<uint, Npc> _npcs = new();
    private readonly Dictionary<uint, long> _active = new();
    private readonly HashSet<uint> _effects = new();
    private readonly Dictionary<uint, (uint Player, long Ms)> _lastCast = new();

    /// <summary>A lingering skill effect (Bitterkalter Wind, Heilige Aura, traps …) shows up as a new entity using the
    /// skill family its player cast just before.</summary>
    private const long EffectWindowMs = 2_000;

    /// <summary>Effect ids are recycled: an id quiet for this long can be someone else's effect now.</summary>
    private const long StaleMs = 8_000;

    /// <summary>Marks the entity active; true when it had been quiet (or never seen).</summary>
    private bool Touch(uint entity, long ms)
    {
        var stale = !_active.TryGetValue(entity, out var last) || ms - last > StaleMs;
        _active[entity] = ms;
        if (stale && _effects.Remove(entity))
        {
            _summons.Remove(entity);
            _owner.Remove(entity);
        }
        return stale;
    }

    public uint Self { get; private set; }
    public uint Zone { get; private set; }

    public IReadOnlyDictionary<uint, Player> Players => _players;

    public void SetZone(uint zone)
    {
        if (zone == Zone) return;
        Zone = zone;
        _npcs.Clear();
        _owner.Clear();
        _ownerName.Clear();
        _summons.Clear();
        _active.Clear();
        _effects.Clear();
        _lastCast.Clear();
    }

    public void SetSelf(uint entity)
    {
        Self = entity;
        PlayerOf(entity);
    }

    public void Named(uint entity, string name, PlayerFaction faction)
    {
        if (name.Length == 0 || name.All(char.IsDigit)) return;
        var p = PlayerOf(entity);
        if (p.Name is not null && p.Name != name) p.Class = 0; // id taken over by another character
        p.Name = name;
        if (faction != PlayerFaction.Unknown) p.Faction = faction;
        _byName[name] = entity;
        _npcs.Remove(entity);
        _summons.Remove(entity);
    }

    public void Spawned(SpawnEvent s)
    {
        Touch(s.Entity, s.TimeMs);
        _effects.Remove(s.Entity);
        var ownedByPlayer = s.Owner != s.Entity && IsPlayer(s.Owner) ||
                            s.OwnerName is { } on && _byName.TryGetValue(on, out var byName) && byName != s.Entity;
        if (s.Summonish || ownedByPlayer)
        {
            _summons.Add(s.Entity);
            _npcs.Remove(s.Entity);
            if (_players.TryGetValue(s.Entity, out var was) && was.Name is null) _players.Remove(s.Entity); // its hits came first
            if (s.Owner != 0 && s.Owner != s.Entity) _owner[s.Entity] = s.Owner;
            if (s.OwnerName is { Length: > 0 } n) _ownerName[s.Entity] = n;
            return;
        }
        if (s.NpcCode == 0) return;
        if (_players.ContainsKey(s.Entity) && s.Entity != Self) _players.Remove(s.Entity); // id reused by a monster
        if (!_npcs.TryGetValue(s.Entity, out var npc)) _npcs[s.Entity] = npc = new Npc();
        npc.Code = s.NpcCode;
        if (s.HpMax > 0) npc.MaxHp = s.HpMax;
        npc.Hp = -1;
    }

    public void Hp(uint entity, long hp)
    {
        if (_npcs.TryGetValue(entity, out var npc)) npc.Hp = hp;
    }

    /// <summary><c>02 38</c>: a known player's cast is remembered per skill family; a new entity casting the same family
    /// right after it is that player's lingering effect. Any other unknown caster of a class skill is a player.</summary>
    public void Cast(CastEvent c)
    {
        var stale = Touch(c.Actor, c.TimeMs);
        var cls = Skills.ClassOf(c.Skill);
        if (cls is 0 or ClassInfo.Spirit || _npcs.ContainsKey(c.Actor) || _summons.Contains(c.Actor)) return;
        var family = Skills.Family(c.Skill);
        if (stale && Name(c.Actor) is null && EffectOf(family, c.Actor, c.TimeMs)) return;
        PlayerOf(c.Actor).Class = cls;
        _lastCast[family] = (c.Actor, c.TimeMs);
    }

    private bool EffectOf(uint family, uint actor, long ms)
    {
        if (!_lastCast.TryGetValue(family, out var cast) || cast.Player == actor || ms - cast.Ms > EffectWindowMs ||
            !IsPlayer(cast.Player)) return false;
        _summons.Add(actor);
        _effects.Add(actor);
        _owner[actor] = cast.Player;
        if (_players.TryGetValue(actor, out var was) && was.Name is null) _players.Remove(actor);
        return true;
    }

    /// <summary>The player a summon belongs to by its owner link (no guessing); null for anything else.</summary>
    public uint? OwnerOf(uint summon)
    {
        if (!_summons.Contains(summon)) return null;
        var at = summon;
        for (var i = 0; i < 4; i++)
        {
            if (_owner.TryGetValue(at, out var next))
            {
                if (IsPlayer(next)) return next;
                at = next;
                continue;
            }
            if (_ownerName.TryGetValue(at, out var name) && _byName.TryGetValue(name, out var byName) && IsPlayer(byName))
                return byName;
            break;
        }
        return null;
    }

    public Npc? NpcOf(uint entity) => _npcs.GetValueOrDefault(entity);

    public bool IsSummon(uint entity) => _summons.Contains(entity);

    public bool IsPlayer(uint entity) => _players.ContainsKey(entity) && !_summons.Contains(entity) && !_npcs.ContainsKey(entity);

    public Player? PlayerInfo(uint entity) => IsPlayer(entity) ? _players[entity] : null;

    public string? Name(uint entity) => _players.TryGetValue(entity, out var p) ? p.Name : null;

    public int Class(uint entity) => _players.TryGetValue(entity, out var p) ? p.Class : 0;

    /// <summary>The player behind an action: summons resolve to their owner (followed a few steps, a summon can have a
    /// summon as owner), otherwise a player skill makes the actor a player. Null: not a player.</summary>
    public uint? Source(uint actor, uint skill, long ms, Func<int, uint?> uniqueOfClass)
    {
        var stale = Touch(actor, ms);
        if (_summons.Contains(actor))
            return Skills.IsPlayerSkill(skill) ? SummonOwner(actor, skill, uniqueOfClass) : null; // an enemy's effect
        if (_npcs.ContainsKey(actor) || !Skills.IsPlayerSkill(skill)) return null;
        var cls = Skills.ClassOf(skill);
        if (cls is not (0 or ClassInfo.Spirit) && stale && Name(actor) is null && EffectOf(Skills.Family(skill), actor, ms))
            return SummonOwner(actor, skill, uniqueOfClass);
        if (cls == ClassInfo.Spirit)
        {
            // an Elementalist spirit whose spawn we missed: a summon from now on
            _summons.Add(actor);
            return SummonOwner(actor, skill, uniqueOfClass);
        }
        if (cls == 0)
        {
            // theostone / pet skill: only for an actor already known as a player
            return _players.ContainsKey(actor) ? actor : null;
        }
        var p = PlayerOf(actor);
        p.Class = cls;
        return actor;
    }

    private uint? SummonOwner(uint actor, uint skill, Func<int, uint?> uniqueOfClass)
    {
        var at = actor;
        for (var i = 0; i < 4; i++)
        {
            if (_owner.TryGetValue(at, out var next))
            {
                if (IsPlayer(next)) return next;
                if (!_summons.Contains(next)) break;
                at = next;
                continue;
            }
            if (_ownerName.TryGetValue(at, out var name) && _byName.TryGetValue(name, out var byName) && IsPlayer(byName))
            {
                _owner[at] = byName;
                return byName;
            }
            break;
        }
        // No owner link: a class skill tells the class; if exactly one such player is around, it is theirs.
        var cls = Skills.ClassOf(skill);
        if (cls == ClassInfo.Spirit) cls = 16;
        if (cls != 0 && uniqueOfClass(cls) is { } guess)
        {
            _owner[actor] = guess;
            return guess;
        }
        return null;
    }

    private Player PlayerOf(uint entity)
    {
        if (!_players.TryGetValue(entity, out var p)) _players[entity] = p = new Player();
        return p;
    }
}
