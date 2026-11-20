using Orion.Services.Farm;

namespace Orion.Tests.Services.Farm;

public sealed class DropSourcesTests
{
    [Fact]
    public void Parse_ReadsItemsAndTheirDroppers_AndLeavesOutWhatItCannotRead()
    {
        var drops = DropSources.Parse("""{ "5291": [37, 5], "12569": [3576], "bad": [1], "7": [], "8": "x" }""");

        Assert.Equal(2, drops.Count);
        Assert.Equal(new uint[] { 37, 5 }, drops[5291]);
        Assert.Equal(new uint[] { 3576 }, drops[12569]);
    }

    [Fact]
    public void MobsFor_GivesAnItemsDroppers_AndNothingForAnItemItDoesNotKnow()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", DropSources.FileName);
        var drops = new DropSources(path, _ => { });

        Assert.Contains(37u, drops.MobsFor(5291)); // ground squirrels drop Animal Skin
        Assert.Empty(drops.MobsFor(1));            // gil is not on the list
    }

    [Fact]
    public void AMissingFile_LeavesTheListEmpty_AndSaysSo()
    {
        var warnings = new List<string>();
        var drops = new DropSources(Path.Combine(AppContext.BaseDirectory, "no-such-file.json"), warnings.Add);

        Assert.Empty(drops.ItemIds);
        Assert.Single(warnings);
    }

    [Fact]
    public void ShippedAsset_Parses_AndKnowsTheCommonDrops()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", DropSources.FileName);
        Assert.True(File.Exists(path), $"the asset was not copied beside the tests: {path}");

        var drops = DropSources.Parse(File.ReadAllText(path));

        // 237 items when this was fetched (2026-11-19).
        Assert.True(drops.Count >= 200, $"only {drops.Count} items in the asset");
        Assert.True(drops.ContainsKey(5291));  // Animal Skin
        Assert.True(drops.ContainsKey(12569)); // Wyvern Skin
    }
}
