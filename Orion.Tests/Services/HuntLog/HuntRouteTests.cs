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

    /// <summary>A zone with one spot per level given: spot i stands at (i, i).</summary>
    private static HuntSpots Levelled(uint territory, params int[] levels)
        => new(territory, levels.Select((_, i) => new Vector2(i, i)).ToList(), levels.Max(), 0, levels);

    private static HuntCandidate Seen(uint[] logZones, params HuntSpots[] zones)
    {
        var named = logZones.Select(t => new HuntLogZone(t, $"zone {t}", "")).ToList();
        return new HuntCandidate(new HuntLogTarget(0, 0, 0, 0, 100, "mob", 3, named), zones);
    }

    [Fact]
    public void ZoneFor_TakesTheZoneWhereTheMobIsLowest_OverTheOneTheLogNames()
    {
        // The same name at 34 where the log points, and at 12 somewhere else.
        var candidate = Seen([CentralShroud], Levelled(CentralShroud, 34), Levelled(NorthShroud, 12));

        var zone = HuntRoute.ZoneFor(candidate, WesternThanalan, Anywhere);

        Assert.Equal(NorthShroud, zone!.TerritoryId);
    }

    [Fact]
    public void ZoneFor_LeavesTheCurrentZone_WhenTheMobIsHarderHere()
    {
        var candidate = Seen([CentralShroud], Levelled(CentralShroud, 34), Levelled(NorthShroud, 12));

        Assert.Equal(NorthShroud, HuntRoute.ZoneFor(candidate, CentralShroud, Anywhere)!.TerritoryId);
    }

    [Fact]
    public void ZoneFor_StaysPut_WhenTheMobHereIsTheSameGroupOfLevels()
    {
        // 14 here against 12 elsewhere is one group of mobs, not a harder variant: no trip.
        var candidate = Seen([NorthShroud], Levelled(CentralShroud, 14), Levelled(NorthShroud, 12));

        Assert.Equal(CentralShroud, HuntRoute.ZoneFor(candidate, CentralShroud, Anywhere)!.TerritoryId);
    }

    [Fact]
    public void ZoneFor_TheLowestZoneMustBeReachable()
    {
        var candidate = Seen([CentralShroud], Levelled(CentralShroud, 34), Levelled(NorthShroud, 12));

        var zone = HuntRoute.ZoneFor(candidate, WesternThanalan, territory => territory != NorthShroud);

        Assert.Equal(CentralShroud, zone!.TerritoryId);
    }

    [Fact]
    public void ZoneFor_AZoneWithALevelBeatsOneWithNone()
    {
        var unknown = new HuntSpots(CentralShroud, [new Vector2(1f, 2f)]);
        var candidate = Seen([CentralShroud], unknown, Levelled(NorthShroud, 40));

        Assert.Equal(NorthShroud, HuntRoute.ZoneFor(candidate, WesternThanalan, Anywhere)!.TerritoryId);
    }

    [Fact]
    public void Easiest_KeepsOnlyTheLowestGroupOfSpots_LowestFirst()
    {
        // Busiest first as the asset writes them: two level groups in one zone, and one unknown.
        var zone = Levelled(CentralShroud, 48, 7, 34, 5, 0, 5);

        var easy = HuntRoute.Easiest(zone);

        Assert.Equal([5, 5, 7], easy.SpotLevels);
        Assert.Equal([new Vector2(3, 3), new Vector2(5, 5), new Vector2(1, 1)], easy.Spots);
        Assert.Equal(7, easy.Level);
        Assert.Equal(8, HuntRoute.LevelCap(easy)); // nothing above the lowest group is attacked
    }

    [Fact]
    public void Harder_IsTheSpotsAboveTheLowestGroup_AndNotTheUnknownOnes()
    {
        // Spot i stands at (i, i): 48 and 34 are harder than the 5-to-7 group; the 0 is unknown.
        var zone = Levelled(CentralShroud, 48, 7, 34, 5, 0, 5);

        Assert.Equal([new Vector2(0, 0), new Vector2(2, 2)], HuntRoute.Harder(zone));
    }

    [Fact]
    public void Harder_IsEmpty_WhenNoLevelIsKnown_OrAllAreOneGroup()
    {
        Assert.Empty(HuntRoute.Harder(new HuntSpots(CentralShroud, [new Vector2(1f, 2f)])));
        Assert.Empty(HuntRoute.Harder(Levelled(CentralShroud, 20, 23, 21)));
    }

    [Fact]
    public void Easiest_WithNoLevelsKnown_ChangesNothing_AndSetsNoLimit()
    {
        var zone = new HuntSpots(CentralShroud, [new Vector2(1f, 2f), new Vector2(3f, 4f)]);

        Assert.Same(zone, HuntRoute.Easiest(zone));
        Assert.Equal(0, HuntRoute.LevelCap(zone));
    }

    [Fact]
    public void Next_HandsBackOnlyTheLowestSpotsOfTheZone()
    {
        var candidates = new[] { Seen([CentralShroud], Levelled(CentralShroud, 48, 34)) };

        var leg = HuntRoute.Next(candidates, WesternThanalan, Anywhere);

        Assert.Equal([34], leg!.Zone.SpotLevels);
    }

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
