using Orion.Services.HuntLog;

namespace Orion.Tests.Services.HuntLog;

public sealed class HuntPositionsTests
{
    // The shape Tools/import_teamcraft_monsters.py writes.
    private const string Sample = """
        {
          "source": "FFXIV Teamcraft monsters.json (MIT)",
          "imported": "2026-11-19",
          "format": "mobs[BNpcName id] = [[Map id, map x, map y, reports], ...]",
          "mobs": {
            "49": [[15, 23.8, 22.98, 5], [15, 22.02, 23.35, 4], [20, 28.66, 24.0, 7]],
            "262": [[21, 23.3, 34, 2]],
            "bad": [[1, 2, 3, 4]],
            "7": [[1, 2]]
          }
        }
        """;

    [Fact]
    public void Parse_ReadsEachMobsSpots_InTheOrderWritten()
    {
        var mobs = HuntPositions.Parse(Sample);

        var ladybug = mobs[49];
        Assert.Equal(3, ladybug.Count);
        Assert.Equal(new RecordedSpot(15, 23.8f, 22.98f, 5), ladybug[0]);
        Assert.Equal(20u, ladybug[2].MapId);
        Assert.Equal(34f, mobs[262][0].MapY); // a whole number in the file is still a coordinate
    }

    [Fact]
    public void Parse_LeavesOutWhatItCannotRead()
    {
        var mobs = HuntPositions.Parse(Sample);

        Assert.Equal(2, mobs.Count);          // the non-numeric key and the short row are gone
        Assert.False(mobs.ContainsKey(7));
    }

    [Fact]
    public void ShippedAsset_Parses_AndCoversMostOfTheHuntLog()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", HuntPositions.FileName);
        Assert.True(File.Exists(path), $"the asset was not copied beside the tests: {path}");

        var mobs = HuntPositions.Parse(File.ReadAllText(path));

        // 284 of the hunt log's 362 mobs had non-FATE reports when this was imported (2026-11-19).
        Assert.True(mobs.Count >= 250, $"only {mobs.Count} mobs in the asset");
        Assert.True(mobs.ContainsKey(49)); // little ladybug: Gladiator 01
        Assert.All(mobs.Values, spots => Assert.All(spots, s => Assert.InRange(s.MapX, 1f, 45f)));
    }
}
