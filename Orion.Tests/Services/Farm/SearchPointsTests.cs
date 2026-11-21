using System.Numerics;
using Orion.Services.Farm;
using Xunit;

namespace Orion.Tests.Services.Farm;

public class SearchPointsTests
{
    /// <summary>Flat ground everywhere, at the height given.</summary>
    private static Func<float, float, Vector3?> Ground(float y = 0f) => (x, z) => new Vector3(x, y, z);

    [Fact]
    public void Around_OneRecordedSpot_BecomesAPatrolOfFive()
    {
        var patrol = SearchPoints.Around([new Vector3(100f, 0f, 100f)], Ground());

        Assert.Equal(5, patrol.Count);
        Assert.Equal(new Vector3(100f, 0f, 100f), patrol[0]); // the recorded spot is stood on first
        Assert.All(patrol.Skip(1), p => Assert.Equal(SearchPoints.RingYalms, FarmRoamPolicy.Flat(p, patrol[0]), 3));
    }

    [Fact]
    public void Around_LeavesOutAPointWithNoFloor()
    {
        // Nothing to stand on east of the spot (water, or off the mesh).
        Func<float, float, Vector3?> floor = (x, z) => x > 150f ? null : new Vector3(x, 0f, z);

        var patrol = SearchPoints.Around([new Vector3(100f, 0f, 100f)], floor);

        Assert.Equal(4, patrol.Count);
        Assert.DoesNotContain(patrol, p => p.X > 150f);
    }

    [Fact]
    public void Around_LeavesOutAPointUpACliff()
    {
        // North of the spot the ground is 60 yalms higher.
        Func<float, float, Vector3?> floor = (x, z) => new Vector3(x, z < 50f ? 60f : 0f, z);

        var patrol = SearchPoints.Around([new Vector3(100f, 0f, 100f)], floor);

        Assert.Equal(4, patrol.Count);
        Assert.DoesNotContain(patrol, p => p.Y > 0f);
    }

    [Fact]
    public void Around_DoesNotCrowdPointsBetweenTwoRecordedSpots()
    {
        // 100 yalms apart: the first's east point and the second's west point land 20 yalms from
        // the other spot, where its own leash already reaches.
        var spots = new[] { new Vector3(0f, 0f, 0f), new Vector3(100f, 0f, 0f) };

        var patrol = SearchPoints.Around(spots, Ground());

        Assert.Equal(8, patrol.Count); // two spots, three points each
        for (var i = 0; i < patrol.Count; i++)
            for (var j = i + 1; j < patrol.Count; j++)
                Assert.True(FarmRoamPolicy.Flat(patrol[i], patrol[j]) >= SearchPoints.MinApartYalms);
    }

    [Fact]
    public void Around_KeepsEveryRecordedSpot_AndTheirOrder()
    {
        // Two recorded spots nearly on top of each other are both kept: they are where the mob was seen.
        var spots = new[] { new Vector3(0f, 0f, 0f), new Vector3(10f, 0f, 0f) };

        var patrol = SearchPoints.Around(spots, Ground());

        Assert.Contains(spots[0], patrol);
        Assert.Contains(spots[1], patrol);
        Assert.True(patrol.IndexOf(spots[0]) < patrol.IndexOf(spots[1]));
    }
}
