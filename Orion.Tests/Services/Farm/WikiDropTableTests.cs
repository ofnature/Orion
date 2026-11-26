using Orion.Services.Farm;

namespace Orion.Tests.Services.Farm;

public sealed class WikiDropTableTests
{
    // The drop tables of "Diremite Web" and "Fleece" as ffxiv.consolegameswiki.com rendered them on
    // 2026-11-26, cut to the rows the tests read, with the page's other tables around them.
    private const string Page = """
        <table class="npc sortable stdt__table_v2">
        <tbody><tr><th>Vendor</th><th>Location / Coordinates</th><th>Cost</th></tr>
        <tr><td><a href="/wiki/Some_Vendor">Some Vendor</a></td><td>Lakeland (1,2)</td><td>5</td></tr></tbody></table>
        <table class="item sortable stdt__table_v2">
        <tbody><tr>
        <th>Enemy</th>
        <th>Level</th>
        <th>Location
        </th></tr>
        <tr>
        <td><a href="/wiki/Banemite" title="Banemite">Banemite</a>
        </td>
        <td>25-31
        </td>
        <td><a href="/wiki/North_Shroud" title="North Shroud">North Shroud</a> <small>(25,25)</small>
        </td></tr>
        <tr>
        <td><a href="/wiki/Flame_Zonure" title="Flame Zonure">Flame Zonure</a>
        </td>
        <td>77
        </td>
        <td><a href="/wiki/Amh_Araeng" title="Amh Araeng">Amh Araeng</a> <small>(24.5,34.3)</small>
        </td></tr>
        <tr>
        <td><a href="/wiki/Mad_Karakul_Ewe" title="Mad Karakul Ewe">Mad Karakul Ewe</a>
        </td>
        <td>
        </td>
        <td><a href="/wiki/Coerthas_Central_Highlands" title="Coerthas Central Highlands">Coerthas Central Highlands</a>
        </td></tr></tbody></table>
        <table class="recipe sortable stdt__table_v2"><tbody><tr><th>Item</th></tr></tbody></table>
        """;

    [Fact]
    public void Parse_ReadsMobLevelZoneAndCoordinates()
    {
        var rows = WikiDropTable.Parse(Page);

        Assert.Equal(3, rows.Count); // the vendor and recipe tables are not drop tables
        Assert.Equal(new WikiDropRow("Banemite", 31, "North Shroud", 25f, 25f), rows[0]); // a range gives its top
        Assert.Equal(new WikiDropRow("Flame Zonure", 77, "Amh Araeng", 24.5f, 34.3f), rows[1]);
    }

    [Fact]
    public void Parse_ARowWithNoCoordinatesOrLevel_KeepsTheZone()
    {
        var row = WikiDropTable.Parse(Page)[2];

        Assert.Equal(new WikiDropRow("Mad Karakul Ewe", 0, "Coerthas Central Highlands", null, null), row);
    }

    [Fact]
    public void Parse_AnItemTableNotHeadedEnemy_IsNotADropTable()
    {
        // The "Leather" category page lists items in a table of the same class.
        const string category = """
            <table class="sortable item align-left stdt__table_v2"><tbody>
            <tr><th>Item</th><th>Level</th><th>Source</th></tr>
            <tr><td>Aldgoat Leather</td><td>15</td><td>Crafted</td></tr></tbody></table>
            """;

        Assert.Empty(WikiDropTable.Parse(category));
    }

    [Fact]
    public void ParseResponse_ReadsTheHtmlOutOfTheApisJson()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(
            new Dictionary<string, object> { ["parse"] = new Dictionary<string, object> { ["title"] = "Zonure Skin", ["text"] = new Dictionary<string, string> { ["*"] = Page } } });

        Assert.Equal(3, WikiDropTable.ParseResponse(json)!.Count);
    }

    [Fact]
    public void ParseResponse_IsNull_WhenTheWikiHasNoSuchPage()
    {
        const string missing = """{"error":{"code":"missingtitle","info":"The page you specified doesn't exist."}}""";

        Assert.Null(WikiDropTable.ParseResponse(missing));
    }

    [Fact]
    public void UrlFor_NamesThePageByTheItemName_WithUnderscores()
    {
        var url = WikiDropTable.UrlFor("Zonure Skin");

        Assert.EndsWith("&page=Zonure_Skin", url);
        Assert.EndsWith("&page=Tiger%27s_Eye", WikiDropTable.UrlFor("Tiger's Eye"));
    }

    [Theory]
    [InlineData("77", 77)]
    [InlineData("25-31", 31)]
    [InlineData("2 - 4", 4)]
    [InlineData("", 0)]
    public void HighestLevel_IsTheTopOfARange(string text, int expected)
        => Assert.Equal(expected, WikiDropTable.HighestLevel(text));
}
