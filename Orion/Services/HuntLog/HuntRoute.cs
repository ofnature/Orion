using System;
using System.Collections.Generic;
using System.Numerics;

namespace Orion.Services.HuntLog;

/// <summary>A target that still wants kills, and the zones it has recorded spots in.</summary>
public sealed record HuntCandidate(HuntLogTarget Target, IReadOnlyList<HuntSpots> Zones);

/// <summary>What to hunt next, and where: the zone holds only the spots worth standing at.</summary>
public sealed record HuntLeg(HuntLogTarget Target, HuntSpots Zone);

/// <summary>
/// Pure choices for the hunt log run: which target next, in which zone, at which spots. A mob of
/// one name stands at different levels in different places, and a kill counts the same wherever it
/// is made, so the run always goes where the mob's level is lowest: a new class can then get as far
/// down its log as its gear allows, and a high one loses nothing.
/// </summary>
public static class HuntRoute
{
    /// <summary>
    /// Levels this close to the lowest are the same group of mobs (one group spans a few levels),
    /// not a harder variant.
    /// </summary>
    public const int LevelBand = 3;

    private const int Unknown = int.MaxValue;

    /// <summary>
    /// The next leg: a target whose zone is the one the character is already in comes first (in
    /// log order); otherwise the first target in log order. Null when no candidate has a zone it
    /// can be hunted in.
    /// </summary>
    /// <param name="reachable">Whether the character can get into a territory at all (an attuned aetheryte).</param>
    public static HuntLeg? Next(IReadOnlyList<HuntCandidate> candidates, uint currentTerritory, Func<uint, bool> reachable)
    {
        HuntLeg? first = null;
        foreach (var candidate in candidates)
        {
            if (ZoneFor(candidate, currentTerritory, reachable) is not { } zone)
                continue;
            if (zone.TerritoryId == currentTerritory)
                return new HuntLeg(candidate.Target, zone);
            first ??= new HuntLeg(candidate.Target, zone);
        }
        return first;
    }

    /// <summary>
    /// The zone to hunt one target in, cut down to its lowest-level spots. Of the zones that can be
    /// reached, only those where the mob's level is lowest (within <see cref="LevelBand"/>) are
    /// considered: the current zone first, then the zones the log itself names (in its order),
    /// then the lowest of the rest. Zones with no level recorded lose to any that has one. Null
    /// when none can be reached.
    /// </summary>
    public static HuntSpots? ZoneFor(HuntCandidate candidate, uint currentTerritory, Func<uint, bool> reachable)
    {
        var open = new List<HuntSpots>();
        var lowest = Unknown;
        foreach (var zone in candidate.Zones)
        {
            if (zone.TerritoryId != currentTerritory && !reachable(zone.TerritoryId))
                continue;
            open.Add(zone);
            lowest = Math.Min(lowest, LowestLevel(zone));
        }

        bool Easy(HuntSpots zone) => lowest == Unknown || LowestLevel(zone) - lowest <= LevelBand;

        foreach (var zone in open)
        {
            if (zone.TerritoryId == currentTerritory && Easy(zone))
                return Easiest(zone);
        }

        foreach (var named in candidate.Target.Zones)
        {
            foreach (var zone in open)
            {
                if (zone.TerritoryId == named.TerritoryId && Easy(zone))
                    return Easiest(zone);
            }
        }

        foreach (var zone in open)
        {
            if (LowestLevel(zone) == lowest)
                return Easiest(zone);
        }
        return null;
    }

    /// <summary>
    /// A zone's spots where the mob's level is lowest (within <see cref="LevelBand"/>), lowest
    /// first and otherwise in the order given. The zone unchanged when no spot's level is known.
    /// </summary>
    public static HuntSpots Easiest(HuntSpots zone)
    {
        var lowest = LowestLevel(zone);
        if (lowest == Unknown)
            return zone;

        var levels = zone.SpotLevels!;
        var keep = new List<int>();
        for (var i = 0; i < zone.Spots.Count && i < levels.Count; i++)
        {
            if (levels[i] > 0 && levels[i] - lowest <= LevelBand)
                keep.Add(i);
        }
        keep.Sort((a, b) => levels[a] != levels[b] ? levels[a].CompareTo(levels[b]) : a.CompareTo(b));

        var spots = new List<Vector2>(keep.Count);
        var kept = new List<int>(keep.Count);
        foreach (var i in keep)
        {
            spots.Add(zone.Spots[i]);
            kept.Add(levels[i]);
        }
        return zone with { Spots = spots, SpotLevels = kept, Level = kept[^1] };
    }

    /// <summary>
    /// The highest level worth attacking at a zone's chosen spots: the lowest level there plus
    /// <see cref="LevelBand"/>. 0 when no level is known, which means no limit.
    /// </summary>
    public static int LevelCap(HuntSpots zone)
    {
        var lowest = LowestLevel(zone);
        return lowest == Unknown ? 0 : lowest + LevelBand;
    }

    private static int LowestLevel(HuntSpots zone)
    {
        var lowest = Unknown;
        if (zone.SpotLevels is { } levels)
        {
            foreach (var level in levels)
            {
                if (level > 0 && level < lowest)
                    lowest = level;
            }
        }
        return lowest;
    }
}
