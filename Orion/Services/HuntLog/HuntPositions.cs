using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text.Json;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using Orion.Services.Farm;

namespace Orion.Services.HuntLog;

/// <summary>
/// One recorded spot as the asset stores it: a map, a point in map coordinates, how many reports
/// back it, and the highest level the mob was seen at there (0 when the asset does not say).
/// </summary>
public readonly record struct RecordedSpot(uint MapId, float MapX, float MapY, int Reports, int Level = 0);

/// <summary>
/// Where a mob has been seen in one overworld zone: world X and Z per spot, the busiest first,
/// with the highest level seen there and the reports behind the whole zone.
/// <paramref name="SpotLevels"/> runs beside <paramref name="Spots"/>: the highest level seen at
/// each spot, 0 when unknown. The same mob can stand at very different levels in one zone.
/// </summary>
public sealed record HuntSpots(
    uint TerritoryId, IReadOnlyList<Vector2> Spots, int Level = 0, int Reports = 0, IReadOnlyList<int>? SpotLevels = null);

/// <summary>
/// Where the mobs Orion hunts stand, from <c>Assets/hunt-positions.json</c>: FFXIV Teamcraft's
/// player-reported positions (MIT), trimmed to the hunt log's mobs and the mobs that drop items,
/// and grouped into spots by <c>Tools/import_teamcraft_monsters.py</c>. The game's own sheets only
/// name the area. Heights are not stored: the navmesh is asked for the floor once the character
/// is in the zone.
/// </summary>
public sealed class HuntPositions
{
    public const string FileName = "hunt-positions.json";

    /// <summary><c>TerritoryIntendedUse</c> of an ordinary overworld zone.</summary>
    private const uint OverworldUse = 1;

    private readonly Dictionary<uint, List<HuntSpots>> _byMob = new();
    private readonly Dictionary<uint, List<HuntSpots>> _added = new();

    public static string PathBeside(string? assemblyDirectory)
        => Path.Combine(assemblyDirectory ?? string.Empty, "Assets", FileName);

    /// <summary>Reads the asset. A missing or broken file leaves every mob without a position, with a warning.</summary>
    public HuntPositions(string path, IDataManager data, Action<string> warn)
    {
        Dictionary<uint, List<RecordedSpot>> recorded;
        try
        {
            recorded = Parse(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            warn($"Mob positions could not be read from {path} ({ex.GetType().Name}: {ex.Message}) — Orion has nowhere to travel to.");
            return;
        }

        var maps = data.GetExcelSheet<Map>();
        foreach (var (nameId, spots) in recorded)
        {
            // Territory to (spots, highest level, reports), in the order the zones first appear.
            var order = new List<uint>();
            var zones = new Dictionary<uint, (List<Vector2> Spots, List<int> Levels, int Level, int Reports)>();
            foreach (var spot in spots)
            {
                // Dungeon maps are in the reports too; only the overworld can be walked into.
                if (maps.GetRowOrDefault(spot.MapId) is not { } map
                    || map.TerritoryType.ValueNullable is not { } territory
                    || territory.TerritoryIntendedUse.RowId != OverworldUse)
                    continue;

                var world = new Vector2(
                    FarmLocationHelper.MapCoordToWorld(spot.MapX, map.SizeFactor, map.OffsetX),
                    FarmLocationHelper.MapCoordToWorld(spot.MapY, map.SizeFactor, map.OffsetY));

                if (!zones.TryGetValue(territory.RowId, out var zone))
                {
                    zone = (new List<Vector2>(), new List<int>(), 0, 0);
                    order.Add(territory.RowId);
                }
                zone.Spots.Add(world);
                zone.Levels.Add(spot.Level);
                zones[territory.RowId] = (zone.Spots, zone.Levels, Math.Max(zone.Level, spot.Level), zone.Reports + spot.Reports);
            }

            if (order.Count == 0)
                continue;
            var list = new List<HuntSpots>(order.Count);
            foreach (var territoryId in order)
            {
                var zone = zones[territoryId];
                list.Add(new HuntSpots(territoryId, zone.Spots, zone.Level, zone.Reports, zone.Levels));
            }
            _byMob[nameId] = list;
        }
    }

    /// <summary>
    /// The zones a mob has been seen in, or an empty list when nothing is recorded. The asset's
    /// reports come first; a mob it does not know falls back to the places added from elsewhere.
    /// </summary>
    public IReadOnlyList<HuntSpots> For(uint nameId)
        => _byMob.TryGetValue(nameId, out var zones) ? zones
            : _added.TryGetValue(nameId, out var added) ? added : [];

    /// <summary>
    /// A place for a mob from outside the asset (the wiki's coordinates). One zone per territory;
    /// it only shows through <see cref="For"/> for a mob the asset has nothing on.
    /// </summary>
    public void Add(uint nameId, HuntSpots zone)
    {
        if (!_added.TryGetValue(nameId, out var zones))
            _added[nameId] = zones = new List<HuntSpots>();
        if (!zones.Exists(z => z.TerritoryId == zone.TerritoryId))
            zones.Add(zone);
    }

    /// <summary>
    /// The asset's <c>mobs</c> table: BNpcName id to <c>[map id, map x, map y, reports, level]</c>
    /// rows (level optional), which the import tool writes busiest first within each map.
    /// </summary>
    internal static Dictionary<uint, List<RecordedSpot>> Parse(string json)
    {
        var result = new Dictionary<uint, List<RecordedSpot>>();
        using var doc = JsonDocument.Parse(json);
        foreach (var mob in doc.RootElement.GetProperty("mobs").EnumerateObject())
        {
            if (!uint.TryParse(mob.Name, out var nameId))
                continue;
            var spots = new List<RecordedSpot>();
            foreach (var row in mob.Value.EnumerateArray())
            {
                var length = row.GetArrayLength();
                if (length < 4)
                    continue;
                spots.Add(new RecordedSpot(
                    row[0].GetUInt32(), row[1].GetSingle(), row[2].GetSingle(), row[3].GetInt32(),
                    length > 4 ? row[4].GetInt32() : 0));
            }
            if (spots.Count > 0)
                result[nameId] = spots;
        }
        return result;
    }
}
