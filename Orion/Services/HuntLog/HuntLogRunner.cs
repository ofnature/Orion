using System;
using System.Collections.Generic;
using System.Numerics;
using Orion.Services.Farm;
using Orion.Services.Travel;

namespace Orion.Services.HuntLog;

/// <summary>
/// Works through the current rank of one hunt log on its own: picks a target that still wants
/// kills, travels to a zone it has been seen in (Lifestream into the zone, Ariadne to the spot),
/// hands the killing to farm mode with that target as its goal, and moves on when it is filled.
/// A rank that finishes rolls into the next one.
///
/// <para>
/// A target is skipped, with the reason said in chat, when nothing is recorded for it (dungeon
/// and FATE mobs), when no zone it lives in can be reached, or when none turns up for
/// <see cref="StallAfter"/>. The run ends when nothing is left that can be done.
/// </para>
/// </summary>
public sealed class HuntLogRunner : IDisposable
{
    private enum Phase { Idle, EnteringZone, FindingGround, GoingToSpot, Farming, Settling }

    /// <summary>No kill on a target for this long: its spots are wrong, empty or contested. Move on.</summary>
    public static readonly TimeSpan StallAfter = TimeSpan.FromMinutes(4);

    /// <summary>A zone just loaded may not have its mesh yet.</summary>
    private static readonly TimeSpan GroundWait = TimeSpan.FromSeconds(15);

    /// <summary>The game moves a log to its next rank a moment after the last kill.</summary>
    private static readonly TimeSpan RankSettle = TimeSpan.FromSeconds(5);

    /// <summary>Spots handed to farm mode for one target: enough to roam, few enough to come back round.</summary>
    private const int MaxSpots = 4;

    /// <summary>How near the first spot the travel leg ends; farm mode walks the rest.</summary>
    private const float SpotStopDistance = 15f;

    /// <summary>Arriving in a zone means standing by its aetheryte.</summary>
    private const float AetheryteStopDistance = 40f;

    private readonly HuntLogCatalog _catalog;
    private readonly HuntLogProgress _progress;
    private readonly HuntPositions _positions;
    private readonly FarmModeService _farm;
    private readonly ITravel _travel;
    private readonly ITravelWorld _world;
    private readonly Func<float, float, Vector3?> _floorAt;
    private readonly Func<uint> _currentClassJob;
    private readonly Func<uint> _grandCompany;
    private readonly Func<string?> _missingPlugin;
    private readonly Action<string> _say;

    // Keyed by place in the rank: the catalog hands out fresh target objects on every read.
    private readonly Dictionary<(int Entry, int Slot), string> _skipped = new();
    private Phase _phase;
    private DateTime _phaseStart;
    private HuntLogBook? _book;
    private int _rank;
    private HuntLeg? _leg;
    private List<Vector3> _spots = new();
    private int _lastKills;
    private DateTime _lastKillAt;
    private int _filled;

    public bool IsRunning => _phase != Phase.Idle;
    public string StatusLine { get; private set; } = "Idle";
    public HuntLogBook? Book => _book;

    /// <summary>Why a target of the rank being worked was passed over, or null when it was not.</summary>
    public string? SkipReason(HuntLogTarget target)
        => IsRunning && target.LogIndex == _book?.Index && target.Rank == _rank
           && _skipped.TryGetValue((target.Entry, target.Slot), out var reason) ? reason : null;

    public HuntLogRunner(
        HuntLogCatalog catalog,
        HuntLogProgress progress,
        HuntPositions positions,
        FarmModeService farm,
        ITravel travel,
        ITravelWorld world,
        Func<float, float, Vector3?> floorAt,
        Func<uint> currentClassJob,
        Func<uint> grandCompany,
        Func<string?> missingPlugin,
        Action<string> say)
    {
        _catalog = catalog;
        _progress = progress;
        _positions = positions;
        _farm = farm;
        _travel = travel;
        _world = world;
        _floorAt = floorAt;
        _currentClassJob = currentClassJob;
        _grandCompany = grandCompany;
        _missingPlugin = missingPlugin;
        _say = say;
    }

    /// <summary>Starts on a log's current rank. Returns why not, or null when it started.</summary>
    public string? Start(HuntLogBook book)
    {
        if (IsRunning)
            return "already running";
        if (_farm.IsRunning)
            return "a farm run is going — stop it first";
        if (_missingPlugin() is { } missing)
            return $"{missing} is not loaded — the hunt log run travels with Lifestream and Ariadne";

        var rank = _progress.CurrentRank(book.Index);
        if (rank < 0)
            return "the hunt log could not be read";
        if (rank >= HuntLogIds.RanksPerLog)
            return "that log is complete";

        // Kills only count for the log of the class being played, and for the company served.
        if (book.GrandCompanyId != 0)
        {
            if (_grandCompany() != book.GrandCompanyId)
                return $"this character does not serve {book.Name}";
        }
        else if (_catalog.BookForClassJob(_currentClassJob())?.Index != book.Index)
        {
            return $"switch to {book.Name} first — kills only count on that class";
        }

        _book = book;
        _rank = -1;
        _filled = 0;
        _skipped.Clear();
        _say($"Hunt log run started: {book.Name}, rank {rank + 1}.");
        Enter(Phase.Settling, _world.UtcNow);
        Next();
        return IsRunning ? null : "nothing on this rank can be done from here (see chat)";
    }

    public void Stop(string reason)
    {
        if (!IsRunning)
            return;

        _phase = Phase.Idle;
        StatusLine = "Idle";
        _travel.Cancel();
        if (_farm.IsRunning)
            _farm.Stop("hunt log run stopped");
        _say($"Hunt log run stopped — {reason}. {_filled} target(s) filled"
             + (_skipped.Count > 0 ? $", {_skipped.Count} skipped." : "."));
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
                        Skip(_travel.FailReason);
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
                        BeginFarm(now);
                        break;
                    case TravelStatus.Failed:
                        Skip(_travel.FailReason);
                        break;
                }
                break;

            case Phase.Farming:
                TickFarm(now);
                break;

            case Phase.Settling:
                if (_progress.CurrentRank(_book!.Index) != _rank || now - _phaseStart > RankSettle)
                    Next();
                break;
        }
    }

    /// <summary>Chooses the next target and sets off, or ends the run when nothing is left.</summary>
    private void Next()
    {
        var book = _book!;
        var now = _world.UtcNow;
        var rank = _progress.CurrentRank(book.Index);
        if (rank < 0)
        {
            Stop("the hunt log could not be read");
            return;
        }
        if (rank >= HuntLogIds.RanksPerLog)
        {
            Stop("the log is complete");
            return;
        }

        var settled = _phase == Phase.Settling;
        if (rank != _rank)
        {
            if (_rank >= 0)
                _say($"{book.Name} hunt log: rank {rank + 1} begins.");
            _rank = rank;
            _skipped.Clear();
            settled = false;
        }

        var candidates = new List<HuntCandidate>();
        foreach (var target in _catalog.Targets(book, rank))
        {
            if (_skipped.ContainsKey((target.Entry, target.Slot)) || _progress.KillsLeft(target) == 0)
                continue;

            var zones = _positions.For(target.NameId);
            if (zones.Count == 0)
            {
                Note(target, "no recorded position (a dungeon or FATE mob?)");
                continue;
            }
            candidates.Add(new HuntCandidate(target, zones));
        }

        if (candidates.Count == 0)
        {
            if (_skipped.Count > 0)
                Stop("nothing left on this rank that can be done");
            else if (settled)
                Stop("the rank is finished");
            else
                Enter(Phase.Settling, now); // every target filled: give the game a moment to turn the page
            return;
        }

        var here = _world.TerritoryId;
        var leg = HuntRoute.Next(candidates, here, territory => _world.RouteTo(territory, Vector3.Zero) is not null);
        if (leg == null)
        {
            foreach (var candidate in candidates)
                Note(candidate.Target, "no attuned aetheryte in any zone it lives in");
            Stop("nothing left on this rank that can be reached");
            return;
        }

        _leg = leg;
        if (leg.Zone.TerritoryId == here)
        {
            Enter(Phase.FindingGround, now);
            return;
        }

        // Another zone: its aetheryte first. The spots' heights can only be asked of that zone's mesh.
        var first = leg.Zone.Spots[0];
        var near = new Vector3(first.X, 0f, first.Y);
        var arrival = _world.NearestAttunedAetheryte(leg.Zone.TerritoryId, near) is { } aetheryte
            ? _world.AetherytePosition(aetheryte) ?? near
            : near;
        _travel.Begin(leg.Zone.TerritoryId, arrival, fly: false, land: false, AetheryteStopDistance);
        StatusLine = $"Travelling to {Zones.Describe(leg.Zone.TerritoryId)} for {leg.Target.MobName}";
        Enter(Phase.EnteringZone, now);
    }

    /// <summary>In the zone: ask the mesh for the floor under each recorded spot, then go to the first.</summary>
    private void FindGround(DateTime now)
    {
        var leg = _leg!;
        _spots = new List<Vector3>();
        foreach (var spot in leg.Zone.Spots)
        {
            if (_floorAt(spot.X, spot.Y) is { } floor)
                _spots.Add(floor);
            if (_spots.Count == MaxSpots)
                break;
        }

        if (_spots.Count == 0)
        {
            StatusLine = $"Finding the ground for {leg.Target.MobName}";
            if (now - _phaseStart > GroundWait)
                Skip("the navmesh has no floor at its recorded positions");
            return;
        }

        _travel.Begin(leg.Zone.TerritoryId, _spots[0], fly: true, land: true, SpotStopDistance);
        StatusLine = $"Going to {leg.Target.MobName}";
        Enter(Phase.GoingToSpot, now);
    }

    private void BeginFarm(DateTime now)
    {
        var leg = _leg!;
        var profile = _farm.Profile;
        profile.ClearMobs();
        profile.AddMob(leg.Target.NameId, leg.Target.MobName);
        profile.Hunt = leg.Target;
        profile.Spots.Clear();
        profile.Spots.AddRange(_spots);
        profile.TerritoryId = (ushort)leg.Zone.TerritoryId;

        if (_farm.Start() is { } error)
        {
            Skip(error);
            return;
        }

        _lastKills = _progress.Kills(leg.Target);
        _lastKillAt = now;
        Enter(Phase.Farming, now);
    }

    private void TickFarm(DateTime now)
    {
        var target = _leg!.Target;
        if (!_farm.IsRunning)
        {
            // Farm mode stops itself when the target is filled. Stopped short, it was the player
            // (or leaving the zone), and that ends the run too.
            if (_progress.KillsLeft(target) == 0)
            {
                _filled++;
                Next();
            }
            else
            {
                Stop("the farm stopped before the target was filled");
            }
            return;
        }

        StatusLine = $"{target.MobName}: {_farm.StatusLine}";
        var kills = _progress.Kills(target);
        if (kills != _lastKills)
        {
            _lastKills = kills;
            _lastKillAt = now;
        }
        else if (now - _lastKillAt > StallAfter)
        {
            _farm.Stop("no kills on this target");
            Skip($"none killed in {StallAfter.TotalMinutes:F0} minutes");
        }
    }

    private void Skip(string reason)
    {
        Note(_leg!.Target, reason);
        Next();
    }

    private void Note(HuntLogTarget target, string reason)
    {
        if (_skipped.TryAdd((target.Entry, target.Slot), reason))
            _say($"Hunt log: skipping {target.MobName} — {reason}.");
    }

    private void Enter(Phase phase, DateTime now)
    {
        _phase = phase;
        _phaseStart = now;
    }

    public void Dispose() => Stop("plugin unloading");
}
