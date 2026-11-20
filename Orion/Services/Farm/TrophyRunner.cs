using System;
using System.Collections.Generic;
using System.Numerics;
using Orion.Services.HuntLog;
using Orion.Services.Travel;

namespace Orion.Services.Farm;

/// <summary>
/// Trophy Hunt's travel: with an item and its mobs set but no spot marked, picks a zone one of
/// the mobs has been seen in (<see cref="TrophyRoute"/>), goes there (<see cref="SpotTravel"/>),
/// marks the recorded spots, and starts farm mode. From then on the run is farm mode's own; this
/// is only busy while it travels.
/// </summary>
public sealed class TrophyRunner : IDisposable
{
    private readonly FarmModeService _farm;
    private readonly HuntPositions _positions;
    private readonly SpotTravel _travel;
    private readonly ITravelWorld _world;
    private readonly Func<int> _playerLevel;
    private readonly Func<bool> _otherRunBusy;
    private readonly Func<string?> _missingPlugin;
    private readonly Action<string> _say;

    private TrophyLeg? _leg;

    public bool IsRunning { get; private set; }
    public string StatusLine => IsRunning ? _travel.StatusLine : "Idle";

    /// <param name="otherRunBusy">Something else of Orion's is driving the character (the hunt log run).</param>
    public TrophyRunner(
        FarmModeService farm,
        HuntPositions positions,
        SpotTravel travel,
        ITravelWorld world,
        Func<int> playerLevel,
        Func<bool> otherRunBusy,
        Func<string?> missingPlugin,
        Action<string> say)
    {
        _farm = farm;
        _positions = positions;
        _travel = travel;
        _world = world;
        _playerLevel = playerLevel;
        _otherRunBusy = otherRunBusy;
        _missingPlugin = missingPlugin;
        _say = say;
    }

    /// <summary>Where a start would go for the mobs listed now, or null when none can be placed and reached.</summary>
    public TrophyLeg? Plan()
    {
        var candidates = new List<TrophyCandidate>();
        foreach (var mob in _farm.Profile.Mobs)
        {
            var zones = _positions.For(mob.NameId);
            if (zones.Count > 0)
                candidates.Add(new TrophyCandidate(mob, zones));
        }
        return TrophyRoute.Pick(
            candidates, _world.TerritoryId, _playerLevel(), territory => _world.RouteTo(territory, Vector3.Zero) is not null);
    }

    /// <summary>Sets off for the mobs. Returns why not, or null when it started.</summary>
    public string? Start()
    {
        var profile = _farm.Profile;
        if (IsRunning)
            return "already on the way";
        if (_farm.IsRunning || _otherRunBusy())
            return "another run is going — stop it first";
        if (profile.Hunt != null || profile.ItemId == 0 || profile.TargetCount <= 0 || profile.Mobs.Count == 0)
            return "needs an item, a target count and at least one mob";
        if (_missingPlugin() is { } missing)
            return $"{missing} is not loaded — travelling to the mobs needs Lifestream and Ariadne (or mark a spot yourself)";
        if (Plan() is not { } leg)
            return "none of the listed mobs has a recorded position in a zone this character can reach — mark a spot yourself";

        _leg = leg;
        IsRunning = true;
        _travel.Begin(leg.Zone, leg.Mob.Name);
        _say($"Trophy Hunt: heading to {Zones.Describe(leg.Zone.TerritoryId)} for {leg.Mob.Name}.");
        return null;
    }

    /// <summary>Framework-thread tick.</summary>
    public void Tick()
    {
        if (!IsRunning)
            return;
        if (_world.IsDead)
        {
            Stop("the character died");
            return;
        }

        switch (_travel.Tick())
        {
            case TravelStatus.Done:
                // Arrived: the recorded spots become the profile's, and farm mode takes it from here.
                IsRunning = false;
                var profile = _farm.Profile;
                profile.Spots.Clear();
                profile.Spots.AddRange(_travel.Spots);
                profile.TerritoryId = (ushort)_leg!.Zone.TerritoryId;
                if (_farm.Start() is { } error)
                    _say($"Trophy Hunt: arrived, but the farm would not start — {error}.");
                break;

            case TravelStatus.Failed:
                Stop(_travel.FailReason);
                break;
        }
    }

    public void Stop(string reason)
    {
        if (!IsRunning)
            return;

        IsRunning = false;
        _travel.Cancel();
        _say($"Trophy Hunt: travel stopped — {reason}.");
    }

    public void Dispose() => Stop("plugin unloading");
}
