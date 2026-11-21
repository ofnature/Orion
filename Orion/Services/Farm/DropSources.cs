using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Orion.Services.Farm;

/// <summary>
/// Which items mobs drop, from <c>Assets/teamcraft-drop-sources.json</c>: FFXIV Teamcraft's
/// <c>drop-sources.json</c> (MIT), shipped verbatim. Item id to the BNpcName ids of its droppers.
/// Drop tables are server-side, so the game's own sheets cannot say; this list is what players'
/// clients reported, and it is not complete (Zonure Skin and Smilodon Skin were missing when it
/// was fetched), which is why the search filter built on it can be switched off.
/// </summary>
public sealed class DropSources
{
    public const string FileName = "teamcraft-drop-sources.json";

    private readonly Dictionary<uint, uint[]> _mobsByItem;

    public static string PathBeside(string? assemblyDirectory)
        => Path.Combine(assemblyDirectory ?? string.Empty, "Assets", FileName);

    /// <summary>Reads the asset. A missing or broken file leaves the list empty, with a warning.</summary>
    public DropSources(string path, Action<string> warn)
    {
        try
        {
            _mobsByItem = Parse(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            _mobsByItem = new Dictionary<uint, uint[]>();
            warn($"The dropped-items list could not be read from {path} ({ex.GetType().Name}: {ex.Message}) — the item search lists every item.");
        }
    }

    /// <summary>Every item the list knows a dropper for.</summary>
    public IReadOnlyCollection<uint> ItemIds => _mobsByItem.Keys;

    /// <summary>The BNpcName ids of the mobs known to drop an item; empty when the list does not know it.</summary>
    public IReadOnlyList<uint> MobsFor(uint itemId)
        => _mobsByItem.TryGetValue(itemId, out var mobs) ? mobs : [];

    /// <summary>
    /// The mobs known to drop an item, named for the mob list. <paramref name="nameOf"/> is the
    /// BNpcName sheet's singular, which is lowercase ("ground squirrel"); a mob it has no name for
    /// is left out.
    /// </summary>
    public IReadOnlyList<FarmMob> NamedMobsFor(uint itemId, Func<uint, string?> nameOf)
    {
        var mobs = new List<FarmMob>();
        foreach (var nameId in MobsFor(itemId))
        {
            var name = nameOf(nameId);
            if (string.IsNullOrEmpty(name))
                continue;
            mobs.Add(new FarmMob(nameId, char.IsLower(name[0]) ? char.ToUpperInvariant(name[0]) + name[1..] : name));
        }
        return mobs;
    }

    /// <summary>Teamcraft's file: <c>{ "item id": [BNpcName id, ...], ... }</c>.</summary>
    internal static Dictionary<uint, uint[]> Parse(string json)
    {
        var result = new Dictionary<uint, uint[]>();
        using var doc = JsonDocument.Parse(json);
        foreach (var item in doc.RootElement.EnumerateObject())
        {
            if (!uint.TryParse(item.Name, out var itemId) || item.Value.ValueKind != JsonValueKind.Array)
                continue;
            var mobs = new List<uint>();
            foreach (var mob in item.Value.EnumerateArray())
            {
                if (mob.ValueKind == JsonValueKind.Number && mob.TryGetUInt32(out var nameId))
                    mobs.Add(nameId);
            }
            if (mobs.Count > 0)
                result[itemId] = mobs.ToArray();
        }
        return result;
    }
}
