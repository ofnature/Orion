using System.Numerics;
using Orion.Services.Farm;
using Xunit;

namespace Orion.Tests.Services.Farm;

/// <summary>Which mobs are gone for, and when a spot is given up as unreachable.</summary>
public class FarmReachTests
{
    private static readonly Vector3[] Spots = [new(0f, 0f, 0f), new(300f, 0f, 0f)];

    [Fact]
    public void InReach_AMobInsideAnySpotsLeash_NotOnlyTheActiveOne()
    {
        // Beside the second spot while the first is the active one.
        var mob = new Vector3(280f, 0f, 10f);

        Assert.True(FarmRoamPolicy.InReach(mob, player: new Vector3(250f, 0f, 0f), Spots, leashYalms: 60f));
    }

    [Fact]
    public void InReach_AMobBesideTheCharacter_OutsideEveryLeash()
    {
        // Half way between two spots, 150 yalms from either: passed over before.
        var player = new Vector3(150f, 0f, 0f);
        var mob = new Vector3(160f, 0f, 15f);

        Assert.True(FarmRoamPolicy.InReach(mob, player, Spots, leashYalms: 60f));
    }

    [Fact]
    public void InReach_NotAMobFarFromBothTheSpotsAndTheCharacter()
    {
        var player = new Vector3(150f, 0f, 0f);
        var mob = new Vector3(150f, 0f, 90f);

        Assert.False(FarmRoamPolicy.InReach(mob, player, Spots, leashYalms: 60f));
    }

    [Fact]
    public void InReach_MeasuresTheLeashAcrossTheGround()
    {
        // The spot's height was read off a ledge 80 yalms above the mobs.
        var spots = new[] { new Vector3(0f, 80f, 0f) };
        var mob = new Vector3(40f, 0f, 0f);

        Assert.True(FarmRoamPolicy.InReach(mob, player: new Vector3(200f, 0f, 0f), spots, leashYalms: 60f));
    }

    [Fact]
    public void InArea_AMobAnywhereInsideTheRadiusOfAReportedPlace()
    {
        var centres = new[] { new Vector3(0f, 0f, 0f), new Vector3(1000f, 0f, 0f) };

        Assert.True(FarmRoamPolicy.InArea(new Vector3(150f, 0f, 40f), centres, radiusYalms: 160f));
        Assert.True(FarmRoamPolicy.InArea(new Vector3(1000f, 30f, -160f), centres, radiusYalms: 160f)); // across the ground
    }

    [Fact]
    public void InArea_NotAMobPastTheRadius_HoweverNearTheCharacterItIs()
    {
        // The last kill left the character at the edge; the next mob is five yalms further out.
        var centres = new[] { new Vector3(0f, 0f, 0f) };

        Assert.False(FarmRoamPolicy.InArea(new Vector3(165f, 0f, 0f), centres, radiusYalms: 160f));
    }

    [Fact]
    public void NearestSpotIndex_IsThePatrolPointClosestToTheCharacter()
    {
        var patrol = new[] { new Vector3(0f, 0f, 0f), new Vector3(0f, 0f, -80f), new Vector3(80f, 0f, 0f), new Vector3(0f, 0f, 80f) };

        Assert.Equal(2, FarmRoamPolicy.NearestSpotIndex(new Vector3(70f, 0f, 20f), patrol));
        Assert.Equal(0, FarmRoamPolicy.NearestSpotIndex(new Vector3(5f, 50f, 5f), patrol)); // height does not count
        Assert.Equal(0, FarmRoamPolicy.NearestSpotIndex(new Vector3(5f, 0f, 5f), []));
    }

    [Fact]
    public void SpotIsOutOfReach_WhenThePathEndedNoNearer()
    {
        Assert.True(FarmRoamPolicy.SpotIsOutOfReach(
            navBusy: false, pathAsked: true, secondsSincePath: 2, distanceAtPath: 12f, distanceNow: 11.5f));
    }

    [Fact]
    public void SpotIsOutOfReach_NotWhileThePathIsRunning()
    {
        Assert.False(FarmRoamPolicy.SpotIsOutOfReach(
            navBusy: true, pathAsked: true, secondsSincePath: 30, distanceAtPath: 12f, distanceNow: 12f));
    }

    [Fact]
    public void SpotIsOutOfReach_NotBeforeAPathWasAskedFor()
    {
        Assert.False(FarmRoamPolicy.SpotIsOutOfReach(
            navBusy: false, pathAsked: false, secondsSincePath: 30, distanceAtPath: 0f, distanceNow: 40f));
    }

    [Fact]
    public void SpotIsOutOfReach_NotInTheMomentAfterAsking()
    {
        // The pathfind has not reported itself yet: idle here means nothing.
        Assert.False(FarmRoamPolicy.SpotIsOutOfReach(
            navBusy: false, pathAsked: true, secondsSincePath: 0.5, distanceAtPath: 40f, distanceNow: 40f));
    }

    [Fact]
    public void SpotIsOutOfReach_NotWhenThePathBroughtTheCharacterNearer()
    {
        // Stopped short, but a long way nearer: ask again from here.
        Assert.False(FarmRoamPolicy.SpotIsOutOfReach(
            navBusy: false, pathAsked: true, secondsSincePath: 20, distanceAtPath: 200f, distanceNow: 50f));
    }

    [Fact]
    public void Flat_IgnoresHeight()
    {
        Assert.Equal(5f, FarmRoamPolicy.Flat(new Vector3(0f, 100f, 0f), new Vector3(3f, -40f, 4f)), 3);
    }
}
