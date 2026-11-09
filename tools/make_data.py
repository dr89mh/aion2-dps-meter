"""Builds the game data tables in data/ from the cyberbadger6969/aion2-dps-meter data folder (GPL-3.0; itself taken
from taengu/A2Tools-DPS-Meter and Kuroukihime/AIon2-Dps-Meter, see that folder's NOTICE.md). Game data (c) NCSOFT.

  skills-<lang>.json  every named skill code (NPC, theostone, pet and class skills); class skill variants whose name is
                      the same as their family (code with the last four digits cleared) are left out, the app falls back
  npcs-<lang>.json    NPC code -> {"n": name, "b": 1 boss, "d": dungeon id, "x": 1 training dummy}
  zones-<lang>.json   dungeon / raid zone id -> name (only English exists; German uses it too)
  heals.json          skill families that heal

Run: python -I tools/make_data.py <path to aion2-dps-meter/data> <output data dir>"""
import json, os, sys

src, out = sys.argv[1], sys.argv[2]
nm = lambda v: v if isinstance(v, str) else (v or {}).get("name")


def load(*parts):
    p = os.path.join(src, *parts)
    return json.load(open(p, encoding="utf-8")) if os.path.exists(p) else None


def write(name, data):
    with open(os.path.join(out, name), "w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False, separators=(",", ":"), sort_keys=True)


def npc(v):
    e = {"n": v["name"]}
    if v.get("isBoss"): e["b"] = 1
    if v.get("dungeonId"): e["d"] = v["dungeonId"]
    if v.get("isDummy"): e["x"] = 1
    return e


zones_en = {k: nm(v) for k, v in (load("dungeons", "en.json") or {}).items() if nm(v)}
for lang in ("en", "de"):
    raw = {int(k): nm(v) for k, v in load("skills", f"{lang}.json").items() if k.isdigit() and nm(v)}
    en = {int(k): nm(v) for k, v in load("skills", "en.json").items() if k.isdigit() and nm(v)}
    skills = {}
    for code, name in raw.items():
        if name.strip() in ("", "???"):
            name = en.get(code)
            if not name or name.strip() == "???": continue
        if 10_000_000 <= code <= 19_999_999:
            fam = code - code % 10_000
            if fam != code and raw.get(fam) == name: continue
        skills[str(code)] = name
    npcs = {k: npc(v) for k, v in load("npcs", f"{lang}.json").items() if v.get("name")}
    zones = {k: v for k, v in ((load("dungeons", f"{lang}.json") or {}).items())} if lang == "en" else {}
    zones = {k: nm(v) for k, v in zones.items()} if zones else dict(zones_en)
    write(f"skills-{lang}.json", skills)
    write(f"npcs-{lang}.json", npcs)
    write(f"zones-{lang}.json", zones)
    print(lang, len(skills), "skills,", len(npcs), "npcs,", len(zones), "zones")

heals = sorted({c - c % 10_000 for c in load("healing_skill_ids.json")})
write("heals.json", heals)
print("heals", heals)
