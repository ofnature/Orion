using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Numerics;
using System.Text.Json;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Orion.Services.HuntLog;

namespace Orion.Services.Farm;

/// <summary>
/// A mob the wiki says drops an item, matched to the game: its BNpcName id, and where the wiki
/// puts it as a territory and a world X and Z. <paramref name="TerritoryId"/> is 0 when the wiki
/// gave no coordinates or named a zone Orion could not place.
/// </summary>
public sealed record WikiDropper(uint NameId, string Name, int Level, uint TerritoryId, float X, float Z);

/// <summary>
/// The second drop source, asked only for an item the bundled Teamcraft list has no dropper for
/// (Zonure Skin and Smilodon Skin, which GarlandTools does not know either): the item's page on
/// the FFXIV Console Games Wiki (<see cref="WikiDropTable"/>). One request per item, sent off the
/// framework thread; what comes back is matched to the game on the framework thread, kept in
/// <c>wiki-droppers.json</c> in the plugin's config folder so it is asked once, and its places are
/// handed to <see cref="HuntPositions"/> so Trophy Hunt can travel to mobs the asset never heard of.
/// </summary>
public sealed class WikiDropSource : IDisposable
{
    public const string FileName = "wiki-droppers.json";

    private readonly IDataManager _data;
    private readonly HuntPositions _positions;
    private readonly string _path;
    private readonly Action<string> _warn;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    // Everything below is the framework thread's, but for the queue the lookups answer into.
    private readonly Dictionary<uint, List<WikiDropper>> _known;
    private readonly HashSet<uint> _looking = new();
    private readonly HashSet<uint> _answered = new();
    private readonly Dictionary<uint, string> _problems = new();
    private readonly ConcurrentQueue<(uint ItemId, IReadOnlyList<WikiDropRow>? Rows, string? Error)> _done = new();
    private Dictionary<string, List<uint>>? _mobIds;

    public WikiDropSource(IDataManager data, HuntPositions positions, string configDirectory, Action<string> warn)
    {
        _data = data;
        _positions = positions;
        _path = Path.Combine(configDirectory, FileName);
        _warn = warn;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("FFXIV-Dalamud-plugin");

        _known = new Dictionary<uint, List<WikiDropper>>();
        try
        {
            if (File.Exists(_path))
                _known = Read(File.ReadAllText(_path));
        }
        catch (Exception ex)
        {
            _warn($"The remembered wiki droppers could not be read from {_path} ({ex.GetType().Name}: {ex.Message}) — they will be asked for again.");
        }

        foreach (var droppers in _known.Values)
            Place(droppers);
    }

    /// <summary>The mobs the wiki named for an item, as far as it has been asked; empty when it has not, or named none.</summary>
    public IReadOnlyList<FarmMob> MobsFor(uint itemId)
        => _known.TryGetValue(itemId, out var droppers)
            ? droppers.Select(d => new FarmMob(d.NameId, d.Name)).ToList()
            : [];

    /// <summary>The wiki is being asked about this item right now.</summary>
    public bool IsLooking(uint itemId) => _looking.Contains(itemId);

    /// <summary>Why the last lookup for an item gave no mob, or null when there was none or it gave some.</summary>
    public string? Problem(uint itemId) => _problems.GetValueOrDefault(itemId);

    /// <summary>
    /// Asks the wiki who drops an item, unless that is known, under way, or already answered with
    /// nothing this session. A lookup that failed on the way is asked again.
    /// </summary>
    public void Request(uint itemId)
    {
        if (_known.ContainsKey(itemId) || _looking.Contains(itemId) || _answered.Contains(itemId))
            return;

        // The wiki names its pages in English, whatever language the client runs in.
        var name = _data.GetExcelSheet<Lumina.Excel.Sheets.Item>(Dalamud.Game.ClientLanguage.English)
            .GetRowOrDefault(itemId)?.Name.ExtractText();
        if (string.IsNullOrEmpty(name))
        {
            _problems[itemId] = "the game has no English name for it to look up";
            return;
        }

        _problems.Remove(itemId);
        _looking.Add(itemId);
        var url = WikiDropTable.UrlFor(name);
        _ = Task.Run(async () =>
        {
            try
            {
                var json = await _http.GetStringAsync(url).ConfigureAwait(false);
                _done.Enqueue((itemId, WikiDropTable.ParseResponse(json) ?? [], null));
            }
            catch (Exception ex)
            {
                _done.Enqueue((itemId, null, ex.Message));
            }
        });
    }

    /// <summary>Framework-thread tick: takes in the lookups that came back.</summary>
    public void Tick()
    {
        while (_done.TryDequeue(out var answer))
        {
            _looking.Remove(answer.ItemId);
            if (answer.Rows is not { } rows)
            {
                _problems[answer.ItemId] = $"the wiki lookup failed ({answer.Error})";
                continue;
            }

            _answered.Add(answer.ItemId);
            var droppers = Resolve(rows, IdsFor, zone => FarmLocationHelper.ResolveZoneByName(_data, zone));
            if (droppers.Count == 0)
            {
                _problems[answer.ItemId] = rows.Count == 0
                    ? "the wiki lists no mob that drops it"
                    : $"the wiki names {string.Join(", ", rows.Select(r => r.Mob).Distinct())}, which matched no mob in the game";
                continue;
            }

            _known[answer.ItemId] = droppers;
            Place(droppers);
            Save();
        }
    }

    /// <summary>
    /// Wiki rows to game mobs. A name can belong to several BNpcName ids (an overworld mob and its
    /// dungeon namesake): all of them are taken, since nothing here says which one stands in the
    /// field. A row whose name matches no mob is left out.
    /// </summary>
    internal static List<WikiDropper> Resolve(
        IReadOnlyList<WikiDropRow> rows, Func<string, IReadOnlyList<uint>> idsFor, Func<string, ResolvedZone?> zoneFor)
    {
        var droppers = new List<WikiDropper>();
        foreach (var row in rows)
        {
            uint territory = 0;
            float x = 0f, z = 0f;
            if (row is { MapX: { } mapX, MapY: { } mapY } && zoneFor(row.Zone) is { } zone)
            {
                territory = zone.TerritoryId;
                x = FarmLocationHelper.MapCoordToWorld(mapX, zone.SizeFactor, zone.OffsetX);
                z = FarmLocationHelper.MapCoordToWorld(mapY, zone.SizeFactor, zone.OffsetY);
            }

            foreach (var nameId in idsFor(row.Mob))
                droppers.Add(new WikiDropper(nameId, row.Mob, row.Level, territory, x, z));
        }
        return droppers;
    }

    internal static Dictionary<uint, List<WikiDropper>> Read(string json)
        => JsonSerializer.Deserialize<Dictionary<uint, List<WikiDropper>>>(json) ?? new Dictionary<uint, List<WikiDropper>>();

    internal static string Write(Dictionary<uint, List<WikiDropper>> known)
        => JsonSerializer.Serialize(known);

    /// <summary>Hands the places the wiki gave to the position list, for mobs it has none of its own for.</summary>
    private void Place(IReadOnlyList<WikiDropper> droppers)
    {
        foreach (var dropper in droppers)
        {
            if (dropper.TerritoryId != 0)
                _positions.Add(dropper.NameId, new HuntSpots(dropper.TerritoryId, [new Vector2(dropper.X, dropper.Z)], dropper.Level, 0, [dropper.Level]));
        }
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(_path, Write(_known));
        }
        catch (Exception ex)
        {
            _warn($"The wiki droppers could not be saved to {_path} ({ex.GetType().Name}: {ex.Message}) — they will be asked for again next time.");
        }
    }

    /// <summary>Every BNpcName id with this English name. The index is built on first use.</summary>
    private IReadOnlyList<uint> IdsFor(string mobName)
    {
        if (_mobIds == null)
        {
            _mobIds = new Dictionary<string, List<uint>>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in _data.GetExcelSheet<Lumina.Excel.Sheets.BNpcName>(Dalamud.Game.ClientLanguage.English))
            {
                var singular = row.Singular.ExtractText().Trim();
                if (singular.Length == 0)
                    continue;
                if (!_mobIds.TryGetValue(singular, out var ids))
                    _mobIds[singular] = ids = new List<uint>();
                ids.Add(row.RowId);
            }
        }
        return _mobIds.TryGetValue(mobName.Trim(), out var found) ? found : [];
    }

    public void Dispose() => _http.Dispose();
}
