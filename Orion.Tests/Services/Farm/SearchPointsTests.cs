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
    public void Around_AWiderSearch_AddsASecondRingOfEight()
    {
        var spot = new Vector3(0f, 0f, 0f);

        var patrol = SearchPoints.Around([spot], Ground(), radiusYalms: 160f);

        Assert.Equal(13, patrol.Count); // the spot, four at 80 yalms, eight at 160
        Assert.Equal(8, patrol.Count(p => MathF.Abs(FarmRoamPolicy.Flat(p, spot) - 160f) < 0.01f));
        for (var i = 0; i < patrol.Count; i++)
            for (var j = i + 1; j < patrol.Count; j++)
                Assert.True(FarmRoamPolicy.Flat(patrol[i], patrol[j]) >= SearchPoints.MinApartYalms);
    }

    [Fact]
    public void Around_ASearchOfNothing_StandsOnTheRecordedSpotsAlone()
    {
        var spots = new[] { new Vector3(0f, 0f, 0f), new Vector3(300f, 0f, 0f) };

        Assert.Equal(spots, SearchPoints.Around(spots, Ground(), radiusYalms: 0f));
    }

    [Fact]
    public void Around_TheWidestSearch_StaysUnderThePatrolLimit_AndKeepsEveryRecordedSpot()
    {
        // Four recorded spots a long way apart, four rings each: 160 points if nothing held it back.
        var spots = new[]
        {
            new Vector3(0f, 0f, 0f), new Vector3(2000f, 0f, 0f), new Vector3(0f, 0f, 2000f), new Vector3(2000f, 0f, 2000f),
        };

        var patrol = SearchPoints.Around(spots, Ground(), SearchPoints.MaxRadiusYalms);

        Assert.Equal(SearchPoints.MaxPatrolPoints, patrol.Count);
        Assert.All(spots, spot => Assert.Contains(spot, patrol));
        // Each spot got the same share, nearest ring first.
        Assert.All(spots, spot => Assert.Equal(10, patrol.Count(p => FarmRoamPolicy.Flat(p, spot) <= 170f)));
    }

    [Theory]
    [InlineData(0f, 0)]
    [InlineData(39f, 0)]
    [InlineData(40f, 1)]
    [InlineData(80f, 1)]
    [InlineData(160f, 2)]
    [InlineData(200f, 3)]
    [InlineData(320f, 4)]
    [InlineData(900f, 4)]
    public void Rings_OnePerEightyYalms_UpToFour(float radius, int rings)
    {
        Assert.Equal(rings, SearchPoints.Rings(radius));
    }

    [Theory]
    [InlineData(0f, 80f)]    // a search of nothing still takes the mobs around the spot
    [InlineData(80f, 80f)]
    [InlineData(160f, 160f)]
    [InlineData(200f, 240f)] // out to the last ring, so no patrol point lies outside the area
    [InlineData(320f, 320f)]
    public void AreaRadius_ReachesTheLastRing_AndNeverLessThanOne(float radius, float area)
    {
        Assert.Equal(area, SearchPoints.AreaRadius(radius));
    }

    [Fact]
    public void Around_EveryPatrolPointLiesInsideTheArea()
    {
        var spot = new Vector3(0f, 0f, 0f);

        foreach (var radius in new[] { 0f, 100f, 200f, 320f })
        {
            var patrol = SearchPoints.Around([spot], Ground(), radius);
            Assert.All(patrol, p => Assert.True(FarmRoamPolicy.InArea(p, [spot], SearchPoints.AreaRadius(radius) + 0.01f)));
        }
    }

    [Fact]
    public void Around_KeepsThePatrolAwayFromPlacesToAvoid_ButNeverDropsTheRecordedSpot()
    {
        // The same mob stands at a higher level 100 yalms east of the spot.
        var spot = new Vector3(0f, 0f, 0f);
        var harder = new[] { new Vector2(100f, 0f) };

        var patrol = SearchPoints.Around([spot], Ground(), radiusYalms: 160f, harder);

        Assert.Equal(spot, patrol[0]);
        Assert.All(patrol.Skip(1), p => Assert.True(Vector2.Distance(new Vector2(p.X, p.Z), harder[0]) >= SearchPoints.AvoidYalms));
        Assert.True(patrol.Count < 13);                 // the east side is gone
        Assert.Contains(patrol, p => p.X < -70f);       // the west side is still walked
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
