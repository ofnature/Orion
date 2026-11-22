using System;
using System.Collections.Generic;
using System.Numerics;

namespace Orion.Services.Farm;

/// <summary>
/// The patrol Trophy Hunt walks when it marked the spots itself. A recorded spot is one point
/// where players reported the mob, and a third of the mobs that drop something have only one in
/// their zone: stood on alone, the search reached no further than the leash around it. Each
/// recorded spot gets rings of points around it, out to the search radius the player set, so the
/// patrol covers the ground the mobs spread over instead of waiting at the middle of it.
/// </summary>
public static class SearchPoints
{
    /// <summary>How far apart the rings sit. With the default 60-yalm leash the circles overlap.</summary>
    public const float RingYalms = 80f;

    /// <summary>The widest search the setting allows: four rings.</summary>
    public const float MaxRadiusYalms = 320f;

    /// <summary>The patrol's size, recorded spots included: more than this and it never comes back round.</summary>
    public const int MaxPatrolPoints = 40;

    /// <summary>An extra point this near one already on the patrol adds nothing.</summary>
    public const float MinApartYalms = 50f;

    /// <summary>An extra point this far above or below its spot, per ring out, is up a cliff or down a hole.</summary>
    public const float MaxClimbYalms = 30f;

    /// <summary>How many rings a search radius comes to: one per <see cref="RingYalms"/>, none below half of one.</summary>
    public static int Rings(float radiusYalms)
        => (int)MathF.Round(Math.Clamp(radiusYalms, 0f, MaxRadiusYalms) / RingYalms, MidpointRounding.AwayFromZero);

    /// <summary>
    /// How far from a recorded spot the run may go for a search radius: out to its last ring, and
    /// never less than one ring, so a search of nothing still takes the mobs around the spot.
    /// </summary>
    public static float AreaRadius(float radiusYalms) => Math.Max(Rings(radiusYalms), 1) * RingYalms;

    /// <summary>
    /// The recorded spots, each followed by its rings of points, nearest ring first and each ring
    /// walked round in order. Left out: points with no floor, up a cliff, or on top of a point
    /// already kept or of another recorded spot. The recorded spots are always all kept; the extra
    /// points are shared between them under <see cref="MaxPatrolPoints"/>.
    /// </summary>
    /// <param name="floorAt">The floor of the zone's mesh at a world X and Z, or null where there is none.</param>
    /// <param name="radiusYalms">How far from each recorded spot to search.</param>
    public static List<Vector3> Around(
        IReadOnlyList<Vector3> spots, Func<float, float, Vector3?> floorAt, float radiusYalms = RingYalms)
    {
        var patrol = new List<Vector3>();
        if (spots.Count == 0)
            return patrol;

        var rings = Rings(radiusYalms);
        var extraPerSpot = Math.Max(0, MaxPatrolPoints - spots.Count) / spots.Count;
        foreach (var spot in spots)
        {
            patrol.Add(spot);
            var extra = 0;
            for (var ring = 1; ring <= rings && extra < extraPerSpot; ring++)
            {
                // Four points on the first ring (north, east, south, west), eight on the second, and
                // so on: about the same step between neighbours on every ring. Even rings are turned
                // half a step so their points fall between the ones inside them.
                var count = 4 * ring;
                var turn = ring % 2 == 0 ? MathF.PI / count : 0f;
                for (var i = 0; i < count && extra < extraPerSpot; i++)
                {
                    var angle = MathF.Tau * i / count + turn;
                    var x = spot.X + MathF.Sin(angle) * ring * RingYalms;
                    var z = spot.Z - MathF.Cos(angle) * ring * RingYalms;
                    if (floorAt(x, z) is not { } point)
                        continue;
                    if (MathF.Abs(point.Y - spot.Y) > MaxClimbYalms * ring)
                        continue;
                    if (IsNear(point, patrol) || IsNear(point, spots))
                        continue;
                    patrol.Add(point);
                    extra++;
                }
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
