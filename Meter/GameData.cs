using System.Reflection;
using System.Text.Json;

namespace DpsMeter.Meter;

/// <summary>Skill, NPC and zone names in English and German plus the heal skill families (from the aion2-dps-meter game
/// data, see tools/make_data.py). Names follow <see cref="L.Lang"/>.</summary>
public sealed class GameData
{
    private readonly Dictionary<string, Dictionary<uint, string>> _npcNames = new();
    private readonly Dictionary<string, Dictionary<uint, string>> _skillNames = new();
    private readonly Dictionary<string, Dictionary<uint, string>> _zoneNames = new();
    private readonly Dictionary<uint, (bool Boss, uint Dungeon, bool Dummy)> _npcFlags = new();
    private readonly HashSet<uint> _healFamilies = new();

    public static GameData Load()
    {
        var data = new GameData();
        foreach (var lang in L.Languages)
        {
            var npcs = data._npcNames[lang] = new Dictionary<uint, string>();
            using (var doc = Json($"data/npcs-{lang}.json"))
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    if (!uint.TryParse(p.Name, out var id)) continue;
                    npcs[id] = p.Value.GetProperty("n").GetString() ?? "";
                    if (lang == "en")
                        data._npcFlags[id] = (p.Value.TryGetProperty("b", out _),
                            p.Value.TryGetProperty("d", out var d) && d.TryGetUInt32(out var dungeon) ? dungeon : 0,
                            p.Value.TryGetProperty("x", out _));
                }
            data._skillNames[lang] = Names($"data/skills-{lang}.json");
            data._zoneNames[lang] = Names($"data/zones-{lang}.json");
        }
        using (var doc = Json("data/heals.json"))
            foreach (var e in doc.RootElement.EnumerateArray())
                if (e.TryGetUInt32(out var family)) data._healFamilies.Add(family);
        return data;
    }

    private static Dictionary<uint, string> Names(string resource)
    {
        var table = new Dictionary<uint, string>();
        using var doc = Json(resource);
        foreach (var p in doc.RootElement.EnumerateObject())
            if (uint.TryParse(p.Name, out var id) && p.Value.GetString() is { Length: > 0 } n)
                table[id] = n;
        return table;
    }

    private static JsonDocument Json(string name)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
                      ?? throw new InvalidOperationException(name + " fehlt");
        return JsonDocument.Parse(s);
    }

    public string? NpcName(uint code) => _npcNames[L.Lang].TryGetValue(code, out var n) && n.Length > 0 ? n : null;

    public bool IsBoss(uint code) => _npcFlags.TryGetValue(code, out var n) && n.Boss && !n.Dummy;

    public bool IsDummy(uint code) => _npcFlags.TryGetValue(code, out var n) && n.Dummy;

    public string? ZoneName(uint zone) => _zoneNames[L.Lang].TryGetValue(zone, out var n) ? n : zone switch
    {
        // not in the dungeon table; ids from own recordings (Raid Announcer)
        600082 => "Ludra",
        600083 => L.T("Ludra (Hard)", "Ludra (Schwer)"),
        200003 => L.T("Nightmare", "Albtraum"),
        _ => null,
    };

    /// <summary>Dungeons, raids and other instances (the open world has 4-digit zone ids).</summary>
    public static bool IsInstance(uint zone) => zone >= 100_000;

    /// <summary>Name of a skill code: the code itself, its family (class skills: last four digits cleared), then the
    /// variant's base (last digit cleared).</summary>
    public string? SkillName(uint code)
    {
        var table = _skillNames[L.Lang];
        if (table.TryGetValue(code, out var n)) return n;
        var family = Skills.Family(code);
        if (family != code && table.TryGetValue(family, out n)) return n;
        if (code % 10 != 0 && table.TryGetValue(code - code % 10, out n)) return n;
        if (L.Lang != "en") return _skillNames["en"].TryGetValue(code, out n) ? n : null;
        return null;
    }

    public bool IsHeal(uint skill) => Skills.IsClassSkill(skill) && _healFamilies.Contains(Skills.Family(skill));
}

/// <summary>What a skill code says (HamMeter docs/protocol.md §7).</summary>
public static class Skills
{
    /// <summary>Player class skills: 8 digits, the first two are the class (10 = Elementalist spirit).</summary>
    public static bool IsClassSkill(uint code) => code is >= 10_000_000 and <= 19_999_999;

    /// <summary>Theostones: usable by every class, the class must come from the actor.</summary>
    public static bool IsTheostone(uint code) => code is >= 3_000_000 and <= 3_099_999;

    /// <summary>Pet / summon skills.</summary>
    public static bool IsPetSkill(uint code) => code is >= 100_000 and <= 199_999;

    /// <summary>NPC skills are 7 digits (theostones excepted).</summary>
    public static bool IsNpcSkill(uint code) => code is >= 1_000_000 and <= 9_999_999 && !IsTheostone(code);

    /// <summary>A skill only players (or their summons) use.</summary>
    public static bool IsPlayerSkill(uint code) => IsClassSkill(code) || IsTheostone(code) || IsPetSkill(code);

    /// <summary>Class skills group by family: the last four digits carry level / specialisation.</summary>
    public static uint Family(uint code) => IsClassSkill(code) ? code - code % 10_000 : code;

    /// <summary>Class id from a class skill (10..19), 0 for anything else. Mob skills hidden in the range (xx00xxxx with a
    /// zero skill number) map to nothing.</summary>
    public static int ClassOf(uint code) =>
        IsClassSkill(code) && code / 10_000 % 100 != 0 ? (int)(code / 1_000_000) : 0;

    /// <summary>Elementalist spirit links (1699xxxx, 1677xxxx) use the damage opcode but are not damage.</summary>
    public static bool IsSpiritLink(uint code) => code is >= 16_990_000 and <= 16_999_999 or >= 16_770_000 and <= 16_779_999;
}
