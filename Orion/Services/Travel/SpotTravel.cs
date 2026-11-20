using System;
using System.Collections.Generic;
using System.Numerics;
using Orion.Services.HuntLog;

namespace Orion.Services.Travel;

/// <summary>
/// Getting to a mob's recorded spots: into the zone (its aetheryte, when it is another zone), the
/// floor under each spot asked of that zone's mesh, then on to the first spot. What the hunt log
/// run and Trophy Hunt's travel share; what happens on arrival is theirs.
/// </summary>
public sealed class SpotTravel
{
    private enum Phase { Idle, EnteringZone, FindingGround, GoingToSpot }

    /// <summary>A zone just loaded may not have its mesh yet.</summary>
    private static readonly TimeSpan GroundWait = TimeSpan.FromSeconds(15);

    /// <summary>Spots kept for farm mode: enough to roam, few enough to come back round.</summary>
    private const int MaxSpots = 4;

    /// <summary>How near the first spot the travel leg ends; farm mode walks the rest.</summary>
    private const float SpotStopDistance = 15f;

    /// <summary>Arriving in a zone means standing by its aetheryte.</summary>
    private const float AetheryteStopDistance = 40f;

    private readonly ITravel _travel;
    private readonly ITravelWorld _world;
    private readonly Func<float, float, Vector3?> _floorAt;

    private Phase _phase;
    private DateTime _phaseStart;
    private HuntSpots? _zone;
    private string _what = string.Empty;
    private List<Vector3> _spots = new();

    public TravelStatus Status { get; private set; } = TravelStatus.Idle;
    public string FailReason { get; private set; } = string.Empty;
    public string StatusLine { get; private set; } = string.Empty;

    /// <summary>Once <see cref="TravelStatus.Done"/>: the spots with their floor heights, the one stood at first.</summary>
    public IReadOnlyList<Vector3> Spots => _spots;

    /// <param name="floorAt">The highest floor of the current zone's mesh at a world X and Z, or null.</param>
    public SpotTravel(ITravel travel, ITravelWorld world, Func<float, float, Vector3?> floorAt)
    {
        _travel = travel;
        _world = world;
        _floorAt = floorAt;
    }

    /// <summary>Set off for a zone's spots. <paramref name="what"/> names the mob, for the status line.</summary>
    public void Begin(HuntSpots zone, string what)
    {
        _zone = zone;
        _what = what;
        _spots = new List<Vector3>();
        FailReason = string.Empty;
        Status = TravelStatus.Running;

        var now = _world.UtcNow;
        if (zone.TerritoryId == _world.TerritoryId)
        {
            Enter(Phase.FindingGround, now);
            return;
        }

        // Another zone: its aetheryte first. The spots' heights can only be asked of that zone's mesh.
        var first = zone.Spots[0];
        var near = new Vector3(first.X, 0f, first.Y);
        var arrival = _world.NearestAttunedAetheryte(zone.TerritoryId, near) is { } aetheryte
            ? _world.AetherytePosition(aetheryte) ?? near
            : near;
        _travel.Begin(zone.TerritoryId, arrival, fly: false, land: false, AetheryteStopDistance);
        StatusLine = $"Travelling to {Zones.Describe(zone.TerritoryId)} for {what}";
        Enter(Phase.EnteringZone, now);
    }

    public TravelStatus Tick()
    {
        if (Status != TravelStatus.Running)
            return Status;

        var now = _world.UtcNow;
        switch (_phase)
        {
            case Phase.EnteringZone:
                switch (_travel.Tick())
                {
                    case TravelStatus.Done:
                        Enter(Phase.FindingGround, now);
                        break;
                    case TravelStatus.Failed:
                        Fail(_travel.FailReason);
                        break;
                }
                break;

            case Phase.FindingGround:
                FindGround(now);
                break;

            case Phase.GoingToSpot:
                switch (_travel.Tick())
                {
                    case TravelStatus.Done:
                        _phase = Phase.Idle;
                        Status = TravelStatus.Done;
                        break;
                    case TravelStatus.Failed:
                        Fail(_travel.FailReason);
                        break;
                }
                break;
        }
        return Status;
    }

    public void Cancel()
    {
        if (Status == TravelStatus.Running)
            _travel.Cancel();
        _phase = Phase.Idle;
        Status = TravelStatus.Idle;
    }

    /// <summary>In the zone: ask the mesh for the floor under each recorded spot, then go to the first.</summary>
    private void FindGround(DateTime now)
    {
        var zone = _zone!;
        _spots = new List<Vector3>();
        foreach (var spot in zone.Spots)
        {
            if (_floorAt(spot.X, spot.Y) is { } floor)
                _spots.Add(floor);
            if (_spots.Count == MaxSpots)
                break;
        }

        if (_spots.Count == 0)
        {
            StatusLine = $"Finding the ground for {_what}";
            if (now - _phaseStart > GroundWait)
                Fail("the navmesh has no floor at its recorded positions");
            return;
        }

        _travel.Begin(zone.TerritoryId, _spots[0], fly: true, land: true, SpotStopDistance);
        StatusLine = $"Going to {_what}";
        Enter(Phase.GoingToSpot, now);
    }

    private void Fail(string reason)
    {
        _phase = Phase.Idle;
        FailReason = reason;
        Status = TravelStatus.Failed;
    }

    private void Enter(Phase phase, DateTime now)
    {
        _phase = phase;
        _phaseStart = now;
    }
}
