using System.Numerics;
using Orion.Services.Farm;
using Orion.Services.HuntLog;

namespace Orion.Tests.Services.Farm;

public sealed class TrophyRouteTests
{
    private const uint Forelands = 398;
    private const uint ChurningMists = 400;
    private const uint WesternHighlands = 397;

    private static HuntSpots Zone(uint territory, int level, int reports)
        => new(territory, [new Vector2(1f, 2f)], level, reports);

    private static TrophyCandidate Mob(uint nameId, params HuntSpots[] zones)
        => new(new FarmMob(nameId, $"mob {nameId}"), zones);

    private static bool Anywhere(uint territory) => true;

    [Fact]
    public void TheZoneTheCharacterIsIn_Wins_EvenOverABetterReportedOne()
    {
        var candidates = new[]
        {
            Mob(1, Zone(Forelands, 53, 16)),
            Mob(2, Zone(ChurningMists, 55, 2)),
        };

        var leg = TrophyRoute.Pick(candidates, ChurningMists, playerLevel: 60, Anywhere);

        Assert.Equal(2u, leg!.Mob.NameId);
        Assert.Equal(ChurningMists, leg.Zone.TerritoryId);
    }

    [Fact]
    public void TheCurrentZoneNeedsNoWayIn_AndNoLevel()
    {
        var candidates = new[] { Mob(1, Zone(Forelands, 53, 16)) };

        Assert.Equal(Forelands, TrophyRoute.Pick(candidates, Forelands, playerLevel: 10, _ => false)!.Zone.TerritoryId);
    }

    [Fact]
    public void Elsewhere_TheBestReportedZoneWithinTheCharactersLevel_IsChosen()
    {
        var candidates = new[]
        {
            Mob(1, Zone(Forelands, 53, 16)),
            Mob(2, Zone(ChurningMists, 55, 24)),
            Mob(3, Zone(WesternHighlands, 56, 40)), // the busiest, but above the character
        };

        var leg = TrophyRoute.Pick(candidates, currentTerritory: 134, playerLevel: 55, Anywhere);

        Assert.Equal(ChurningMists, leg!.Zone.TerritoryId);
    }

    [Fact]
    public void WhenEveryZoneIsAboveTheCharacter_TheLowestIsChosen()
    {
        var candidates = new[]
        {
            Mob(1, Zone(ChurningMists, 55, 24)),
            Mob(2, Zone(Forelands, 53, 2)),
        };

        Assert.Equal(Forelands, TrophyRoute.Pick(candidates, 134, playerLevel: 30, Anywhere)!.Zone.TerritoryId);
    }

    [Fact]
    public void AZoneWithNoWayIn_IsPassedOver()
    {
        var candidates = new[]
        {
            Mob(1, Zone(Forelands, 53, 16)),
            Mob(2, Zone(ChurningMists, 55, 2)),
        };

        var leg = TrophyRoute.Pick(candidates, 134, playerLevel: 60, territory => territory != Forelands);

        Assert.Equal(ChurningMists, leg!.Zone.TerritoryId);
    }

    [Fact]
    public void AnUnrecordedLevel_CountsAsWithinReach()
    {
        var candidates = new[] { Mob(1, Zone(Forelands, level: 0, reports: 3)) };

        Assert.NotNull(TrophyRoute.Pick(candidates, 134, playerLevel: 1, Anywhere));
    }

    [Fact]
    public void Nothing_WhenNoMobCanBePlacedOrReached()
    {
        Assert.Null(TrophyRoute.Pick([], 134, 60, Anywhere));
        Assert.Null(TrophyRoute.Pick([Mob(1, Zone(Forelands, 53, 16))], 134, 60, _ => false));
    }
}
