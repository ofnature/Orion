using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Orion.Services.Farm;

/// <summary>
/// One row of an item page's drop table: the mob, the highest level the wiki gives it (0 when it
/// gives none), the zone, and the map coordinates when the wiki has them.
/// </summary>
public readonly record struct WikiDropRow(string Mob, int Level, string Zone, float? MapX, float? MapY);

/// <summary>
/// Reads the "dropped by" table off an item's page on the FFXIV Console Games Wiki, the way
/// Monster Loot Hunter does (danielbrenom/MonsterLootHunter, MIT: the endpoint and the table are
/// its find, the parser is Orion's). The page is asked for through MediaWiki's parse API, which
/// answers with the rendered HTML inside JSON; the table is the one classed <c>item</c> whose
/// heading row names an Enemy.
/// </summary>
public static partial class WikiDropTable
{
    private const string ApiUrl =
        "https://ffxiv.consolegameswiki.com/mediawiki/api.php?action=parse&prop=text&redirects=1&format=json&page={0}";

    [GeneratedRegex("<table[^>]*\\bclass=\"([^\"]*)\"[^>]*>(.*?)</table>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex TableRegex();

    [GeneratedRegex("<tr[^>]*>(.*?)</tr>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex RowRegex();

    [GeneratedRegex("<td[^>]*>(.*?)</td>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex CellRegex();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\(\s*(\d+(?:\.\d+)?)\s*,\s*(\d+(?:\.\d+)?)\s*\)")]
    private static partial Regex CoordinatesRegex();

    [GeneratedRegex(@"\d+")]
    private static partial Regex NumberRegex();

    /// <summary>The address that answers with an item's page. The wiki names pages by the English item name.</summary>
    public static string UrlFor(string englishItemName)
        => string.Format(CultureInfo.InvariantCulture, ApiUrl, Uri.EscapeDataString(englishItemName.Trim().Replace(' ', '_')));

    /// <summary>
    /// The drop table in the API's answer. Null when the wiki has no such page; empty when the page
    /// has no drop table.
    /// </summary>
    public static IReadOnlyList<WikiDropRow>? ParseResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("parse", out var parse)
            || !parse.TryGetProperty("text", out var text)
            || !text.TryGetProperty("*", out var html)
            || html.ValueKind != JsonValueKind.String)
            return null;
        return Parse(html.GetString() ?? string.Empty);
    }

    /// <summary>The drop table in a page's HTML; empty when there is none.</summary>
    public static IReadOnlyList<WikiDropRow> Parse(string html)
    {
        var rows = new List<WikiDropRow>();
        foreach (Match table in TableRegex().Matches(html))
        {
            // Other tables share the class (the Leather category page's item list): only the one
            // headed "Enemy" is a drop table.
            var classes = table.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var body = table.Groups[2].Value;
            if (Array.IndexOf(classes, "item") < 0 || !body.Contains(">Enemy", StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (Match row in RowRegex().Matches(body))
            {
                var cells = CellRegex().Matches(row.Groups[1].Value);
                if (cells.Count < 3)
                    continue;

                var mob = Text(cells[0].Groups[1].Value);
                if (mob.Length == 0)
                    continue;

                var place = Text(cells[^1].Groups[1].Value);
                var bracket = place.IndexOf('(');
                var zone = (bracket >= 0 ? place[..bracket] : place).Trim();

                float? x = null, y = null;
                var coordinates = CoordinatesRegex().Match(place);
                if (coordinates.Success)
                {
                    x = float.Parse(coordinates.Groups[1].Value, CultureInfo.InvariantCulture);
                    y = float.Parse(coordinates.Groups[2].Value, CultureInfo.InvariantCulture);
                }

                rows.Add(new WikiDropRow(mob, HighestLevel(Text(cells[1].Groups[1].Value)), zone, x, y));
            }
        }
        return rows;
    }

    /// <summary>"77" is 77, "25-31" is 31, and a cell with no number is 0.</summary>
    internal static int HighestLevel(string text)
    {
        var highest = 0;
        foreach (Match number in NumberRegex().Matches(text))
        {
            if (int.TryParse(number.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var level))
                highest = Math.Max(highest, level);
        }
        return highest;
    }

    private static string Text(string html)
        => WebUtility.HtmlDecode(TagRegex().Replace(html, string.Empty)).Replace(' ', ' ').Trim();
}
