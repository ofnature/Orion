using System.Numerics;

namespace Orion.Services.Nav;

/// <summary>
/// Navmesh IPC adapter (Ariadne or vnavmesh). Executor only: it moves where it is told.
/// </summary>
public interface IVNavService
{
    /// <summary>The navmesh plugin is installed and loaded.</summary>
    bool IsAvailable { get; }

    /// <summary><c>Nav.IsReady</c>: navmesh loaded for the current zone.</summary>
    bool IsNavReady { get; }

    /// <summary><c>Path.IsRunning</c>.</summary>
    bool IsPathRunning { get; }

    /// <summary><c>SimpleMove.PathfindInProgress</c>.</summary>
    bool IsPathfindInProgress { get; }

    /// <summary><c>SimpleMove.PathfindAndMoveTo</c>.</summary>
    VNavMoveResult PathfindAndMoveTo(Vector3 destination, bool fly = false);

    /// <summary><c>Path.Stop</c>.</summary>
    void Stop();

    /// <summary><c>vnavmesh.Query.Mesh.PointOnFloor</c> when available; otherwise <paramref name="position"/>.</summary>
    Vector3 SnapToFloor(Vector3 position);

    /// <summary>
    /// The highest floor of the current zone's mesh at a world X and Z, or null when there is none
    /// within a few yalms (or no mesh yet). For positions known only in two dimensions.
    /// </summary>
    Vector3? FloorAt(float x, float z);
}
