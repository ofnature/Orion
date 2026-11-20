"""Builds Orion/Assets/hunt-positions.json from FFXIV Teamcraft's monsters.json (MIT).

Teamcraft's file holds positions players' clients reported for every mob: map coordinates, the
map and zone, the level, and whether the mob belonged to a FATE. This keeps the hunt log's mobs
only (Tools/hunt-log-mobs.txt, read from the game's MonsterNote sheets), drops FATE spawns, and
groups each mob's points on a map into a few spots, the busiest first.

    python -I Tools/import_teamcraft_monsters.py <path to a downloaded monsters.json>

Source: https://github.com/ffxiv-teamcraft/ffxiv-teamcraft
        libs/data/src/lib/json/monsters.json (staging branch)
"""
import datetime
import json
import os
import sys

# Points closer than this (in map units; 50 yalms on an ordinary zone map) are one spot.
CLUSTER_RADIUS = 1.0
MAX_SPOTS_PER_MAP = 6

here = os.path.dirname(os.path.abspath(__file__))


def clusters(points):
    """Greedy: the most-reported point seeds a spot and takes every point within the radius."""
    counts = {}
    for x, y in points:
        counts[(x, y)] = counts.get((x, y), 0) + 1
    left = sorted(counts.items(), key=lambda kv: (-kv[1], kv[0]))
    spots = []
    while left:
        (sx, sy), _ = left[0]
        inside = [(p, n) for p, n in left if (p[0] - sx) ** 2 + (p[1] - sy) ** 2 <= CLUSTER_RADIUS ** 2]
        left = [(p, n) for p, n in left if (p[0] - sx) ** 2 + (p[1] - sy) ** 2 > CLUSTER_RADIUS ** 2]
        total = sum(n for _, n in inside)
        cx = sum(p[0] * n for p, n in inside) / total
        cy = sum(p[1] * n for p, n in inside) / total
        spots.append((round(cx, 2), round(cy, 2), total))
    spots.sort(key=lambda s: -s[2])
    return spots[:MAX_SPOTS_PER_MAP]


def main():
    if len(sys.argv) != 2:
        sys.exit(__doc__)

    with open(os.path.join(here, "hunt-log-mobs.txt"), encoding="utf-8") as f:
        wanted = [line.strip() for line in f if line.strip()]
    with open(sys.argv[1], encoding="utf-8") as f:
        monsters = json.load(f)

    mobs = {}
    missing = []
    fate_only = []
    for name_id in wanted:
        entry = monsters.get(name_id)
        positions = entry.get("positions", []) if entry else []
        if not positions:
            missing.append(name_id)
            continue
        by_map = {}
        for p in positions:
            if p.get("fate"):
                continue
            by_map.setdefault(p["map"], []).append((p["x"], p["y"]))
        if not by_map:
            fate_only.append(name_id)
            continue
        spots = []
        for map_id in sorted(by_map):
            for x, y, n in clusters(by_map[map_id]):
                spots.append([map_id, x, y, n])
        mobs[name_id] = spots

    out = {
        "source": "FFXIV Teamcraft monsters.json (MIT) - https://github.com/ffxiv-teamcraft/ffxiv-teamcraft",
        "imported": datetime.date.today().isoformat(),
        "format": "mobs[BNpcName id] = [[Map id, map x, map y, reports], ...]",
        "mobs": mobs,
    }
    target = os.path.join(here, "..", "Orion", "Assets", "hunt-positions.json")
    os.makedirs(os.path.dirname(target), exist_ok=True)
    with open(target, "w", encoding="utf-8", newline="\n") as f:
        json.dump(out, f, separators=(",", ":"))
        f.write("\n")

    print(f"hunt-log mobs: {len(wanted)}; with positions: {len(mobs)}; "
          f"FATE-only: {len(fate_only)}; not in Teamcraft: {len(missing)}")
    print(f"spots written: {sum(len(v) for v in mobs.values())}; file: {os.path.getsize(target)} bytes")
    print("not in Teamcraft:", " ".join(missing))
    print("FATE-only:", " ".join(fate_only))


if __name__ == "__main__":
    main()
