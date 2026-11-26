using System.Numerics;
using Orion.Services.Farm;
using Orion.Services.HuntLog;

namespace Orion.Tests.Services.Farm;

public sealed class WikiDropSourceTests
{
    private const uint AmhAraeng = 815;

    // Amh Araeng's map: size factor 100, no offset.
    private static ResolvedZone? Zone(string name)
        => name == "Amh Araeng" ? new ResolvedZone(AmhAraeng, 493, 100, 0, 0, name) : null;

    private static IReadOnlyList<uint> Ids(string name) => name switch
    {
        "Flame Zonure" => [8563],
        "Banemite" => [11, 461], // the field mob and its dungeon namesake
        _ => [],
    };

    [Fact]
    public void Resolve_MatchesTheMob_AndTurnsTheMapCoordinatesIntoWorldOnes()
    {
        var rows = new[] { new WikiDropRow("Flame Zonure", 77, "Amh Araeng", 24.5f, 34.3f) };

        var dropper = Assert.Single(WikiDropSource.Resolve(rows, Ids, Zone));

        Assert.Equal(8563u, dropper.NameId);
        Assert.Equal("Flame Zonure", dropper.Name);
        Assert.Equal(77, dropper.Level);
        Assert.Equal(AmhAraeng, dropper.TerritoryId);
        Assert.Equal(FarmLocationHelper.MapCoordToWorld(24.5f, 100, 0), dropper.X);
        Assert.Equal(FarmLocationHelper.MapCoordToWorld(34.3f, 100, 0), dropper.Z);
    }

    [Fact]
    public void Resolve_TakesEveryIdThatCarriesTheName()
    {
        var rows = new[] { new WikiDropRow("Banemite", 31, "Amh Araeng", 25f, 25f) };

        Assert.Equal([11u, 461u], WikiDropSource.Resolve(rows, Ids, Zone).Select(d => d.NameId));
    }

    [Fact]
    public void Resolve_LeavesOutANameTheGameDoesNotHave()
    {
        var rows = new[] { new WikiDropRow("Wiki Typo", 10, "Amh Araeng", 1f, 1f) };

        Assert.Empty(WikiDropSource.Resolve(rows, Ids, Zone));
    }

    [Theory]
    [InlineData("Amh Araeng", null, null)] // the wiki gave the zone only
    [InlineData("Nowhere Known", 5f, 5f)]  // a zone Orion cannot place
    public void Resolve_KeepsTheMobWithoutAPlace(string zone, float? x, float? y)
    {
        var dropper = Assert.Single(WikiDropSource.Resolve([new WikiDropRow("Flame Zonure", 77, zone, x, y)], Ids, Zone));

        Assert.Equal(8563u, dropper.NameId);
        Assert.Equal(0u, dropper.TerritoryId);
    }

    [Fact]
    public void WhatIsRemembered_ReadsBackAsItWasWritten()
    {
        var known = new Dictionary<uint, List<WikiDropper>>
        {
            [27735] = [new WikiDropper(8563, "Flame Zonure", 77, AmhAraeng, 123.5f, -45.25f)],
        };

        var back = WikiDropSource.Read(WikiDropSource.Write(known));

        Assert.Equal(known[27735], back[27735]);
    }

    [Fact]
    public void Positions_AnAddedPlaceIsFound_OncePerTerritory()
    {
        // No asset at this path: the list starts empty (and warns), as on a broken install.
        var positions = new HuntPositions(Path.Combine(Path.GetTempPath(), "orion-no-such-asset.json"), null!, _ => { });
        var place = new HuntSpots(AmhAraeng, [new Vector2(1f, 2f)], 77, 0, [77]);

        Assert.Empty(positions.For(8563));
        positions.Add(8563, place);
        positions.Add(8563, place with { Level = 80 }); // the same territory again changes nothing

        Assert.Same(place, Assert.Single(positions.For(8563)));
    }
}
