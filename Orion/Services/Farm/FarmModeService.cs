using System;
using System.Diagnostics;
using System.Numerics;
using Dalamud.Plugin.Services;
using Dalamud.Game.ClientState.Objects;
using Orion.Config;
using Orion.Services.Game;
using Orion.Services.HuntLog;
using Orion.Services.Ipc;
using Orion.Services.Nav;

namespace Orion.Services.Farm;

/// <summary>
/// Farm mode: kill the profile's mobs around user-placed spots until X of an item is in the bag,
/// or until a hunt log target is filled. Copied out of Daedalus (v0.1.127), where it held the
/// external-combat override itself; here <see cref="IsRunning"/> feeds <c>Orion.IsBusy</c> and
/// Daedalus holds the override while that is true. Orion acquires targets (aggroed-on-us first,
/// then profile mobs leashed to the active spot), Daedalus's automation engage opens on them, and
/// Orion roams between spots via the navmesh when nothing is up. Single zone only — leaving the
/// zone stops the run (no teleporting by design). Design notes: Daedalus's docs/farm-mode.md.
/// </summary>
public sealed class FarmModeService : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    private const float AggroCleanupRangeYalms = 40f;
    private const float StallWarnMinutes = 5;

    private readonly OrionConfig _configuration;
    private readonly IObjectTable _objectTable;
    private readonly ITargetManager _targetManager;
    private readonly EnemyScanner _enemies;
    private readonly IVNavService _vNav;
    private readonly Func<uint, uint> _itemCount;
    private readonly HuntLogProgress _huntLog;
    private readonly DaedalusIpc _daedalus;
    private readonly IClientState _clientState;
    private readonly IFarmMountHelper _mount;
    private readonly IPluginLog _log;

    private readonly Stopwatch _pollClock = Stopwatch.StartNew();
    private int _spotIndex;
    private bool _atSpot;
    private DateTime _arrivedAtSpotUtc;
    private DateTime _lastProgressUtc;
    private ulong _lastEngagedTargetId;

    /// <summary>The engaged target turned on us or took damage: a fight, not just a target.</summary>
    private bool _foughtEngaged;
    private ulong _approachTargetId;
    private DateTime? _atTagRangeSinceUtc;

    // ---- v4 mounted travel state ----
    private const double MountCastTimeoutSeconds = 3.0;
    private const double MountGiveUpSeconds = 30.0;
    private const double DismountRetrySeconds = 2.0;
    private DateTime _lastDismountPressUtc = DateTime.MinValue;
    private DateTime? _mountCastRequestedUtc;
    private bool _mountCastRetried;
    private DateTime _suppressMountUntilUtc = DateTime.MinValue;
    private bool _flyFailedThisLeg;
    private bool _warnedSpecificMountFallback;

    /// <summary>User-facing progress/state notifications (Plugin routes these to chat).</summary>
    public event Action<string>? Notify;

    /// <summary>A run ended. True when it reached its goal, false when it was stopped short.</summary>
    public event Action<bool>? Ended;

    public FarmProfile Profile { get; } = new();
    public bool IsRunning { get; private set; }

    /// <summary>Live bag count for any item — the setup UI shows this before a run starts
    /// (<see cref="CurrentItemCount"/> only refreshes while running).</summary>
    public uint PeekItemCount(uint itemId) => itemId == 0 ? 0 : _itemCount(itemId);

    /// <summary>Kills the profile's hunt log target still wants (0 without one).</summary>
    public int HuntKillsLeft => Profile.Hunt is { } hunt ? _huntLog.KillsLeft(hunt) : 0;
    public string StatusLine { get; private set; } = "Idle";
    public int Kills { get; private set; }
    public uint CurrentItemCount { get; private set; }
    public int ActiveSpotIndex => _spotIndex;

    public FarmModeService(
        OrionConfig configuration,
        IObjectTable objectTable,
        ITargetManager targetManager,
        EnemyScanner enemies,
        IVNavService vNav,
        Func<uint, uint> itemCount,
        HuntLogProgress huntLog,
        DaedalusIpc daedalus,
        IClientState clientState,
        IFarmMountHelper mountHelper,
        IPluginLog log)
    {
        _configuration = configuration;
        _objectTable = objectTable;
        _targetManager = targetManager;
        _enemies = enemies;
        _vNav = vNav;
        _itemCount = itemCount;
        _huntLog = huntLog;
        _daedalus = daedalus;
        _clientState = clientState;
        _mount = mountHelper;
        _log = log;
    }

    public string? Start()
    {
        if (IsRunning)
            return "already running";
        if (!Profile.IsValid)
            return "profile incomplete — needs an item and a target count (or a hunt log target), at least one mob, and at least one spot";
        if (_clientState.TerritoryType != Profile.TerritoryId)
            return "wrong zone — the spots were placed in a different zone";
        if (!_vNav.IsAvailable)
            return "neither Ariadne nor vnavmesh is loaded — farm mode needs one to move between spots";
        if (Profile.Hunt is { } filled && _huntLog.KillsLeft(filled) == 0)
            return "that hunt log target is already filled";

        IsRunning = true;
        _spotIndex = 0;
        _atSpot = false;
        Kills = 0;
        _lastEngagedTargetId = 0;
        _foughtEngaged = false;
        _lastProgressUtc = DateTime.UtcNow;
        _mountCastRequestedUtc = null;
        _mountCastRetried = false;
        _suppressMountUntilUtc = DateTime.MinValue;
        _flyFailedThisLeg = false;
        _warnedSpecificMountFallback = false;
        CurrentItemCount = PeekItemCount(Profile.ItemId);
        Notify?.Invoke($"Farm started: {GoalText()}, {Profile.Mobs.Count} mob kind(s), {Profile.Spots.Count} spot(s).");
        _log.Info("[Farm] started: {0}, {1} mobs, {2} spots, territory {3}",
            GoalText(), Profile.Mobs.Count, Profile.Spots.Count, Profile.TerritoryId);
        return null;
    }

    public void Stop(string reason) => Stop(reason, completed: false);

    private void Stop(string reason, bool completed)
    {
        if (!IsRunning)
            return;

        IsRunning = false;
        StatusLine = "Idle";
        try
        {
            if (_vNav.IsPathRunning || _vNav.IsPathfindInProgress)
                _vNav.Stop();
        }
        catch
        {
            // vnav unload race — nothing to clean up.
        }
        Notify?.Invoke($"Farm stopped — {reason}. {GoalText()}, {Kills} kills this run.");
        _log.Info("[Farm] stopped: {0}", reason);
        Ended?.Invoke(completed);
    }

    /// <summary>The goal and how far along it is, for the chat lines.</summary>
    private string GoalText() => Profile.Hunt is { } hunt
        ? $"hunt log {hunt.MobName}, {_huntLog.KillsLeft(hunt)} of {hunt.Needed} left"
        : $"{Profile.ItemName} ×{Profile.TargetCount} (have {CurrentItemCount})";

    /// <summary>Framework-thread poll; internally throttled.</summary>
    public void Update()
    {
        if (!IsRunning)
            return;
        if (_pollClock.Elapsed < PollInterval)
            return;
        _pollClock.Restart();

        var player = _objectTable.LocalPlayer;
        if (player == null)
            return;

        if (player.CurrentHp == 0)
        {
            Stop("player died");
            return;
        }

        if (_clientState.TerritoryType != Profile.TerritoryId)
        {
            Stop("left the farm zone");
            return;
        }

        var newCount = PeekItemCount(Profile.ItemId);
        if (newCount != CurrentItemCount)
        {
            _log.Debug("[Farm] bag count {0} -> {1} (target {2})", CurrentItemCount, newCount, Profile.TargetCount);
            _lastProgressUtc = DateTime.UtcNow;
        }
        CurrentItemCount = newCount;
        if (Profile.Hunt is { } hunt)
        {
            if (_huntLog.KillsLeft(hunt) == 0)
            {
                Stop("hunt log target filled", completed: true);
                return;
            }
        }
        else if (FarmRoamPolicy.IsComplete(CurrentItemCount, Profile.TargetCount))
        {
            Stop("target count reached", completed: true);
            return;
        }

        // Raw hard-target check, deliberately without the attackability probe: it fails while the
        // target is still out of range, and the farm then never approached (Daedalus field bug).
        // The farm set this target itself; a live, targetable BattleNpc is enough to walk to.
        if (_targetManager.Target is Dalamud.Game.ClientState.Objects.Types.IBattleNpc { IsDead: false, IsTargetable: true } target)
        {
            if (target.GameObjectId != _lastEngagedTargetId)
                _foughtEngaged = false;
            _lastEngagedTargetId = target.GameObjectId;
            if (target.TargetObjectId == player.GameObjectId || target.CurrentHp < target.MaxHp)
                _foughtEngaged = true;
            _lastProgressUtc = DateTime.UtcNow;
            ApproachTarget(player, target);
            return;
        }

        // The target we took is no longer a live target. That is a kill only when the mob is dead:
        // a target that never took, was dropped by something else, or wandered off is not one.
        // (Counting every lost target ran the counter up with no mob in reach, 2026-11-19.)
        if (_lastEngagedTargetId != 0)
        {
            var mob = _objectTable.SearchById(_lastEngagedTargetId) as Dalamud.Game.ClientState.Objects.Types.IBattleNpc;
            if (FarmRoamPolicy.CountsAsKill(mob != null, mob is { IsDead: true } or { CurrentHp: 0 }, _foughtEngaged))
            {
                Kills++;
                _lastProgressUtc = DateTime.UtcNow;
            }
            _lastEngagedTargetId = 0;
            _foughtEngaged = false;
        }

        var spot = Profile.Spots[Math.Min(_spotIndex, Profile.Spots.Count - 1)];

        // Acquire: finish anything on us first, then the nearest profile mob. The player-centered
        // scan runs out to the v4 acquisition radius (spot leash still applies — the widened
        // funnel must not pull the toon outside the patrol area).
        var scanRadius = Math.Clamp(_configuration.Farm.ScanRadiusYalms, 10f, 100f);
        var candidate = _enemies.FindNearestAggroedEnemy(AggroCleanupRangeYalms, player)
            ?? _enemies.FindNearestEnemyByNameIds(
                Profile.MobNameIds, Math.Max(scanRadius, Profile.LeashRadiusYalms), player, spot, Profile.LeashRadiusYalms);
        if (candidate != null)
        {
            _daedalus.RecordTargetWrite(candidate.GameObjectId);
            _targetManager.Target = candidate;
            // Remember the mob at ACQUISITION: high-level toons one-shot farm mobs inside a single
            // poll, so the "fighting" branch may never see the target alive — without this the
            // kill counter reads 0 on every fast-kill run. Whether it counts is decided when the
            // target is gone, by whether the mob is dead.
            _lastEngagedTargetId = candidate.GameObjectId;
            _foughtEngaged = false;
            _flyFailedThisLeg = false; // new travel leg
            if (_vNav.IsPathRunning)
                _vNav.Stop();
            StatusLine = $"Targeting {candidate.Name}";
            return;
        }

        Roam(player.Position, spot);

        if ((DateTime.UtcNow - _lastProgressUtc).TotalMinutes >= StallWarnMinutes)
        {
            Notify?.Invoke("Farm: no kills in a while — check the spot placement and mob list.");
            _lastProgressUtc = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Farm-only pull tactic (deliberately not shared with any other movement system):
    /// walk to ranged tag distance, stand still while the rotation tags the mob, let it run to
    /// us and die on the way in (melee finishes when it arrives). If the tag window passes with
    /// no engagement (kits without a ranged attack, e.g. MNK), walk to melee reach instead.
    /// Uses only SimpleMove.PathfindAndMoveTo — the one vnavmesh endpoint proven on this install.
    /// </summary>
    private void ApproachTarget(
        Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter player,
        Dalamud.Game.ClientState.Objects.Types.IBattleNpc target)
    {
        // New target: reset the tag-window tracking.
        if (target.GameObjectId != _approachTargetId)
        {
            _approachTargetId = target.GameObjectId;
            _atTagRangeSinceUtc = null;
        }

        var toPlayer = player.Position - target.Position;
        var distance = toPlayer.Length();
        var edgeDistance = distance - target.HitboxRadius - player.HitboxRadius;

        // Mounted travel owns the long legs: mount up when the mob is far, ride/fly in, land and
        // dismount at DismountRange, then the normal tag/approach logic takes over on foot.
        if (HandleMountedTravel(target.Position, edgeDistance, destinationIsMob: true, target.Name?.TextValue ?? "target"))
            return;

        if (_atTagRangeSinceUtc == null && edgeDistance <= FarmRoamPolicy.TagRangeYalms)
            _atTagRangeSinceUtc = DateTime.UtcNow;

        // "Tagged" = the mob has turned on us (it targets the player) — from here it closes the
        // distance itself while the rotation shoots it.
        var engagedOnUs = target.TargetObjectId == player.GameObjectId;
        var secondsAtTagRange = _atTagRangeSinceUtc.HasValue
            ? (DateTime.UtcNow - _atTagRangeSinceUtc.Value).TotalSeconds
            : 0;

        switch (FarmRoamPolicy.DecideApproach(engagedOnUs, edgeDistance, secondsAtTagRange))
        {
            case FarmApproachAction.MoveToTagRange:
                MoveTowardTarget(target.Position, toPlayer, distance, target.HitboxRadius, player.HitboxRadius, FarmRoamPolicy.TagRangeYalms * 0.85f);
                StatusLine = $"Approaching {target.Name} (to tag range)";
                break;

            case FarmApproachAction.MoveToMelee:
                MoveTowardTarget(target.Position, toPlayer, distance, target.HitboxRadius, player.HitboxRadius, FarmRoamPolicy.MeleeStopYalms * 0.6f);
                StatusLine = $"Walking to {target.Name} (no ranged tag)";
                break;

            default:
                if (_vNav.IsPathRunning)
                    _vNav.Stop();
                StatusLine = engagedOnUs ? $"Fighting {target.Name}" : $"Tagging {target.Name}";
                break;
        }
    }

    private void MoveTowardTarget(Vector3 targetPosition, Vector3 toPlayer, float distance, float targetHitbox, float playerHitbox, float stopEdgeYalms)
    {
        // Re-path only when nav is idle (vNav stutter-step guard).
        if (_vNav.IsPathRunning || _vNav.IsPathfindInProgress)
            return;

        var stopPoint = distance > 0.5f
            ? targetPosition + toPlayer / distance * (targetHitbox + playerHitbox + stopEdgeYalms)
            : targetPosition;
        var result = _vNav.PathfindAndMoveTo(_vNav.SnapToFloor(stopPoint));
        if (result != VNavMoveResult.Queued)
            _log.Debug("[Farm] approach path not queued: {0}", result);
    }

    /// <summary>
    /// v4 mounted travel (docs/farm-mode.md §v4): one policy decision per tick, executed against
    /// vNav + the mount helper. Returns true when travel consumed the tick (casting the mount,
    /// riding, landing, dismounting); false hands the tick back to the normal ground logic.
    /// </summary>
    private bool HandleMountedTravel(Vector3 destination, float distanceYalms, bool destinationIsMob, string label)
    {
        var cfg = _configuration.Farm;
        var mounted = _mount.IsMounted;
        var castPending = _mountCastRequestedUtc.HasValue && !mounted;
        var suppressed = DateTime.UtcNow < _suppressMountUntilUtc;

        if (mounted && _mountCastRequestedUtc.HasValue)
        {
            // Cast landed — clear the wait state.
            _mountCastRequestedUtc = null;
            _mountCastRetried = false;
        }

        var action = FarmMountPolicy.Decide(
            mounted,
            _mount.IsInFlight,
            _mount.IsInCombat,
            castPending,
            suppressed,
            distanceYalms,
            cfg.MountDistanceThresholdYalms,
            cfg.DismountRangeYalms,
            destinationIsMob);

        switch (action)
        {
            case FarmTravelAction.CastMount:
                // Mount cast is ~1s and breaks on movement: halt nav this tick, cast on the next.
                if (_vNav.IsPathRunning || _vNav.IsPathfindInProgress)
                {
                    _vNav.Stop();
                    StatusLine = "Stopping to mount";
                    return true;
                }
                CastMount();
                StatusLine = "Calling mount";
                return true;

            case FarmTravelAction.AwaitMount:
                var waited = (DateTime.UtcNow - _mountCastRequestedUtc!.Value).TotalSeconds;
                if (waited >= MountCastTimeoutSeconds)
                {
                    if (!_mountCastRetried)
                    {
                        _mountCastRetried = true;
                        CastMount();
                    }
                    else
                    {
                        // Never loop-cast: give up on mounting for a while and walk this leg.
                        _mountCastRequestedUtc = null;
                        _mountCastRetried = false;
                        _suppressMountUntilUtc = DateTime.UtcNow.AddSeconds(MountGiveUpSeconds);
                        _log.Info("[Farm] mount cast never landed — walking for the next {0}s", MountGiveUpSeconds);
                        return false;
                    }
                }
                StatusLine = "Mounting...";
                return true;

            case FarmTravelAction.Ride:
                RideToward(destination, label);
                return true;

            case FarmTravelAction.LandThenDismount:
                // Airborne at the mob. Dismount pressed in the air is a descent: the mount flies
                // down, lands, and the next press gets off (what Demeter's travel does). A ground
                // path asked of the navmesh does NOT land a flying mount: the character hovered
                // over the mob, still mounted, and nothing attacked (2026-11-19).
                if (_vNav.IsPathRunning || _vNav.IsPathfindInProgress)
                    _vNav.Stop();
                PressDismount();
                StatusLine = $"Landing near {label}";
                return true;

            case FarmTravelAction.Dismount:
                if (_vNav.IsPathRunning || _vNav.IsPathfindInProgress)
                    _vNav.Stop();
                PressDismount();
                StatusLine = "Dismounting";
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Dismount, asked again every couple of seconds until it takes rather than every poll: in the
    /// air the first press only starts the descent, and a press landing in the gap while the
    /// mounted flag clears is eaten.
    /// </summary>
    private void PressDismount()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastDismountPressUtc).TotalSeconds < DismountRetrySeconds)
            return;
        _lastDismountPressUtc = now;
        _mount.TryDismount();
    }

    private void CastMount()
    {
        var cfg = _configuration.Farm;
        var useSpecific = FarmMountPolicy.UseSpecificMount(
            cfg.MountMode, cfg.SpecificMountId, _mount.IsMountUnlocked(cfg.SpecificMountId));

        if (cfg.MountMode == FarmMountMode.Specific && cfg.SpecificMountId != 0
            && !useSpecific && !_warnedSpecificMountFallback)
        {
            _warnedSpecificMountFallback = true;
            Notify?.Invoke("Farm: selected mount is not unlocked on this character — using Mount Roulette.");
        }

        if (_mount.TryMount(useSpecific, cfg.SpecificMountId, out var detail))
        {
            _mountCastRequestedUtc = DateTime.UtcNow;
            _log.Debug("[Farm] mount cast issued ({0})", detail);
        }
        else
        {
            // Cast refused (status gate / combat re-check) — don't spin on it this leg.
            _suppressMountUntilUtc = DateTime.UtcNow.AddSeconds(MountGiveUpSeconds);
            _log.Debug("[Farm] mount cast refused: {0}", detail);
        }
    }

    private void RideToward(Vector3 destination, string label)
    {
        if (_vNav.IsPathRunning || _vNav.IsPathfindInProgress)
        {
            StatusLine = $"Riding to {label}";
            return;
        }

        var fly = cfgFlyAllowed();
        var result = _vNav.PathfindAndMoveTo(fly ? destination : _vNav.SnapToFloor(destination), fly);
        if (result != VNavMoveResult.Queued && fly)
        {
            // Fly pathing failed (mesh/attunement edge) — fall back to ground for this leg.
            _flyFailedThisLeg = true;
            result = _vNav.PathfindAndMoveTo(_vNav.SnapToFloor(destination), fly: false);
        }

        if (result != VNavMoveResult.Queued)
            _log.Debug("[Farm] mounted path not queued: {0}", result);

        StatusLine = fly && !_flyFailedThisLeg ? $"Flying to {label}" : $"Riding to {label}";

        bool cfgFlyAllowed() =>
            _configuration.Farm.FlyWhenPossible && !_flyFailedThisLeg && _mount.CanFlyInCurrentZone();
    }

    private void Roam(Vector3 playerPosition, Vector3 spot)
    {
        var distance = Vector3.Distance(playerPosition, spot);
        var wasAtSpot = _atSpot;
        _atSpot = distance <= FarmRoamPolicy.ArriveToleranceYalms;
        if (_atSpot && !wasAtSpot)
            _arrivedAtSpotUtc = DateTime.UtcNow;

        var idleSeconds = _atSpot ? (DateTime.UtcNow - _arrivedAtSpotUtc).TotalSeconds : 0;

        // Long spot legs travel mounted (fly where legal); short hops walk as before.
        if (HandleMountedTravel(spot, distance, destinationIsMob: false, $"spot {_spotIndex + 1}/{Profile.Spots.Count}"))
            return;

        var navBusy = _vNav.IsPathRunning || _vNav.IsPathfindInProgress;

        switch (FarmRoamPolicy.Decide(navBusy, distance, idleSeconds, _configuration.Farm.RespawnWaitSeconds, Profile.Spots.Count))
        {
            case FarmRoamAction.MoveToSpot:
                _vNav.PathfindAndMoveTo(_vNav.SnapToFloor(spot));
                StatusLine = $"Moving to spot {_spotIndex + 1}/{Profile.Spots.Count}";
                break;

            case FarmRoamAction.AdvanceSpot:
                _spotIndex = FarmRoamPolicy.NextSpotIndex(_spotIndex, Profile.Spots.Count);
                _atSpot = false;
                _flyFailedThisLeg = false; // new travel leg
                StatusLine = $"Roaming to spot {_spotIndex + 1}/{Profile.Spots.Count}";
                break;

            default:
                StatusLine = _atSpot
                    ? $"Waiting for respawns at spot {_spotIndex + 1}/{Profile.Spots.Count}"
                    : StatusLine;
                break;
        }
    }

    public void Dispose()
    {
        Stop("plugin unloading");
    }
}
