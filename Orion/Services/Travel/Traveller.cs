using System;
using System.Numerics;
using System.Threading.Tasks;

namespace Orion.Services.Travel;

/// <summary>Why a leg failed, which decides whether the point it was going to is to blame.</summary>
public enum TravelFailure
{
    None,

    /// <summary>The zone could not be reached at all (no attuned way in, a teleport or hop refused). Not the point's fault.</summary>
    Route,

    /// <summary>In the zone, the point itself could not be reached (stuck, no path, could not land). The point is to blame.</summary>
    Point,

    /// <summary>The player took over, the character died, or a plugin is missing. Nobody's point is to blame.</summary>
    Interrupted,
}

public enum TravelStatus
{
    /// <summary>Nothing begun, or cancelled.</summary>
    Idle,
    Running,

    /// <summary>Standing at the point — on the ground, when the leg asked to land.</summary>
    Done,

    /// <summary>Cannot get there; <see cref="ITravel.FailReason"/> says why.</summary>
    Failed,
}

/// <summary>Getting the character to a point, anywhere in the open world.</summary>
public interface ITravel
{
    TravelStatus Status { get; }
    string FailReason { get; }

    /// <summary>What kind of failure it was; None unless Failed.</summary>
    TravelFailure Failure { get; }

    /// <summary>
    /// Go to <paramref name="to"/> in <paramref name="territoryId"/>. <paramref name="fly"/> is
    /// "fly if the zone allows it"; <paramref name="land"/> ends the leg on the ground. A stop
    /// distance of <see cref="Traveller.PointStop"/> means the point itself. <paramref name="teleport"/>
    /// false never uses an aetheryte within the zone, however far the point (Selene's stand points).
    /// </summary>
    void Begin(uint territoryId, Vector3 to, bool fly, bool land, float stopDistance = Traveller.PointStop, bool teleport = true);

    TravelStatus Tick();
    void Cancel();
}

/// <summary>
/// Travel, copied from Demeter: a Lifestream teleport into the zone, then the point handed to Ariadne.
///
/// <para>
/// Ariadne does the moving — pathfinding, calling the mount and flying, stall recovery, the straight
/// walk where the mesh stops short — and says how it went (user, 2026-10-22: "use Lifestream to
/// teleport to the zone, then feed the location to Ariadne"). An earlier cut of this class carried
/// Odysseus's own wedge ladder, stall hops and off-mesh nudges; with Ariadne underneath they were a
/// second opinion fighting the first, and they went.
/// </para>
///
/// <para>
/// What stays ours is what Ariadne leaves to the caller: getting into the zone (cross-zone travel is
/// the consumer's job), the mount for a long ride on the ground (it only calls one to fly), coming
/// off the mount at the end of a flight (it keeps the mount when the caller asked to fly) — including
/// a descent onto a ledge that never touches down — and holding still while Daedalus fights.
/// </para>
/// </summary>
public sealed class Traveller : ITravel
{
    private enum Phase { None, Settle, Teleport, TeleportWait, Aethernet, AethernetWait, ShardWalk, Mount, Move, Land }

    /// <summary>Teleports and hops one leg may take before it is a loop, not a route.</summary>
    private const int MaxHops = 4;

    /// <summary>Lifestream starts a hop only from a shard it counts you as at: this close (Odysseus's measure).</summary>
    private const float ShardTouch = 2.5f;
    private static readonly TimeSpan ShardWalkMax = TimeSpan.FromSeconds(30);

    /// <summary>The point itself: Ariadne's own arrival, not a radius around it.</summary>
    public const float PointStop = 0.5f;

    /// <summary>How close "arrived" is for a stop that is not the point itself.</summary>
    public const float DefaultStopDistance = 3f;

    /// <summary>Ariadne declares arrival by its own measure and can stop a hair outside ours.</summary>
    public const float ArrivalSlack = 1.5f;

    /// <summary>
    /// Close enough for a keypress to land on a node. Measured in three dimensions, because the way
    /// this fails is vertical — the walk finishes on a lip above it.
    /// </summary>
    public const float InteractReach = 3.5f;

    /// <summary>Same zone, but this far out with an aetheryte by the point: the crystal beats the ride.</summary>
    public const float TeleportWorthDistance = 250f;

    /// <summary>And it has to win by this much — a teleport is a cast and a loading screen.</summary>
    private const float TeleportMargin = 200f;

    /// <summary>A ride on the ground this long is worth a mount (Ariadne only calls one to fly).</summary>
    public const float MountWorthDistance = 30f;

    /// <summary>A move that ends "closest reachable point" this near is arrival.</summary>
    private const float NearEnough = 5f;

    /// <summary>Attempts at one leg before it fails, the air tried last where it is open.</summary>
    private const int MaxMoves = 3;

    private static readonly TimeSpan SettleGrace = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SettleMax = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReadyWait = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AetheryteWarmup = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan TeleportRetryEvery = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan TeleportGiveUp = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan TeleportStartGrace = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan TravelStart = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan TravelMax = TimeSpan.FromSeconds(90);
    private const int MaxTeleportAsks = 2;

    /// <summary>
    /// How long to keep asking for a mount before walking instead. The seconds after a teleport are
    /// a lock, and a mount asked for inside it is dropped without a word.
    /// </summary>
    private static readonly TimeSpan MountWait = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan MountRetry = TimeSpan.FromSeconds(2);

    /// <summary>A zone just loaded may not have its mesh yet; Ariadne's answers in ~0.1 s, this is the backstop.</summary>
    private static readonly TimeSpan NavWait = TimeSpan.FromSeconds(30);

    /// <summary>How long Ariadne gets to say it has started, and to stop refusing a move.</summary>
    private static readonly TimeSpan MoveStartGrace = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MoveRefusedMax = TimeSpan.FromSeconds(10);

    /// <summary>A leg that stops closing for this long is stopped; every ten yalms gained buys it back.</summary>
    private static readonly TimeSpan MoveTotal = TimeSpan.FromSeconds(180);
    private static readonly TimeSpan DismountRetry = TimeSpan.FromSeconds(2);

    /// <summary>The mounted flag clears before the animation ends, and a press in that gap is eaten.</summary>
    private static readonly TimeSpan DismountSettle = TimeSpan.FromSeconds(0.8);

    /// <summary>A three-yalm descent touches down in a second or two; five is a descent that will not.</summary>
    private static readonly TimeSpan BlockedDescentAfter = TimeSpan.FromSeconds(5);

    private readonly ITravelWorld _world;

    private Phase _phase;
    private DateTime _phaseStart;
    private uint _territory;

    /// <summary>An aetheryte in the zone may be used when it is far nearer the point.</summary>
    private bool _teleportInZone = true;
    private Vector3 _to;
    private bool _fly;
    private bool _land;
    private float _stop;

    // Teleport
    private uint _teleportTarget;
    private bool _listWarmed;
    private DateTime _warmedAt;
    private DateTime _lastTeleportTry;
    private int _teleportAsks;
    private bool _sawTravelBusy;
    private DateTime _settledSince;

    /// <summary>Where the teleport in hand lands: the zone itself, or a city hub before a hop.</summary>
    private uint _teleportTerritory;

    /// <summary>The aethernet shard to hop to once the teleport lands, if any.</summary>
    private string? _hop;
    private bool _hopAskedByName;
    private bool _walkedToShard;
    private uint _hopFrom;
    private int _hops;

    // Move
    private bool _issued;
    private DateTime _issuedAt;
    private bool _issuedFly;
    private int _moves;
    private bool _airTried;
    private float _bestDistance;
    private DateTime _lastProgress;

    /// <summary>The reroute over a descent that never lands.</summary>
    private Vector3? _detourTo;

    // Mount / land
    private DateTime _lastMountTry;
    private DateTime _lastDismountTry;
    private DateTime _dismountedAt;
    private bool _descentRerouted;
    private Task<Vector3?>? _floorQuery;
    private bool _landThenMove;

    // Fight
    private bool _heldForFight;
    private bool _fightDismounted;
    private DateTime _lastFightDismount;

    public Traveller(ITravelWorld world) => _world = world;

    public TravelStatus Status { get; private set; } = TravelStatus.Idle;
    public string FailReason { get; private set; } = string.Empty;
    public TravelFailure Failure { get; private set; }

    /// <summary>What it is doing, for a status line.</summary>
    public string PhaseName => _heldForFight ? "Waiting out a fight" : _phase.ToString();

    public void Begin(uint territoryId, Vector3 to, bool fly, bool land, float stopDistance = PointStop, bool teleport = true)
    {
        _territory = territoryId;
        _teleportInZone = teleport;
        _to = to;
        _fly = fly;
        _land = land;
        _stop = stopDistance;
        _teleportAsks = 0;
        _listWarmed = false;
        _settledSince = default;
        _descentRerouted = false;
        _floorQuery = null;
        _detourTo = null;
        _heldForFight = false;
        _fightDismounted = false;
        _moves = 0;
        _airTried = false;
        _hops = 0;
        _hop = null;
        _walkedToShard = false;
        _hopFrom = 0;
        FailReason = string.Empty;
        Failure = TravelFailure.None;
        Status = TravelStatus.Running;
        Enter(Phase.Settle);
    }

    public void Cancel()
    {
        if (Status == TravelStatus.Running)
        {
            _world.StopMoving();
            _world.AbortTravel();
        }
        _phase = Phase.None;
        Status = TravelStatus.Idle;
    }

    public TravelStatus Tick()
    {
        if (Status != TravelStatus.Running)
            return Status;

        var now = _world.UtcNow;
        if (_world.IsDead)
            return Fail("the character is dead", TravelFailure.Interrupted);

        if (_phase is Phase.Mount or Phase.Move && HoldForFight(now))
            return Status;

        switch (_phase)
        {
            case Phase.Settle: TickSettle(now); break;
            case Phase.Teleport: TickTeleport(now); break;
            case Phase.TeleportWait: TickTeleportWait(now); break;
            case Phase.Aethernet: TickAethernet(now); break;
            case Phase.AethernetWait: TickAethernetWait(now); break;
            case Phase.ShardWalk: TickShardWalk(now); break;
            case Phase.Mount: TickMount(now); break;
            case Phase.Move: TickMove(now); break;
            case Phase.Land: TickLand(now); break;
        }
        return Status;
    }

    /// <summary>
    /// Where the character is, is only worth choosing a route from once the game has stopped moving
    /// it — a zone load still finishing, or a conversation still open, decides the wrong one.
    /// </summary>
    private void TickSettle(DateTime now)
    {
        if (_world.IsOccupied || _world.IsTravelBusy)
        {
            _settledSince = default;
            if (now - _phaseStart > SettleMax)
            {
                _world.Log($"The game has not let go of the character in {SettleMax.TotalSeconds:F0}s — choosing the route anyway.");
                Enter(Route());
            }
            return;
        }
        if (_settledSince == default)
            _settledSince = now;
        if (now - _settledSince >= SettleGrace)
            Enter(Route());
    }

    private Phase Route()
    {
        var here = _world.PlayerPosition;
        if (_world.TerritoryId != _territory)
        {
            if (++_hops > MaxHops)
            {
                Fail($"still not in {Zones.Describe(_territory)} after {MaxHops} teleports and hops", TravelFailure.Route);
                return Phase.None;
            }
            if (_world.RouteTo(_territory, _to) is not { } route)
            {
                Fail($"{Zones.Describe(_territory)} has no aetheryte this character has attuned, and no aethernet way in — attune one, or go there yourself",
                    TravelFailure.Route);
                return Phase.None;
            }
            _hop = route.AethernetName;
            if (route.AetheryteId is not { } aetheryte)
            {
                // Already in the same city: the hop is the whole way in.
                _world.Log($"Going to {Zones.Describe(_territory)}: the aethernet to {_hop}.");
                return Phase.Aethernet;
            }
            _teleportTarget = aetheryte;
            _teleportTerritory = route.AetheryteTerritory;
            _hopFrom = _hop is null ? 0 : aetheryte;
            _world.Log($"Going to {Zones.Describe(_territory)}: teleporting to aetheryte {aetheryte}" + (_hop is null ? "." : $", then the aethernet to {_hop}."));
            return Phase.Teleport;
        }

        // Same zone and a long way off, with an attuned aetheryte standing near the point: the
        // crystal beats even the flight — when it wins by a clear margin. (Ariadne can plan teleport
        // legs itself, but only with its session switch on, and that switch is every consumer's.)
        var away = Vector3.Distance(here, _to);
        if (_teleportInZone && away > TeleportWorthDistance
            && _world.NearestAttunedAetheryte(_territory, _to) is { } near
            && _world.AetherytePosition(near) is { } nearAt
            && Vector3.Distance(nearAt, _to) + TeleportMargin < away)
        {
            _teleportTarget = near;
            _teleportTerritory = _territory;
            _hop = null;
            _world.Log($"The point is {away:F0}y away and aetheryte {near} stands {Vector3.Distance(nearAt, _to):F0}y from it — teleporting there.");
            return Phase.Teleport;
        }

        return AfterTeleport();
    }

    private Phase AfterTeleport()
    {
        var distance = Vector3.Distance(_world.PlayerPosition, _to);
        if (distance <= _stop + ArrivalSlack)
            return Arrive();
        // Ariadne calls the mount for a flight; a long ride on the ground is ours to mount for.
        if (!WillFly && distance > MountWorthDistance && _world.CanMountHere && !_world.IsMounted)
        {
            _lastMountTry = default;
            return Phase.Mount;
        }
        return Phase.Move;
    }

    private bool WillFly => (_fly || _airTried) && _world.CanFlyHere;

    private void TickTeleport(DateTime now)
    {
        if (!_world.IsReady || _world.InCombat)
        {
            if (_world.IsOccupied)
                _phaseStart = now; // a cutscene is not the teleport failing
            if (now - _phaseStart > ReadyWait)
                Fail("never became ready to teleport", TravelFailure.Route);
            return;
        }
        // The game's aetheryte list fills a frame or two after it is asked to: a request made in
        // the same breath is accepted and casts nothing. Warm it, then ask.
        if (!_listWarmed)
        {
            _listWarmed = true;
            _warmedAt = now;
            _world.RefreshAetheryteList();
            return;
        }
        if (now - _warmedAt < AetheryteWarmup)
            return;
        // A refusal right after a gearset change or a dismount is the action lock settling, not a
        // missing attunement — the gather runner switches job and then teleports, every time.
        if (now - _lastTeleportTry < TeleportRetryEvery)
            return;
        if (!_world.Teleport(_teleportTarget))
        {
            _lastTeleportTry = now;
            if (now - _phaseStart > TeleportGiveUp)
                Fail($"the teleport to aetheryte {_teleportTarget} kept being refused for {(now - _phaseStart).TotalSeconds:F0}s — Lifestream loaded?", TravelFailure.Route);
            return;
        }
        _sawTravelBusy = false;
        Enter(Phase.TeleportWait);
    }

    private void TickTeleportWait(DateTime now)
    {
        if (_world.IsTravelBusy)
            _sawTravelBusy = true;
        var arrived = _world.TerritoryId == _teleportTerritory && !_world.IsTravelBusy && _world.IsReady;

        // Lifestream said yes and nothing happened: the cast fell into the action lock a gearset
        // change leaves behind. Ask again before believing it — the gather lists faulted every item
        // this way in Odysseus before this was there.
        if (!_sawTravelBusy && !_world.IsTravelBusy && _world.TerritoryId != _teleportTerritory
            && now - _phaseStart > TeleportStartGrace && _teleportAsks < MaxTeleportAsks)
        {
            _teleportAsks++;
            _world.Log($"The teleport to aetheryte {_teleportTarget} never started — asking again ({_teleportAsks}/{MaxTeleportAsks}).");
            _listWarmed = false;
            Enter(Phase.Teleport);
            return;
        }

        if (arrived && (_sawTravelBusy || now - _phaseStart > TravelStart))
        {
            // At the city's crystal on the way to a district: the hop, straight away. Routing again
            // from here asked Lifestream "at a shard?" a moment after the load, heard no, and
            // teleported to the same crystal three times (Limsa, 2026-11-14).
            if (_hop is not null && _teleportTerritory != _territory)
            {
                _world.Log($"At the city crystal — the aethernet to {_hop}.");
                Enter(Phase.Aethernet);
                return;
            }
            // Wherever it put us is where the next decision starts — settle, then route again.
            _settledSince = default;
            Enter(Phase.Settle);
            return;
        }
        if (!_sawTravelBusy && !arrived && now - _phaseStart > TravelStart)
        {
            Fail($"the teleport to aetheryte {_teleportTarget} never started", TravelFailure.Route);
            return;
        }
        if (now - _phaseStart > TravelMax)
            Fail($"the teleport to aetheryte {_teleportTarget} did not finish in {TravelMax.TotalSeconds:F0}s", TravelFailure.Route);
    }

    /// <summary>
    /// The aethernet hop into a city district with no crystal of its own, through Lifestream (the
    /// way Odysseus goes): asked once the character can act, asked again while refused, judged by
    /// the zone it lands in.
    /// </summary>
    private void TickAethernet(DateTime now)
    {
        if (!_world.IsReady || _world.InCombat || _world.IsTravelBusy)
        {
            if (_world.IsOccupied)
                _phaseStart = now;
            if (now - _phaseStart > ReadyWait)
                Fail("never became ready for the aethernet", TravelFailure.Route);
            return;
        }
        if (now - _lastTeleportTry < TeleportRetryEvery)
            return;
        if (!_world.AethernetTeleport(_hop!, byNameOnly: false))
        {
            _lastTeleportTry = now;
            if (WalkToShard())
                return;
            if (now - _phaseStart > TeleportGiveUp)
                Fail($"the aethernet hop to {_hop} kept being refused for {(now - _phaseStart).TotalSeconds:F0}s — Lifestream loaded, and the shard attuned?", TravelFailure.Route);
            return;
        }
        _sawTravelBusy = false;
        _hopAskedByName = false;
        Enter(Phase.AethernetWait);
    }

    private void TickAethernetWait(DateTime now)
    {
        if (_world.IsTravelBusy)
            _sawTravelBusy = true;
        var arrived = _world.TerritoryId == _territory && !_world.IsTravelBusy && _world.IsReady;
        if (arrived && (_sawTravelBusy || now - _phaseStart > TravelStart))
        {
            _hop = null;
            _settledSince = default;
            Enter(Phase.Settle);
            return;
        }
        // The by-id ask and the by-name ask take different roads inside Lifestream, and one has
        // been seen to refuse what the other reaches: a hop that never started is asked once more
        // by name before it counts as a failure.
        if (!_sawTravelBusy && !arrived && now - _phaseStart > TeleportStartGrace && WalkToShard())
            return;
        if (!_sawTravelBusy && !arrived && !_hopAskedByName && now - _phaseStart > TeleportStartGrace)
        {
            _hopAskedByName = true;
            _world.Log($"The aethernet hop to {_hop} never started — asking again by name.");
            _world.AethernetTeleport(_hop!, byNameOnly: true);
            _phaseStart = now;
            return;
        }
        if (!_sawTravelBusy && !arrived && now - _phaseStart > TravelStart)
        {
            Fail($"the aethernet hop to {_hop} never started", TravelFailure.Route);
            return;
        }
        if (now - _phaseStart > TravelMax)
            Fail($"the aethernet hop to {_hop} did not finish in {TravelMax.TotalSeconds:F0}s", TravelFailure.Route);
    }

    /// <summary>
    /// Once per leg: a hop refused, or asked for and never started, from more than a couple of
    /// yalms off the crystal is walked up to it and asked again. False when there is nothing to walk.
    /// </summary>
    private bool WalkToShard()
    {
        if (_walkedToShard || _hopFrom == 0 || _world.AetherytePosition(_hopFrom) is not { } crystal
            || Vector3.Distance(_world.PlayerPosition, crystal) <= ShardTouch + ArrivalSlack)
            return false;
        _walkedToShard = true;
        _world.Log($"Lifestream did not start the hop from {Vector3.Distance(_world.PlayerPosition, crystal):F1}y off the crystal — walking up to it.");
        _world.StopMoving();
        if (!_world.MoveTo(crystal, fly: false, ShardTouch))
            _world.Log("Ariadne refused the walk to the crystal — asking for the hop from here.");
        Enter(Phase.ShardWalk);
        return true;
    }

    private void TickShardWalk(DateTime now)
    {
        if (_world.IsMoving && now - _phaseStart < ShardWalkMax)
            return;
        _world.StopMoving();
        _lastTeleportTry = default;
        _hopAskedByName = false;
        Enter(Phase.Aethernet);
    }

    private void TickMount(DateTime now)
    {
        if (_world.IsMounted || now - _phaseStart > MountWait)
        {
            if (!_world.IsMounted)
                _world.Log("The mount would not come — this leg stays on foot.");
            Enter(Phase.Move); // mounted, or long enough — walking is always an option
            return;
        }
        if (_world.IsReady && !_world.InCombat && now - _lastMountTry >= MountRetry)
        {
            _lastMountTry = now;
            _world.Mount();
        }
    }

    /// <summary>
    /// A fight on the way — a mob that took a swing at the node circuit, a FATE spilling over.
    /// Daedalus finishes it (Orion's busy gate is what tells it to) and that moves the character,
    /// so a move left running would pull against it. Stop, get off the mount (nobody fights from the
    /// saddle), hold every clock, and hand Ariadne the point again when it is over. Daedalus switched
    /// off means nobody fights: then moving on is the better answer, and nothing holds.
    /// </summary>
    private bool HoldForFight(DateTime now)
    {
        if (_world.InCombat && !_world.DaedalusDisabledByUser)
        {
            if (!_heldForFight)
            {
                _heldForFight = true;
                _world.StopMoving();
                _world.Log("In combat on the way — waiting for it to end, then going on.");
            }
            if (_world.IsMounted && now - _lastFightDismount > DismountRetry)
            {
                _lastFightDismount = now;
                _fightDismounted = true;
                _world.StopMoving();
                _world.Dismount();
            }
            _phaseStart = now;
            _lastProgress = now;
            _issued = false;   // the move is asked for again afterwards
            return true;
        }
        if (!_heldForFight)
            return false;

        _heldForFight = false;
        var left = Vector3.Distance(_world.PlayerPosition, _to);
        var remount = _phase == Phase.Move && !WillFly && (_fightDismounted || left > MountWorthDistance)
                      && _world.CanMountHere && !_world.IsMounted;
        _fightDismounted = false;
        _world.Log(remount ? "Out of combat — mounting up and going on." : "Out of combat — going on.");
        if (!remount)
            return false;
        _lastMountTry = default;
        Enter(Phase.Mount);
        return true;
    }

    private void TickMove(DateTime now)
    {
        // A cutscene or conversation owns the character: no clock runs through it.
        if (_world.IsOccupied)
        {
            _phaseStart = now;
            _lastProgress = now;
            return;
        }

        var detour = _detourTo;
        var target = detour ?? _to;
        var tolerance = detour is null ? _stop : InteractReach - ArrivalSlack;
        var distance = Vector3.Distance(_world.PlayerPosition, target);

        if (!_issued)
        {
            if (!_world.NavReady)
            {
                if (now - _phaseStart > NavWait)
                    Fail("Ariadne has no mesh for this zone — is it loaded?", TravelFailure.Interrupted);
                return;
            }
            var fly = detour is not null ? _world.IsMounted : WillFly;
            if (!_world.MoveTo(target, fly, tolerance > PointStop ? tolerance : 0f))
            {
                if (now - _phaseStart > MoveRefusedMax)
                    Fail($"Ariadne kept refusing the move to {Fmt(target)} — {_world.MoveResult}", TravelFailure.Interrupted);
                return;
            }
            _issued = true;
            _issuedAt = now;
            _issuedFly = fly;
            _moves++;
            _bestDistance = distance;
            _lastProgress = now;
            return;
        }

        if (_world.IsMoving || now - _issuedAt < MoveStartGrace)
        {
            // The budget measures progress, not wall time: a long crossing still closing is not stuck.
            if (distance < _bestDistance - 10f)
            {
                _bestDistance = distance;
                _lastProgress = now;
            }
            if (now - _lastProgress > MoveTotal)
            {
                _world.StopMoving();
                Fail($"no progress toward {Fmt(target)} for {MoveTotal.TotalSeconds:F0}s ({distance:F1}y left) — Ariadne: {_world.MoveResult}", TravelFailure.Point);
            }
            return;
        }

        // Ariadne has finished: judge where it left us, then what it said.
        var result = _world.MoveResult;
        _issued = false;

        // A flight that ends hanging over the point: horizontally there, above it — come down.
        var overhead = _world.IsInFlight && _world.PlayerPosition.Y > target.Y
                       && Vector3.Distance(target with { Y = _world.PlayerPosition.Y }, _world.PlayerPosition) <= tolerance + ArrivalSlack;
        if (distance <= tolerance + ArrivalSlack || overhead
            || result.StartsWith("goal reached", StringComparison.OrdinalIgnoreCase)
            || (result.StartsWith("closest reachable point", StringComparison.OrdinalIgnoreCase) && distance <= NearEnough))
        {
            if (detour is not null)
            {
                _detourTo = null;
                BeginLand();
                return;
            }
            Enter(Arrive());
            return;
        }

        if (result.StartsWith("zone changed", StringComparison.OrdinalIgnoreCase))
        {
            Enter(Phase.Settle);
            return;
        }

        // The player took the keys: that is theirs to decide, not ours to fight.
        if (result.StartsWith("stopped", StringComparison.OrdinalIgnoreCase))
        {
            Fail($"the move to {Fmt(target)} was stopped (keys pressed?)", TravelFailure.Interrupted);
            return;
        }

        // Stuck, no path, or short of a point the mesh will not reach. Ariadne has already re-pathed
        // on its own; one more ask from where it gave up, then the air where the zone allows it.
        if (_moves < MaxMoves - 1)
        {
            _world.Log($"Ariadne: {result} — {distance:F1}y from {Fmt(target)}; asking again.");
            return;
        }
        if (!_issuedFly && !_airTried && _world.CanFlyHere)
        {
            _airTried = true;
            _world.Log($"Ariadne: {result} on the ground — flying this leg.");
            return;
        }
        Fail($"Ariadne could not reach {Fmt(target)}: {result} ({distance:F1}y left)", TravelFailure.Point);
    }

    private Phase Arrive()
    {
        if (_land && _world.IsMounted)
        {
            BeginLand();
            return _phase;
        }
        Status = TravelStatus.Done;
        return Phase.None;
    }

    private void BeginLand(bool thenMove = false)
    {
        _landThenMove = thenMove;
        _lastDismountTry = default;
        _dismountedAt = default;
        Enter(Phase.Land);
    }

    /// <summary>
    /// Both feet on the ground. A dismount in the air is a descent and the character stays mounted
    /// the whole way down, so this keeps asking rather than assuming it took — and a descent with
    /// no floor beneath it (hovering against a ledge) is aborted and rerouted to come down on a
    /// floor the mesh knows, beside the point.
    /// </summary>
    private void TickLand(DateTime now)
    {
        if (_world.IsMounted && _world.IsInFlight && !_descentRerouted && _world.CanFlyHere
            && now - _phaseStart > BlockedDescentAfter && Vector3.Distance(_to, _world.PlayerPosition) > 0.5f)
        {
            _floorQuery ??= _world.NearestReachablePoint(_to, 8f);
            if (!_floorQuery.IsCompleted)
                return;
            var floor = _floorQuery.Status == TaskStatus.RanToCompletion && _floorQuery.Result is { } f ? f : _to;
            _descentRerouted = true;
            _world.Log($"The descent is not landing (at {Fmt(_world.PlayerPosition)}) — flying over {Fmt(floor)} to come down on its floor.");
            _detourTo = floor;
            Enter(Phase.Move);
            return;
        }

        if (!_world.IsMounted)
        {
            if (_dismountedAt == default)
            {
                _dismountedAt = now;
                return;
            }
            if (now - _dismountedAt < DismountSettle)
                return;
            if (_landThenMove)
            {
                _landThenMove = false;
                Enter(Phase.Move);
                return;
            }
            Status = TravelStatus.Done;
            _phase = Phase.None;
            return;
        }
        if (now - _lastDismountTry > DismountRetry)
        {
            _lastDismountTry = now;
            _world.Dismount();
        }
        if (now - _phaseStart > ReadyWait)
            Fail($"could not get off the mount (at {Fmt(_world.PlayerPosition)}, {(_world.IsInFlight ? "in flight" : "on the ground")})", TravelFailure.Point);
    }

    private void Enter(Phase phase)
    {
        if (phase == Phase.None)
            return; // a helper already finished or failed the leg
        _phase = phase;
        _phaseStart = _world.UtcNow;
        if (phase == Phase.Move)
            _issued = false;
    }

    private TravelStatus Fail(string reason, TravelFailure failure)
    {
        _world.StopMoving();
        _phase = Phase.None;
        FailReason = reason;
        Failure = failure;
        Status = TravelStatus.Failed;
        return Status;
    }

    private static string Fmt(Vector3 v) => $"({v.X:F0},{v.Y:F0},{v.Z:F0})";
}
