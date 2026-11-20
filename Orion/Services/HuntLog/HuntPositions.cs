using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text.Json;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using Orion.Services.Farm;

namespace Orion.Services.HuntLog;

/// <summary>One recorded spot as the asset stores it: a map, a point in map coordinates, and how many reports back it.</summary>
public readonly record struct RecordedSpot(uint MapId, float MapX, float MapY, int Reports);

/// <summary>Where a mob has been seen in one overworld zone: world X and Z per spot, the busiest first.</summary>
public sealed record HuntSpots(uint TerritoryId, IReadOnlyList<Vector2> Spots);

/// <summary>
/// Where the hunt log's mobs stand, from <c>Assets/hunt-positions.json</c>: FFXIV Teamcraft's
/// player-reported positions (MIT), trimmed to hunt-log mobs and grouped into spots by
/// <c>Tools/import_teamcraft_monsters.py</c>. The game's own sheets only name the area.
/// Heights are not stored: the navmesh is asked for the floor once the character is in the zone.
/// </summary>
public sealed class HuntPositions
{
    public const string FileName = "hunt-positions.json";

    /// <summary><c>TerritoryIntendedUse</c> of an ordinary overworld zone.</summary>
    private const uint OverworldUse = 1;

    private readonly Dictionary<uint, List<HuntSpots>> _byMob = new();

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
            warn($"Hunt positions could not be read from {path} ({ex.GetType().Name}: {ex.Message}) — the hunt log run has nowhere to go.");
            return;
        }

        var maps = data.GetExcelSheet<Map>();
        foreach (var (nameId, spots) in recorded)
        {
            var zones = new List<HuntSpots>();
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

                var zone = zones.Find(z => z.TerritoryId == territory.RowId);
                if (zone == null)
                    zones.Add(zone = new HuntSpots(territory.RowId, new List<Vector2>()));
                ((List<Vector2>)zone.Spots).Add(world);
            }
            if (zones.Count > 0)
                _byMob[nameId] = zones;
        }
    }

    /// <summary>The zones a mob has been seen in, or an empty list when nothing is recorded.</summary>
    public IReadOnlyList<HuntSpots> For(uint nameId)
        => _byMob.TryGetValue(nameId, out var zones) ? zones : [];

    /// <summary>
    /// The asset's <c>mobs</c> table: BNpcName id to <c>[map id, map x, map y, reports]</c> rows,
    /// which the import tool writes busiest first within each map.
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
                if (row.GetArrayLength() < 4)
                    continue;
                spots.Add(new RecordedSpot(row[0].GetUInt32(), row[1].GetSingle(), row[2].GetSingle(), row[3].GetInt32()));
            }
            if (spots.Count > 0)
                result[nameId] = spots;
        }
        return result;
    }
}
