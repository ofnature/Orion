using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;
using Orion.Services.Ipc;
using Orion.Services.Travel;

namespace Orion.Services.Game;

/// <summary>
/// What <see cref="Traveller"/> needs of the game, Ariadne and Lifestream, and what
/// <see cref="ChocoboKeeper"/> needs to keep the companion out. The travel half of Demeter's
/// <c>GameBasics</c> (same author, both AGPL), copied without the gathering, gearset and repair
/// members; the companion members are Odysseus's. Decision-free: every judgement lives in
/// <see cref="Traveller"/> and <see cref="ChocoboKeeper"/>.
/// </summary>
public sealed unsafe class TravelWorld : ITravelWorld, IChocoboWorld
{
    /// <summary>GeneralAction 9 — Mount Roulette.</summary>
    private const uint MountRouletteGeneralAction = 9;

    /// <summary>GeneralAction 23 — Dismount.</summary>
    private const uint DismountGeneralAction = 23;

    private readonly IClientState _clientState;
    private readonly IObjectTable _objectTable;
    private readonly ICondition _condition;
    private readonly IDataManager _data;
    private readonly AriadneIpc _ariadne;
    private readonly LifestreamIpc _lifestream;
    private readonly DaedalusIpc _daedalus;
    private readonly AetheryteCatalog _aetherytes;
    private readonly Action<string> _log;

    public TravelWorld(
        IClientState clientState, IObjectTable objectTable, ICondition condition, IDataManager data,
        AriadneIpc ariadne, LifestreamIpc lifestream, DaedalusIpc daedalus, AetheryteCatalog aetherytes,
        Action<string> log)
    {
        _clientState = clientState;
        _objectTable = objectTable;
        _condition = condition;
        _data = data;
        _ariadne = ariadne;
        _lifestream = lifestream;
        _daedalus = daedalus;
        _aetherytes = aetherytes;
        _log = log;
    }

    public DateTime UtcNow => DateTime.UtcNow;
    public uint TerritoryId => _clientState.TerritoryType;
    public Vector3 PlayerPosition => _objectTable.LocalPlayer?.Position ?? Vector3.Zero;
    public void Log(string message) => _log(message);

    // ── Character state ──

    public bool IsReady
        => _objectTable.LocalPlayer is not null
           && !_condition[ConditionFlag.BetweenAreas]
           && !_condition[ConditionFlag.BetweenAreas51]
           && !_condition[ConditionFlag.Occupied]
           && !_condition[ConditionFlag.OccupiedInCutSceneEvent]
           && !_condition[ConditionFlag.OccupiedInQuestEvent]
           && !_condition[ConditionFlag.Casting]
           && !_condition[ConditionFlag.Unconscious];

    public bool IsOccupied
        => _condition[ConditionFlag.Occupied]
           || _condition[ConditionFlag.OccupiedInCutSceneEvent]
           || _condition[ConditionFlag.OccupiedInQuestEvent]
           || _condition[ConditionFlag.OccupiedInEvent]
           || _condition[ConditionFlag.WatchingCutscene]
           || _condition[ConditionFlag.WatchingCutscene78];

    public bool IsTravelBusy
        => _lifestream.IsBusy
           || _condition[ConditionFlag.BetweenAreas]
           || _condition[ConditionFlag.BetweenAreas51]
           || _condition[ConditionFlag.Casting];

    public bool InCombat => _condition[ConditionFlag.InCombat];
    public bool IsDead => _objectTable.LocalPlayer?.IsDead ?? false;
    public bool DaedalusDisabledByUser => _daedalus.IsDisabledByUser();

    // ── Mount ──

    public bool IsMounted => _condition[ConditionFlag.Mounted];
    public bool IsInFlight => _condition[ConditionFlag.InFlight];

    public void Mount() => UseGeneralAction(MountRouletteGeneralAction, "Mount");
    public void Dismount() => UseGeneralAction(DismountGeneralAction, "Dismount");

    /// <summary>Straight off <c>TerritoryType.Mount</c> — false for every city zone.</summary>
    public bool CanMountHere
    {
        get
        {
            try
            {
                return _data.GetExcelSheet<TerritoryType>().GetRowOrDefault(_clientState.TerritoryType)?.Mount ?? false;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// The zone's aether currents are complete. A zone with no current set at all reads false: the
    /// base-game zones fly only after the story unlocks it, and the game refusing the flight is a
    /// worse way to learn that than walking.
    /// </summary>
    public bool CanFlyHere
    {
        get
        {
            try
            {
                var territory = _data.GetExcelSheet<TerritoryType>().GetRowOrDefault(_clientState.TerritoryType);
                if (territory is not { } t)
                    return false;
                var set = t.AetherCurrentCompFlgSet.RowId;
                if (set == 0)
                    return false;
                var state = PlayerState.Instance();
                return state != null && state->IsAetherCurrentZoneComplete(set);
            }
            catch
            {
                return false;
            }
        }
    }

    private void UseGeneralAction(uint id, string what)
    {
        try
        {
            var manager = ActionManager.Instance();
            if (manager != null)
                manager->UseAction(ActionType.GeneralAction, id);
        }
        catch (Exception ex)
        {
            _log($"{what} failed: {ex.Message}");
        }
    }

    // ── Chocobo companion ──

    /// <summary>Seconds the companion has left before it goes away; 0 when it is not out.</summary>
    public float CompanionTimeLeft
    {
        get
        {
            try
            {
                var ui = UIState.Instance();
                return ui == null ? 0f : ui->Buddy.CompanionInfo.TimeLeft;
            }
            catch
            {
                return 0f;
            }
        }
    }

    /// <summary>
    /// The field, not a city and not a duty: <see cref="CanMountHere"/> is false for exactly the
    /// zones a companion is refused in. And on foot and out of a fight, since Orion spends much of a
    /// run in the saddle or in combat, where the game refuses the green and a try is just an error
    /// line every thirty seconds.
    /// </summary>
    public bool CanSummonHere
        => CanMountHere && !IsMounted && !InCombat
           && !_condition[ConditionFlag.BoundByDuty]
           && !_condition[ConditionFlag.BoundByDuty56]
           && !_condition[ConditionFlag.BoundByDuty95];

    /// <summary>Normal-quality copies of an item in the bags.</summary>
    public int ItemCount(uint itemId)
    {
        try
        {
            var manager = InventoryManager.Instance();
            return manager == null ? 0 : Math.Max(0, manager->GetInventoryItemCount(itemId, false));
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Use an inventory item, as clicking it in the bag does.</summary>
    public bool UseItem(uint itemId)
    {
        try
        {
            var agent = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentInventoryContext.Instance();
            if (agent == null)
                return false;
            agent->UseItem(itemId, InventoryType.Invalid, 0, 0);
            return true;
        }
        catch (Exception ex)
        {
            _log($"UseItem {itemId} failed: {ex.Message}");
            return false;
        }
    }

    // ── Ariadne ──

    public bool NavReady => _ariadne.IsReady;
    public bool MoveTo(Vector3 destination, bool fly, float range) => _ariadne.MoveTo(destination, fly, range);
    public bool IsMoving => _ariadne.IsBusy;
    public string MoveResult => _ariadne.LastResult;
    public void StopMoving() => _ariadne.Stop();
    public Task<Vector3?> NearestReachablePoint(Vector3 near, float within) => _ariadne.NearestReachablePoint(near, within);

    // ── Teleport ──

    public uint? NearestAttunedAetheryte(uint territoryId, Vector3 near)
        => Attuned(_aetherytes.InTerritory(territoryId, near));

    public TravelRoute? RouteTo(uint territoryId, Vector3 near)
    {
        if (Attuned(_aetherytes.InTerritory(territoryId, near)) is { } direct)
            return new TravelRoute(direct, null, territoryId);

        // No aetheryte in the zone. It may still be half a city, reachable only across the aethernet.
        if (_aetherytes.ShardIn(territoryId, near) is not { } shard)
            return null;
        if (_aetherytes.GroupOfTerritory(TerritoryId) == shard.Group && _lifestream.ActiveAetheryte != 0)
            return new TravelRoute(null, shard.Name, territoryId);
        if (_aetherytes.HubOfGroup(shard.Group) is not { } hub || Attuned([hub]) is null)
            return null;
        return new TravelRoute(hub, shard.Name, _aetherytes.TerritoryOf(hub) ?? territoryId);
    }

    public bool AethernetTeleport(string destination, bool byNameOnly)
    {
        if (!byNameOnly && _aetherytes.StopNamed(destination) is { PlaceNameId: not 0 } stop)
        {
            _log($"Aethernet to {stop.Name} by id {stop.PlaceNameId}.");
            if (_lifestream.AethernetTeleportByPlaceName(stop.PlaceNameId))
                return true;
            _log($"Aethernet to {stop.Name} by id was refused; trying by name.");
        }
        _log($"Aethernet to \"{destination}\" by name.");
        return _lifestream.AethernetTeleport(destination);
    }

    public Vector3? AetherytePosition(uint aetheryteId) => _aetherytes.PositionOf(aetheryteId);

    public bool Teleport(uint aetheryteId) => _lifestream.Teleport(aetheryteId);

    public void AbortTravel()
    {
        if (_lifestream.IsBusy)
            _lifestream.Abort();
    }

    public void RefreshAetheryteList()
    {
        try
        {
            var telepo = Telepo.Instance();
            if (telepo != null)
                telepo->UpdateAetheryteList();
        }
        catch (Exception ex)
        {
            _log($"Refreshing the aetheryte list failed: {ex.Message}");
        }
    }

    /// <summary>
    /// The first of these the character has attuned. When the unlock state cannot be read at all the
    /// first candidate is taken and Lifestream gets to be the one that says no.
    /// </summary>
    private uint? Attuned(IReadOnlyList<uint> candidates)
    {
        if (candidates.Count == 0)
            return null;
        try
        {
            var state = UIState.Instance();
            if (state == null)
                return candidates[0];
            foreach (var id in candidates)
                if (state->IsAetheryteUnlocked(id))
                    return id;
            return null;
        }
        catch
        {
            return candidates[0];
        }
    }
}
