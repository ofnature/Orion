using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Orion.Services.Game;
using Orion.Services.HuntLog;
using Orion.Services.Travel;

namespace Orion.Services.Farm;

/// <summary>
/// Trophy Hunt's start: switches to the class chosen to fight on, then either farms the spots the
/// player marked or, with none marked, picks a zone one of the mobs has been seen in
/// (<see cref="TrophyRoute"/>), goes there (<see cref="SpotTravel"/>), marks a patrol around the
/// recorded spots (<see cref="SearchPoints"/>), and starts farm mode. From then on the run is farm
/// mode's own; this is only busy while it switches and travels.
/// </summary>
public sealed class TrophyRunner : IDisposable
{
    private enum Phase { Idle, Switching, Travelling }

    /// <summary>A gearset change shows in the class within a moment; past this it did not take.</summary>
    private static readonly TimeSpan SwitchTimeout = TimeSpan.FromSeconds(5);

    private readonly FarmModeService _farm;
    private readonly HuntPositions _positions;
    private readonly SpotTravel _travel;
    private readonly ITravelWorld _world;
    private readonly IFightClassWorld _classes;
    private readonly Func<FightAs> _fightAs;
    private readonly Func<float, float, Vector3?> _floorAt;
    private readonly Func<float> _searchRadius;
    private readonly Func<int> _playerLevel;
    private readonly Func<bool> _otherRunBusy;
    private readonly Func<string?> _missingPlugin;
    private readonly Action<string> _say;

    private Phase _phase;
    private TrophyLeg? _leg;

    // The patrol this marked on its last trip and the area it goes with, to tell its own spots
    // from ones the player marked.
    private Vector3[] _patrol = [];
    private uint _patrolTerritory;
    private FarmArea? _area;
    private GearsetInfo _switchingTo;
    private DateTime _switchStarted;

    public bool IsRunning => _phase != Phase.Idle;

    public string StatusLine => _phase switch
    {
        Phase.Switching => $"Switching to {_switchingTo.ClassName}",
        Phase.Travelling => _travel.StatusLine,
        _ => "Idle",
    };

    /// <param name="floorAt">The floor of the current zone's mesh at a world X and Z, or null.</param>
    /// <param name="searchRadius">How far around each recorded spot the patrol reaches, in yalms.</param>
    /// <param name="otherRunBusy">Something else of Orion's is driving the character (the hunt log run).</param>
    public TrophyRunner(
        FarmModeService farm,
        HuntPositions positions,
        SpotTravel travel,
        ITravelWorld world,
        IFightClassWorld classes,
        Func<FightAs> fightAs,
        Func<float, float, Vector3?> floorAt,
        Func<float> searchRadius,
        Func<int> playerLevel,
        Func<bool> otherRunBusy,
        Func<string?> missingPlugin,
        Action<string> say)
    {
        _farm = farm;
        _positions = positions;
        _travel = travel;
        _world = world;
        _classes = classes;
        _fightAs = fightAs;
        _floorAt = floorAt;
        _searchRadius = searchRadius;
        _playerLevel = playerLevel;
        _otherRunBusy = otherRunBusy;
        _missingPlugin = missingPlugin;
        _say = say;
    }

    /// <summary>The gearset START would fight on, or null when it keeps the class being played.</summary>
    public GearsetInfo? FightClass()
        => _fightAs() is var choice && choice == FightAs.Current
            ? null
            : FightClassPicker.Pick(_classes.Gearsets(), choice, _classes.CurrentClassJob);

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

        // The zone is chosen for the class that will do the fighting, not the one standing here.
        var level = FightClass() is { } set ? set.Level : _playerLevel();
        return TrophyRoute.Pick(
            candidates, _world.TerritoryId, level, territory => _world.RouteTo(territory, Vector3.Zero) is not null);
    }

    /// <summary>Starts the run. Returns why not, or null when it started.</summary>
    public string? Start()
    {
        var profile = _farm.Profile;
        if (IsRunning)
            return "already on the way";
        if (_farm.IsRunning || _otherRunBusy())
            return "another run is going — stop it first";
        if (profile.Hunt != null || profile.ItemId == 0 || profile.TargetCount <= 0 || profile.Mobs.Count == 0)
            return "needs an item, a target count and at least one mob";

        var marked = profile.Spots.Count > 0;
        if (!marked)
        {
            if (_missingPlugin() is { } missing)
                return $"{missing} is not loaded — travelling to the mobs needs Lifestream and Ariadne (or mark a spot yourself)";
            if (Plan() is null)
                return "none of the listed mobs has a recorded position in a zone this character can reach — mark a spot yourself";
        }

        var choice = _fightAs();
        if (choice != FightAs.Current)
        {
            if (FightClass() is not { } set)
                return $"no gearset is saved for a {FightClassPicker.RoleName(choice)} class — save one, or set \"Fight as\" to the current class";

            if (set.ClassJobId != _classes.CurrentClassJob)
            {
                if (!_classes.EquipGearset(set.Id))
                    return $"the game refused the switch to {set.ClassName} — try again out of combat";
                _switchingTo = set;
                _switchStarted = _world.UtcNow;
                _phase = Phase.Switching;
                _say($"Trophy Hunt: switching to {set.ClassName} (level {set.Level}).");
                return null;
            }
        }

        return Go();
    }

    /// <summary>On the right class: farm the marked spots, or set off for the mobs.</summary>
    private string? Go()
    {
        _phase = Phase.Idle;
        var profile = _farm.Profile;
        if (profile.Spots.Count > 0)
        {
            // Spots still as Orion marked them on the last trip: the same roaming run again.
            // Spots the player marked or changed: a camp on them, kept to the leash.
            var own = _area != null && profile.TerritoryId == _patrolTerritory && profile.Spots.SequenceEqual(_patrol);
            return _farm.Start(own ? _area : null);
        }

        if (Plan() is not { } leg)
            return "none of the listed mobs has a recorded position in a zone this character can reach — mark a spot yourself";

        _leg = leg;
        _phase = Phase.Travelling;
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

        if (_phase == Phase.Switching)
        {
            if (_classes.CurrentClassJob == _switchingTo.ClassJobId)
            {
                if (Go() is { } error)
                    _say($"Trophy Hunt: on {_switchingTo.ClassName}, but the run would not start — {error}.");
            }
            else if (_world.UtcNow - _switchStarted > SwitchTimeout)
            {
                Stop($"the class did not change to {_switchingTo.ClassName}");
            }
            return;
        }

        switch (_travel.Tick())
        {
            case TravelStatus.Done:
                // Arrived: a patrol around the recorded spots becomes the profile's, and farm mode
                // takes it from here.
                _phase = Phase.Idle;
                var profile = _farm.Profile;
                var radius = _searchRadius();
                _patrol = SearchPoints.Around(_travel.Spots, _floorAt, radius).ToArray();
                _patrolTerritory = _leg!.Zone.TerritoryId;
                _area = new FarmArea(_travel.Spots.ToArray(), SearchPoints.AreaRadius(radius));
                profile.Spots.Clear();
                profile.Spots.AddRange(_patrol);
                profile.TerritoryId = (ushort)_patrolTerritory;
                if (_farm.Start(_area) is { } error)
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

        _phase = Phase.Idle;
        _travel.Cancel();
        _say($"Trophy Hunt: stopped before the farm began — {reason}.");
    }

    public void Dispose() => Stop("plugin unloading");
}
