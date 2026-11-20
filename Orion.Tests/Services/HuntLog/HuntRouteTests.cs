using System.Numerics;
using Orion.Services.HuntLog;

namespace Orion.Tests.Services.HuntLog;

public sealed class HuntRouteTests
{
    private const uint MiddleLaNoscea = 134;
    private const uint CentralShroud = 148;
    private const uint WesternThanalan = 140;
    private const uint NorthShroud = 154;

    private static HuntCandidate Candidate(int entry, uint[] logZones, params uint[] seenIn)
    {
        var named = logZones.Select(t => new HuntLogZone(t, $"zone {t}", "")).ToList();
        var target = new HuntLogTarget(0, 0, entry, 0, (uint)(100 + entry), $"mob {entry}", 3, named);
        var zones = seenIn.Select(t => new HuntSpots(t, [new Vector2(1f, 2f)])).ToList();
        return new HuntCandidate(target, zones);
    }

    private static bool Anywhere(uint territory) => true;

    [Fact]
    public void Next_PrefersATargetInTheCurrentZone_OverAnEarlierOneElsewhere()
    {
        var candidates = new[]
        {
            Candidate(0, [MiddleLaNoscea], MiddleLaNoscea),
            Candidate(1, [CentralShroud], CentralShroud),
        };

        var leg = HuntRoute.Next(candidates, CentralShroud, Anywhere);

        Assert.Equal(1, leg!.Target.Entry);
        Assert.Equal(CentralShroud, leg.Zone.TerritoryId);
    }

    [Fact]
    public void Next_WithNothingHere_TakesTheFirstTargetInLogOrder()
    {
        var candidates = new[]
        {
            Candidate(0, [MiddleLaNoscea], MiddleLaNoscea),
            Candidate(1, [CentralShroud], CentralShroud),
        };

        var leg = HuntRoute.Next(candidates, WesternThanalan, Anywhere);

        Assert.Equal(0, leg!.Target.Entry);
        Assert.Equal(MiddleLaNoscea, leg.Zone.TerritoryId);
    }

    [Fact]
    public void ZoneFor_PrefersAZoneTheLogNames_OverAnotherTheMobWasSeenIn()
    {
        // Seen in North Shroud first, but the log sends this character's city to Central Shroud.
        var candidate = Candidate(0, [CentralShroud], NorthShroud, CentralShroud);

        var zone = HuntRoute.ZoneFor(candidate, WesternThanalan, Anywhere);

        Assert.Equal(CentralShroud, zone!.TerritoryId);
    }

    [Fact]
    public void ZoneFor_FollowsTheLogsOwnOrder()
    {
        var candidate = Candidate(0, [WesternThanalan, CentralShroud], CentralShroud, WesternThanalan);

        Assert.Equal(WesternThanalan, HuntRoute.ZoneFor(candidate, NorthShroud, Anywhere)!.TerritoryId);
    }

    [Fact]
    public void ZoneFor_SkipsAZoneWithNoWayIn()
    {
        var candidate = Candidate(0, [MiddleLaNoscea, CentralShroud], MiddleLaNoscea, CentralShroud);

        var zone = HuntRoute.ZoneFor(candidate, WesternThanalan, territory => territory != MiddleLaNoscea);

        Assert.Equal(CentralShroud, zone!.TerritoryId);
    }

    [Fact]
    public void ZoneFor_TheCurrentZoneNeedsNoWayIn()
    {
        var candidate = Candidate(0, [MiddleLaNoscea], MiddleLaNoscea);

        Assert.Equal(MiddleLaNoscea, HuntRoute.ZoneFor(candidate, MiddleLaNoscea, _ => false)!.TerritoryId);
    }

    [Fact]
    public void Next_IsNull_WhenNoZoneCanBeReached()
    {
        var candidates = new[] { Candidate(0, [MiddleLaNoscea], MiddleLaNoscea) };

        Assert.Null(HuntRoute.Next(candidates, WesternThanalan, _ => false));
    }

    [Fact]
    public void Next_PassesOverAnUnreachableTarget_ForOneThatCanBeDone()
    {
        var candidates = new[]
        {
            Candidate(0, [MiddleLaNoscea], MiddleLaNoscea),
            Candidate(1, [CentralShroud], CentralShroud),
        };

        var leg = HuntRoute.Next(candidates, WesternThanalan, territory => territory == CentralShroud);

        Assert.Equal(1, leg!.Target.Entry);
    }
}
