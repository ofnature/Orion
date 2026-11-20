using System;
using System.Collections.Generic;

namespace Orion.Services.HuntLog;

/// <summary>A target that still wants kills, and the zones it has recorded spots in.</summary>
public sealed record HuntCandidate(HuntLogTarget Target, IReadOnlyList<HuntSpots> Zones);

/// <summary>What to hunt next, and where.</summary>
public sealed record HuntLeg(HuntLogTarget Target, HuntSpots Zone);

/// <summary>
/// Pure choices for the hunt log run: which target next and in which zone. No route optimising
/// beyond not leaving a zone that still has work in it.
/// </summary>
public static class HuntRoute
{
    /// <summary>
    /// The next leg: a target with spots in the zone the character is already in comes first (in
    /// log order); otherwise the first target in log order, in the best zone for it. Null when no
    /// candidate has a zone it can be hunted in.
    /// </summary>
    /// <param name="reachable">Whether the character can get into a territory at all (an attuned aetheryte).</param>
    public static HuntLeg? Next(IReadOnlyList<HuntCandidate> candidates, uint currentTerritory, Func<uint, bool> reachable)
    {
        foreach (var candidate in candidates)
        {
            foreach (var zone in candidate.Zones)
            {
                if (zone.TerritoryId == currentTerritory)
                    return new HuntLeg(candidate.Target, zone);
            }
        }

        foreach (var candidate in candidates)
        {
            if (ZoneFor(candidate, currentTerritory, reachable) is { } zone)
                return new HuntLeg(candidate.Target, zone);
        }
        return null;
    }

    /// <summary>
    /// The zone to hunt one target in: the current zone when the mob is there, then the zones the
    /// log itself names (in its order), then anywhere else it has been seen. Null when none can be reached.
    /// </summary>
    public static HuntSpots? ZoneFor(HuntCandidate candidate, uint currentTerritory, Func<uint, bool> reachable)
    {
        foreach (var zone in candidate.Zones)
        {
            if (zone.TerritoryId == currentTerritory)
                return zone;
        }

        foreach (var named in candidate.Target.Zones)
        {
            foreach (var zone in candidate.Zones)
            {
                if (zone.TerritoryId == named.TerritoryId && reachable(zone.TerritoryId))
                    return zone;
            }
        }

        foreach (var zone in candidate.Zones)
        {
            if (reachable(zone.TerritoryId))
                return zone;
        }
        return null;
    }
}
