using System.Numerics;
using Orion.Services.Travel;

namespace Orion.Tests;

/// <summary>
/// The game as <see cref="Traveller"/> sees it, scripted: Lifestream teleports and Ariadne moves.
/// A move either arrives at once (<see cref="ArriveOnMove"/>), ending with <see cref="ResultOnArrive"/>,
/// or stays running until the test ends it with <see cref="FinishMove"/>.
/// </summary>
public sealed class FakeTravelWorld : ITravelWorld
{
    public DateTime UtcNow { get; set; } = new(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);
    public uint TerritoryId { get; set; } = 1187;
    public Vector3 PlayerPosition { get; set; }
    public bool IsReady { get; set; } = true;
    public bool IsOccupied { get; set; }
    public bool IsTravelBusy { get; set; }
    public bool InCombat { get; set; }
    public bool IsDead { get; set; }
    public bool DaedalusDisabledByUser { get; set; }
    public bool IsMounted { get; set; }
    public bool IsInFlight { get; set; }
    public bool CanMountHere { get; set; } = true;
    public bool CanFlyHere { get; set; }
    public bool NavReady { get; set; } = true;
    public bool IsMoving { get; set; }
    public string MoveResult { get; set; } = string.Empty;

    public List<string> Calls { get; } = [];
    public List<string> Logged { get; } = [];

    /// <summary>A move puts the player at the destination at once.</summary>
    public bool ArriveOnMove { get; set; }

    /// <summary>What Ariadne says when an arriving move ends.</summary>
    public string ResultOnArrive { get; set; } = "goal reached";

    /// <summary>Ariadne refuses moves (busy, no player).</summary>
    public bool RefuseMoves { get; set; }

    /// <summary>A teleport lands the player in the aetheryte's territory at once.</summary>
    public bool ArriveOnTeleport { get; set; } = true;
    public bool TeleportAccepted { get; set; } = true;

    /// <summary>True models a dismount from the air: asked for, but still coming down.</summary>
    public bool HoldsMount { get; set; }

    /// <summary>Attuned aetherytes: id → (territory, position).</summary>
    public Dictionary<uint, (uint Territory, Vector3 At)> Attuned { get; } = new();

    /// <summary>City districts with no crystal: territory → (the city's crystal, the shard in the district, where the shard stands).</summary>
    public Dictionary<uint, (uint Hub, string Shard, Vector3 At)> Districts { get; } = new();

    /// <summary>Lifestream counts the character as at a shard (true after a teleport to a crystal).</summary>
    public bool AtShard { get; set; }
    public bool HopAccepted { get; set; } = true;

    /// <summary>A hop that is accepted but goes nowhere — Lifestream matched no destination.</summary>
    public bool HopGoesNowhere { get; set; }

    /// <summary>Set: a hop is refused from further than this off the district's city crystal.</summary>
    public float? HopNeedsWithin { get; set; }

    /// <summary>A teleport lands this far from the crystal (the game puts you a few yalms off it).</summary>
    public Vector3 TeleportLandOffset { get; set; }

    public Func<Vector3, float, Vector3?>? NearestReachableFn { get; set; }
    public Vector3? LastMoveTarget { get; private set; }
    public bool? LastMoveFly { get; private set; }
    public float? LastMoveRange { get; private set; }
    public int MoveCount { get; private set; }

    public void Advance(double seconds) => UtcNow = UtcNow.AddSeconds(seconds);

    /// <summary>End a running move the way Ariadne would, optionally somewhere else.</summary>
    public void FinishMove(string result, Vector3? at = null)
    {
        IsMoving = false;
        MoveResult = result;
        if (at is { } p) PlayerPosition = p;
    }

    public void Mount() { Calls.Add("Mount"); IsMounted = true; }

    public void Dismount()
    {
        Calls.Add("Dismount");
        if (!HoldsMount) { IsMounted = false; IsInFlight = false; }
    }

    public bool MoveTo(Vector3 destination, bool fly, float range)
    {
        Calls.Add($"Move {destination.X:F0},{destination.Y:F0},{destination.Z:F0} fly={fly}");
        if (RefuseMoves) return false;
        LastMoveTarget = destination;
        LastMoveFly = fly;
        LastMoveRange = range;
        MoveCount++;
        if (fly && !IsMounted) IsMounted = true;   // Ariadne calls the mount for a flight
        if (ArriveOnMove)
        {
            PlayerPosition = destination;
            IsMoving = false;
            MoveResult = ResultOnArrive;
        }
        else
        {
            IsMoving = true;
            MoveResult = "pathfinding…";
        }
        return true;
    }

    public void StopMoving() { Calls.Add("Stop"); IsMoving = false; }

    public Task<Vector3?> NearestReachablePoint(Vector3 near, float within)
        => Task.FromResult(NearestReachableFn is { } f ? f(near, within) : near);

    public uint? NearestAttunedAetheryte(uint territoryId, Vector3 near)
        => Attuned.Where(a => a.Value.Territory == territoryId)
            .OrderBy(a => Vector3.Distance(a.Value.At, near))
            .Select(a => (uint?)a.Key)
            .FirstOrDefault();

    public TravelRoute? RouteTo(uint territoryId, Vector3 near)
    {
        if (NearestAttunedAetheryte(territoryId, near) is { } direct)
            return new TravelRoute(direct, null, territoryId);
        if (!Districts.TryGetValue(territoryId, out var d) || !Attuned.TryGetValue(d.Hub, out var hub))
            return null;
        return TerritoryId == hub.Territory && AtShard
            ? new TravelRoute(null, d.Shard, territoryId)
            : new TravelRoute(d.Hub, d.Shard, hub.Territory);
    }

    public bool AethernetTeleport(string destination, bool byNameOnly)
    {
        Calls.Add($"Aethernet {destination}" + (byNameOnly ? " by name" : ""));
        if (!HopAccepted) return false;
        if (HopNeedsWithin is { } within
            && !Districts.Values.Any(d => d.Shard == destination && Attuned.TryGetValue(d.Hub, out var hub)
                                          && Vector3.Distance(PlayerPosition, hub.At) <= within))
            return false;
        if (HopGoesNowhere) return true;
        foreach (var (territory, d) in Districts)
            if (d.Shard == destination)
            {
                TerritoryId = territory;
                PlayerPosition = d.At;
            }
        return true;
    }

    public Vector3? AetherytePosition(uint aetheryteId) => Attuned.TryGetValue(aetheryteId, out var a) ? a.At : null;

    public void RefreshAetheryteList() => Calls.Add("RefreshAetherytes");

    public bool Teleport(uint aetheryteId)
    {
        Calls.Add($"Teleport {aetheryteId}");
        if (!TeleportAccepted) return false;
        if (ArriveOnTeleport && Attuned.TryGetValue(aetheryteId, out var a))
        {
            TerritoryId = a.Territory;
            PlayerPosition = a.At + TeleportLandOffset;
            AtShard = true;
        }
        return true;
    }

    public void AbortTravel() => Calls.Add("AbortTravel");
    public void Log(string message) => Logged.Add(message);
}
