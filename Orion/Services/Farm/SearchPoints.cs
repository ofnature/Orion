using System;
using System.Collections.Generic;
using System.Numerics;

namespace Orion.Services.Farm;

/// <summary>
/// The patrol Trophy Hunt walks when it marked the spots itself. A recorded spot is one point
/// where players reported the mob, and a third of the mobs that drop something have only one in
/// their zone: stood on alone, the search reached no further than the leash around it. Each
/// recorded spot gets four more points around it, so the patrol covers the ground the mobs spread
/// over instead of waiting at the middle of it.
/// </summary>
public static class SearchPoints
{
    /// <summary>How far out the extra points sit. With the default 60-yalm leash the circles overlap.</summary>
    public const float RingYalms = 80f;

    /// <summary>An extra point this near one already on the patrol adds nothing.</summary>
    public const float MinApartYalms = 50f;

    /// <summary>An extra point this far above or below its spot is up a cliff or down a hole.</summary>
    public const float MaxClimbYalms = 30f;

    private static readonly (float X, float Z)[] Compass = [(0f, -1f), (1f, 0f), (0f, 1f), (-1f, 0f)];

    /// <summary>
    /// The recorded spots, each followed by the points around it that have a floor, are not up a
    /// cliff, and are not on top of a point already kept or of another recorded spot.
    /// </summary>
    /// <param name="floorAt">The floor of the zone's mesh at a world X and Z, or null where there is none.</param>
    public static List<Vector3> Around(IReadOnlyList<Vector3> spots, Func<float, float, Vector3?> floorAt)
    {
        var patrol = new List<Vector3>();
        foreach (var spot in spots)
        {
            patrol.Add(spot);
            foreach (var (dx, dz) in Compass)
            {
                if (floorAt(spot.X + dx * RingYalms, spot.Z + dz * RingYalms) is not { } point)
                    continue;
                if (MathF.Abs(point.Y - spot.Y) > MaxClimbYalms)
                    continue;
                if (IsNear(point, patrol) || IsNear(point, spots))
                    continue;
                patrol.Add(point);
            }
        }
        return patrol;
    }

    private static bool IsNear(Vector3 point, IReadOnlyList<Vector3> others)
    {
        foreach (var other in others)
        {
            if (FarmRoamPolicy.Flat(point, other) < MinApartYalms)
                return true;
        }
        return false;
    }
}
