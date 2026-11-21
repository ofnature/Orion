using System;
using System.Collections.Generic;
using System.Numerics;

namespace Orion.Services.Farm;

public enum FarmRoamAction
{
    /// <summary>Do nothing this tick (in transit, or waiting for respawns at the spot).</summary>
    Stay,

    /// <summary>Path to the active spot.</summary>
    MoveToSpot,

    /// <summary>Nothing to kill here for long enough — cycle to the next spot.</summary>
    AdvanceSpot,
}

/// <summary>
/// Pure decision logic for the farm loop — kept free of Dalamud types so it is unit-testable.
/// </summary>
public static class FarmRoamPolicy
{
    /// <summary>Distance at which the player counts as "at" a spot.</summary>
    public const float ArriveToleranceYalms = 5f;

    /// <summary>Roam decision when there is nothing to fight.</summary>
    public static FarmRoamAction Decide(
        bool navBusy,
        float distanceToSpotYalms,
        double secondsIdleAtSpot,
        double respawnWaitSeconds,
        int spotCount)
    {
        if (distanceToSpotYalms > ArriveToleranceYalms)
            return navBusy ? FarmRoamAction.Stay : FarmRoamAction.MoveToSpot;

        // At the spot with nothing up: rotate spots so respawn downtime is spent scanning
        // the next cluster instead of idling (single-spot profiles just wait).
        if (spotCount > 1 && secondsIdleAtSpot >= respawnWaitSeconds)
            return FarmRoamAction.AdvanceSpot;

        return FarmRoamAction.Stay;
    }

    public static int NextSpotIndex(int current, int spotCount)
        => spotCount <= 0 ? 0 : (current + 1) % spotCount;

    /// <summary>Mobs this close to the character are taken wherever the spots are.</summary>
    public const float NearbyYalms = 30f;

    /// <summary>A path that leaves the character less than this much nearer got nowhere.</summary>
    public const float ProgressYalms = 2f;

    /// <summary>How long after asking for a path before judging it: the pathfind itself takes a moment.</summary>
    public const double PathSettleSeconds = 1.5;

    /// <summary>
    /// Distance across the ground, heights ignored. A spot Orion marked for itself has its height
    /// read off the mesh from above, which can be a ledge or a roof over the ground the mobs stand on.
    /// </summary>
    public static float Flat(Vector3 a, Vector3 b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    /// <summary>
    /// Whether a mob is one to go for: inside the leash of any spot of the patrol, or right beside
    /// the character. Measured from the active spot alone, a mob standing next to the character on
    /// the way between two spots, or just past the circle after a chase, was passed over.
    /// </summary>
    public static bool InReach(
        Vector3 mob, Vector3 player, IReadOnlyList<Vector3> spots, float leashYalms, float nearbyYalms = NearbyYalms)
    {
        if (Vector3.Distance(mob, player) <= nearbyYalms)
            return true;
        foreach (var spot in spots)
        {
            if (Flat(mob, spot) <= leashYalms)
                return true;
        }
        return false;
    }

    /// <summary>
    /// The spot cannot be stood on: a path was asked for, it has had time to run, it is no longer
    /// running, and the character is no nearer than when it was asked. Asking again every poll is
    /// a step and a stop twice a second, for ever, and the patrol never moves on; where the
    /// character stands is as close as this spot gets.
    /// </summary>
    public static bool SpotIsOutOfReach(
        bool navBusy, bool pathAsked, double secondsSincePath, float distanceAtPath, float distanceNow)
        => !navBusy && pathAsked && secondsSincePath >= PathSettleSeconds
           && distanceNow > distanceAtPath - ProgressYalms;

    /// <summary>
    /// Whether a target that is no longer a live target was a kill. Still in the object table, it
    /// is one when it is dead (the body lingers a few seconds, long enough for the next poll). Gone
    /// from the table altogether, only when it had been fought: a mob that was merely targeted and
    /// then left, or a target that never took, is not a kill.
    /// </summary>
    public static bool CountsAsKill(bool stillInTable, bool dead, bool fought)
        => stillInTable ? dead : fought;

    public static bool IsComplete(uint itemCount, int targetCount)
        => targetCount > 0 && itemCount >= (uint)targetCount;

    /// <summary>Edge distance at which every job can land a ranged tag (all casts/shots reach 25y).</summary>
    public const float TagRangeYalms = 18f;

    /// <summary>Melee-reach edge distance for the walk-in fallback.</summary>
    public const float MeleeStopYalms = 2.5f;

    /// <summary>
    /// How long to stand at tag range waiting for the rotation to tag the mob before walking to
    /// melee instead (kits with no ranged attack — MNK — or ranged pulls disabled on tanks).
    /// </summary>
    public const double TagWindowSeconds = 4.0;

    /// <summary>
    /// Farm-only pull tactic (never shared with other movement systems): approach to ranged tag
    /// distance, stand still and tag, let the mob run to us while the rotation shoots it, and
    /// only walk to melee when the tag window expires without the mob engaging.
    /// </summary>
    public static FarmApproachAction DecideApproach(
        bool targetEngagedOnUs,
        float edgeDistanceYalms,
        double secondsAtTagRange)
    {
        // Tagged: the mob is coming to us — stand and kill it on the way in.
        if (targetEngagedOnUs)
            return FarmApproachAction.Hold;

        if (edgeDistanceYalms > TagRangeYalms)
            return FarmApproachAction.MoveToTagRange;

        if (edgeDistanceYalms <= MeleeStopYalms)
            return FarmApproachAction.Hold;

        // At tag range: give the rotation a window to land the ranged tag before walking in.
        return secondsAtTagRange >= TagWindowSeconds
            ? FarmApproachAction.MoveToMelee
            : FarmApproachAction.Hold;
    }
}

public enum FarmApproachAction
{
    /// <summary>Stand still (tagged mob incoming, standing in the tag window, or already in reach).</summary>
    Hold,

    /// <summary>Walk until the target is within ranged tag distance.</summary>
    MoveToTagRange,

    /// <summary>Tag window expired without engagement — walk to melee reach.</summary>
    MoveToMelee,
}
