using System.Buffers.Binary;
using CubeRadar.Core.Protocol;

namespace DpsMeter.Protocol;

// Decoded game events the meter needs. Layouts: HamMeter docs/protocol.md (GPL-3.0, confirmed there against the game's
// own combat analysis), cyberbadger6969/aion2-dps-meter PacketParser (GPL-3.0) and the Raid Announcer's CombatDecoder.

public abstract record GameEvent(long TimeMs);

/// <summary><c>01 40</c>: the user entered a zone (open world 1010/1110, dungeons/raids 2xxxxx/6xxxxx).</summary>
public sealed record ZoneEvent(long TimeMs, uint Zone) : GameEvent(TimeMs);

/// <summary><c>4A 36</c> / <c>33 36</c>: this entity is the user.</summary>
public sealed record SelfEvent(long TimeMs, uint Entity) : GameEvent(TimeMs);

/// <summary><c>33 36</c> / <c>45 36</c>: a player's name (and faction when the record says so).</summary>
public sealed record PlayerEvent(long TimeMs, uint Entity, string Name, PlayerFaction Faction) : GameEvent(TimeMs);

/// <summary><c>41 36</c>: an NPC, summon or effect appeared. <see cref="Owner"/> / <see cref="OwnerName"/>: the player
/// behind a summon when the record names one.</summary>
public sealed record SpawnEvent(long TimeMs, uint Entity, byte Kind, uint NpcCode, long HpMax, uint Owner, string? OwnerName)
    : GameEvent(TimeMs)
{
    /// <summary>Spirits, pets, totems and lingering skill effects: never a player, never an enemy of the party.</summary>
    public bool Summonish => Kind is 0x5F or 0x1C or 0x1F or 0x1D or 0x5D;
}

/// <summary><c>02 38</c>: skill use announcement (start of a cast).</summary>
public sealed record CastEvent(long TimeMs, uint Actor, uint Skill, uint Target) : GameEvent(TimeMs);

[Flags]
public enum HitFlags : ushort
{
    None = 0,
    Crit = 1,
    Back = 2,
    Front = 4,
    Perfect = 8,
    /// <summary>The game's „Wucht“ (DOUB): double damage.</summary>
    Double = 16,
    /// <summary>The game's „Mehrfachtreffer“ (MULT).</summary>
    Multi = 32,
    Block = 64,
    Parry = 128,
    /// <summary>Miss or resist: no amount.</summary>
    Miss = 256,
    /// <summary>A periodic tick (<c>05 38</c>).</summary>
    Tick = 512,
    /// <summary>A periodic heal tick (HoT).</summary>
    HealTick = 1024,
}

/// <summary><c>04 38</c> direct hit / heal (with amount, or a miss) or <c>05 38</c> periodic tick.</summary>
public sealed record HitEvent(long TimeMs, uint Actor, uint Target, uint Skill, long Amount, HitFlags Flags) : GameEvent(TimeMs)
{
    public bool Periodic => (Flags & (HitFlags.Tick | HitFlags.HealTick)) != 0;
}

/// <summary><c>00 8D</c>: remaining HP of an entity.</summary>
public sealed record HpEvent(long TimeMs, uint Entity, long Hp) : GameEvent(TimeMs);

/// <summary><c>04 8D</c>: an entity died. <see cref="KillerName"/>: the character name of the killing blow.</summary>
public sealed record KillEvent(long TimeMs, uint Entity, uint Killer, string? KillerName) : GameEvent(TimeMs);

public static class Op
{
    public const ushort Zone = 0x0140;
    public const ushort UserState = 0x4A36;
    public const ushort SelfInfo = 0x3336;
    public const ushort PlayerInfo = 0x4536;
    public const ushort Spawn = 0x4136;
    public const ushort Cast = 0x0238;
    public const ushort Hit = 0x0438;
    public const ushort Dot = 0x0538;
    public const ushort RemainHp = 0x008D;
    public const ushort Kill = 0x048D;
}

/// <summary>Turns game frames into <see cref="GameEvent"/>s. Unknown opcodes and malformed bodies are ignored.</summary>
public static class CombatDecoder
{
    private const uint MaxEntity = 9_999_999;
    private const uint MaxAmount = 99_999_999;

    public static void Decode(ushort op, ReadOnlySpan<byte> b, long t, List<GameEvent> output)
    {
        try
        {
            switch (op)
            {
                case Op.Cast: Cast(b, t, output); break;
                case Op.Hit: Hit(b, t, output); break;
                case Op.Dot: Dot(b, t, output); break;
                case Op.Spawn: Spawn(b, t, output); break;
                case Op.RemainHp: RemainHp(b, t, output); break;
                case Op.Kill: Kill(b, t, output); break;
                case Op.Zone:
                    if (b.Length >= 4) output.Add(new ZoneEvent(t, BinaryPrimitives.ReadUInt32LittleEndian(b)));
                    break;
                case Op.UserState:
                {
                    var o = 0;
                    if (Wire.TryVarint(b, ref o, out var id) && id is > 0 and <= MaxEntity) output.Add(new SelfEvent(t, id));
                    break;
                }
                case Op.SelfInfo:
                case Op.PlayerInfo:
                    if (PlayerMessages.TryParseIdentity(b, op == Op.SelfInfo, out var who) && who.EntityId <= MaxEntity)
                    {
                        if (op == Op.SelfInfo) output.Add(new SelfEvent(t, who.EntityId));
                        output.Add(new PlayerEvent(t, who.EntityId, who.Name, who.Faction));
                    }
                    break;
            }
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            // a body shorter than its layout: ignore, like every other malformed frame
        }
    }

    /// <summary>actor varint, varint, skill u32, u8, kind u8, target varint, ...</summary>
    private static void Cast(ReadOnlySpan<byte> b, long t, List<GameEvent> output)
    {
        var o = 0;
        if (!Wire.TryVarint(b, ref o, out var actor) || !Wire.TryVarint(b, ref o, out _) ||
            !Wire.TryU32(b, ref o, out var skill) || !Wire.TryU8(b, ref o, out _) || !Wire.TryU8(b, ref o, out _) ||
            !Wire.TryVarint(b, ref o, out var target)) return;
        if (actor == 0 || actor > MaxEntity || skill == 0 || skill > 299_999_999) return;
        output.Add(new CastEvent(t, actor, skill, target));
    }

    /// <summary>
    /// target varint, layout varint (≤ 255, low nibble 4..7, bit 20 = multi hit), varint, actor varint, skill u32, u8,
    /// hit type varint (2 normal, 3 crit, 1 miss, 6 resist: no more fields), detail block (nibble 6: flags u8, HP varint,
    /// direction u8; nibble 5/7: 3 bytes; nibble 4: nothing), 8 bytes, varint (power), amount varint.
    /// </summary>
    private static void Hit(ReadOnlySpan<byte> b, long t, List<GameEvent> output)
    {
        var o = 0;
        if (!Wire.TryVarint(b, ref o, out var target) || !Wire.TryVarint(b, ref o, out var layout) ||
            !Wire.TryVarint(b, ref o, out _) || !Wire.TryVarint(b, ref o, out var actor) ||
            !Wire.TryU32(b, ref o, out var skill) || !Wire.TryU8(b, ref o, out _) ||
            !Wire.TryVarint(b, ref o, out var hitType)) return;
        var nibble = layout & 0x0F;
        if (layout > 255 || nibble is < 4 or > 7) return;
        if (actor == 0 || actor > MaxEntity || target == 0 || target > MaxEntity || skill == 0 || skill > 299_999_999) return;

        var flags = (layout & 0x20) != 0 ? HitFlags.Multi : HitFlags.None;
        if (hitType is 1 or 6)
        {
            output.Add(new HitEvent(t, actor, target, skill, 0, flags | HitFlags.Miss));
            return;
        }
        if (hitType == 3) flags |= HitFlags.Crit;
        if (nibble == 6)
        {
            if (!Wire.TryU8(b, ref o, out var f) || !Wire.TryVarint(b, ref o, out _) || !Wire.TryU8(b, ref o, out var dir)) return;
            if ((f & 0x01) != 0) flags |= HitFlags.Block;
            if ((f & 0x02) != 0) flags |= HitFlags.Parry;
            if ((f & 0x04) != 0) flags |= HitFlags.Perfect;
            if ((f & 0x08) != 0) flags |= HitFlags.Double;
            if (dir == 1) flags |= HitFlags.Back;
            else if (dir == 2) flags |= HitFlags.Front;
        }
        else if (nibble != 4) o += 3;
        o += 8;
        if (o > b.Length || !Wire.TryVarint(b, ref o, out _) || !Wire.TryVarint(b, ref o, out var amount)) return;
        if (amount is 0 or > MaxAmount) return;
        output.Add(new HitEvent(t, actor, target, skill, amount, flags));
    }

    /// <summary>target varint, effect u8 (damage 02/0A, heal 01/09/0B; exact values), actor varint, varint,
    /// skill × 100 u32, amount varint</summary>
    private static void Dot(ReadOnlySpan<byte> b, long t, List<GameEvent> output)
    {
        var o = 0;
        if (!Wire.TryVarint(b, ref o, out var target) || !Wire.TryU8(b, ref o, out var effect) ||
            !Wire.TryVarint(b, ref o, out var actor) || !Wire.TryVarint(b, ref o, out _) ||
            !Wire.TryU32(b, ref o, out var raw) || !Wire.TryVarint(b, ref o, out var amount)) return;
        var flags = effect switch
        {
            0x02 or 0x0A => HitFlags.Tick,
            0x01 or 0x09 or 0x0B => HitFlags.HealTick,
            _ => HitFlags.None,
        };
        var skill = raw / 100;
        if (flags == HitFlags.None || actor == 0 || actor > MaxEntity || target == 0 || target > MaxEntity ||
            skill == 0 || amount is 0 or > MaxAmount) return;
        output.Add(new HitEvent(t, actor, target, skill, amount, flags));
    }

    private static readonly byte[] SummonBoundary = [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];
    private static readonly byte[][] SummonHeaders = [[0x07, 0x02, 0x06], [0x07, 0x02, 0x01]];

    /// <summary>entity varint, kind u8, flags u8, head at +2 (fallback +4): [name], npc u32, m1, m2, x y z heading,
    /// [12 bytes if m1 &amp; 08], 2 bytes, 01, hp varint, hp max varint. Summon owner: see <see cref="FindOwner"/>.</summary>
    private static void Spawn(ReadOnlySpan<byte> b, long t, List<GameEvent> output)
    {
        var o = 0;
        if (!Wire.TryVarint(b, ref o, out var entity) || entity == 0 || o + 2 > b.Length) return;
        var kind = b[o];
        uint npc = 0;
        long cur = 0, max = 0;
        string? name = null;
        if (!Head(b, o + 2, ref npc, ref cur, ref max, ref name)) Head(b, o + 4, ref npc, ref cur, ref max, ref name);
        var owner = FindOwner(b[o..], entity);
        output.Add(new SpawnEvent(t, entity, kind, npc, max, owner, name));
    }

    private static bool Head(ReadOnlySpan<byte> b, int o, ref uint npc, ref long cur, ref long max, ref string? name)
    {
        if (!Wire.TryU8(b, ref o, out var gate)) return false;
        string? inline = null;
        if ((gate & 1) != 0)
        {
            if (!Wire.TryU8(b, ref o, out var len) || len == 0 || o + len > b.Length) return false;
            if (len <= 36 && Wire.TryName(b.Slice(o, len), out var n)) inline = n;
            o += len;
        }
        if (!Wire.TryU32(b, ref o, out var code) || code > 0x00FF_FFFF) return false;
        if (!Wire.TryU8(b, ref o, out var m1) || (m1 & ~0x48) != 0) return false;
        if (!Wire.TryU8(b, ref o, out var m2) || m2 is not (0x02 or 0x03)) return false;
        npc = code;
        name = inline;
        o += 16; // x y z heading
        if ((m1 & 0x08) != 0) o += 12;
        o += 2;
        if (o >= b.Length || b[o] != 0x01) return true;
        o++;
        if (Wire.TryVarint(b, ref o, out var c) && Wire.TryVarint(b, ref o, out var m))
        {
            cur = c;
            max = m;
        }
        return true;
    }

    /// <summary>
    /// The player behind a summon, two known ways: after a run of eight <c>FF</c> a header <c>07 02 06</c> / <c>07 02 01</c>
    /// with the owner's entity id 3 bytes after its start as u16 (HamMeter), or the owner block
    /// <c>06 | owner u32 | legion u32 | 00 00 | server u16 | len u8 | legion name</c> (AionMeter). 0 = none found;
    /// the engine only believes an owner it knows as a player.
    /// </summary>
    private static uint FindOwner(ReadOnlySpan<byte> b, uint self)
    {
        var boundary = b.IndexOf(SummonBoundary);
        if (boundary >= 0)
        {
            var after = b[(boundary + SummonBoundary.Length)..];
            foreach (var header in SummonHeaders)
            {
                var at = after.IndexOf(header);
                if (at >= 0 && at + 5 <= after.Length)
                {
                    var owner = (uint)(after[at + 3] | after[at + 4] << 8);
                    if (owner > 1 && owner != self) return owner;
                }
            }
        }
        for (var i = 1; i + 13 <= b.Length; i++)
        {
            if (b[i - 1] != 0x06) continue;
            var parent = Wire.U32(b, i);
            if (parent is 0 or > MaxEntity || parent == self || Wire.U16(b, i + 8) != 0) continue;
            var server = Wire.U16(b, i + 10);
            if (server is < 1000 or >= 3000) continue;
            var len = b[i + 12];
            if (len is < 1 or > 48 || i + 13 + len > b.Length || !Wire.TryName(b.Slice(i + 13, len), out _)) continue;
            return parent;
        }
        return 0;
    }

    /// <summary>entity varint, 3 × varint, hp u64</summary>
    private static void RemainHp(ReadOnlySpan<byte> b, long t, List<GameEvent> output)
    {
        var o = 0;
        if (!Wire.TryVarint(b, ref o, out var entity) || !Wire.TryVarint(b, ref o, out _) ||
            !Wire.TryVarint(b, ref o, out _) || !Wire.TryVarint(b, ref o, out _) || o + 8 > b.Length) return;
        var hp = BinaryPrimitives.ReadInt64LittleEndian(b[o..]);
        if (entity == 0 || entity > MaxEntity || hp < 0 || hp > 1_000_000_000_000L) return;
        output.Add(new HpEvent(t, entity, hp));
    }

    /// <summary>entity varint, u32, killer varint, killer server u16, killer name (u8 length + UTF-8), padding</summary>
    private static void Kill(ReadOnlySpan<byte> b, long t, List<GameEvent> output)
    {
        var o = 0;
        if (!Wire.TryVarint(b, ref o, out var entity) || !Wire.TryU32(b, ref o, out _)) return;
        if (entity == 0 || entity > MaxEntity) return;
        Wire.TryVarint(b, ref o, out var killer);
        string? name = null;
        if (killer != 0 && Wire.TryU16(b, ref o, out _) && Wire.TryU8(b, ref o, out var len) && len is > 0 and <= 36 &&
            o + len <= b.Length && Wire.TryName(b.Slice(o, len), out var n))
            name = n;
        output.Add(new KillEvent(t, entity, killer > MaxEntity ? 0 : killer, name));
    }
}
