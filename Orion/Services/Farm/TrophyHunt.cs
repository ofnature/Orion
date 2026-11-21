using System;
using System.Collections.Generic;
using Orion.Config;

namespace Orion.Services.Farm;

/// <summary>
/// A Trophy Hunt set up for an item rather than through the window: the item's known droppers
/// become the mob list, no spot is marked, and <see cref="TrophyRunner"/> takes it from there
/// (the class switch, the trip to the mobs, the patrol). What a hunting list runs for each item.
/// </summary>
public sealed class TrophyHunt : ITrophyHunt
{
    private readonly FarmModeService _farm;
    private readonly TrophyRunner _trophy;
    private readonly Func<uint, IReadOnlyList<FarmMob>> _droppers;

    /// <param name="droppers">The mobs known to drop an item, named; empty when none is known.</param>
    public TrophyHunt(FarmModeService farm, TrophyRunner trophy, Func<uint, IReadOnlyList<FarmMob>> droppers)
    {
        _farm = farm;
        _trophy = trophy;
        _droppers = droppers;
    }

    public bool IsRunning => _trophy.IsRunning || _farm.IsRunning;

    public string StatusLine => _trophy.IsRunning ? _trophy.StatusLine : _farm.StatusLine;

    public string? Start(uint itemId, string name, int targetCount)
    {
        var mobs = _droppers(itemId);
        if (mobs.Count == 0)
            return "no mob is known to drop it";

        var profile = _farm.Profile;
        profile.Hunt = null;
        profile.ItemId = itemId;
        profile.ItemName = name;
        profile.TargetCount = targetCount;
        profile.ClearMobs();
        foreach (var mob in mobs)
            profile.AddMob(mob.NameId, mob.Name);
        profile.Spots.Clear();
        return _trophy.Start();
    }

    public void Stop(string reason)
    {
        if (_trophy.IsRunning)
            _trophy.Stop(reason);
        else
            _farm.Stop(reason);
    }
}

/// <summary>
/// The last Trophy Hunt started from the window, kept in the config so the item, the count and
/// the mob list are there again after a reload or a login. Spots are not kept: marked ones belong
/// to the zone the character stood in, and with none marked START travels to the mobs itself.
/// </summary>
public static class LastHunt
{
    public static SavedFarmProfile Snapshot(FarmProfile profile)
    {
        var saved = new SavedFarmProfile
        {
            ItemId = profile.ItemId,
            ItemName = profile.ItemName,
            TargetCount = profile.TargetCount,
            LeashRadiusYalms = profile.LeashRadiusYalms,
        };
        foreach (var mob in profile.Mobs)
        {
            saved.MobNameIds.Add(mob.NameId);
            saved.MobNames.Add(mob.Name);
        }
        return saved;
    }

    /// <summary>Put a saved hunt back as the working profile. Nothing happens for an empty one.</summary>
    public static void Restore(SavedFarmProfile? saved, FarmProfile profile)
    {
        if (saved is not { ItemId: not 0 })
            return;

        profile.Hunt = null;
        profile.ItemId = saved.ItemId;
        profile.ItemName = saved.ItemName;
        profile.TargetCount = Math.Max(1, saved.TargetCount);
        profile.LeashRadiusYalms = saved.LeashRadiusYalms;
        profile.ClearMobs();
        for (var i = 0; i < saved.MobNameIds.Count; i++)
            profile.AddMob(saved.MobNameIds[i], i < saved.MobNames.Count ? saved.MobNames[i] : $"mob {saved.MobNameIds[i]}");
        profile.Spots.Clear();
    }
}
