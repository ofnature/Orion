using System;
using System.Collections.Generic;
using Orion.Services.HuntLog;

namespace Orion.Services.Farm;

/// <summary>A listed mob and the zones it has recorded spots in.</summary>
public sealed record TrophyCandidate(FarmMob Mob, IReadOnlyList<HuntSpots> Zones);

/// <summary>Where Trophy Hunt would go: which of the listed mobs, in which zone.</summary>
public sealed record TrophyLeg(FarmMob Mob, HuntSpots Zone);

/// <summary>
/// Pure choice of where to farm an item when no spot has been marked. Any dropper will do, so the
/// question is only which zone suits the character.
/// </summary>
public static class TrophyRoute
{
    /// <summary>
    /// The zone the character is already in wins (staying put costs nothing, and standing there
    /// was the player's choice). Otherwise the best-reported zone the character can reach whose
    /// mobs are not above its level; when every reachable zone is above it, the lowest. Null when
    /// no listed mob has a zone that can be reached.
    /// </summary>
    /// <param name="reachable">Whether the character can get into a territory at all (an attuned aetheryte).</param>
    public static TrophyLeg? Pick(
        IReadOnlyList<TrophyCandidate> candidates, uint currentTerritory, int playerLevel, Func<uint, bool> reachable)
    {
        TrophyLeg? here = null;
        TrophyLeg? fits = null;
        TrophyLeg? lowest = null;

        foreach (var candidate in candidates)
        {
            foreach (var zone in candidate.Zones)
            {
                if (zone.TerritoryId == currentTerritory)
                {
                    if (here == null || zone.Reports > here.Zone.Reports)
                        here = new TrophyLeg(candidate.Mob, zone);
                    continue;
                }

                if (here != null || !reachable(zone.TerritoryId))
                    continue;

                // A level of 0 is "not recorded": taken as within reach rather than ruled out.
                if (zone.Level <= playerLevel)
                {
                    if (fits == null || zone.Reports > fits.Zone.Reports)
                        fits = new TrophyLeg(candidate.Mob, zone);
                }
                else if (lowest == null || zone.Level < lowest.Zone.Level)
                {
                    lowest = new TrophyLeg(candidate.Mob, zone);
                }
            }
        }

        return here ?? fits ?? lowest;
    }
}
