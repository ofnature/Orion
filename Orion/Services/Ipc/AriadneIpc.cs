using System;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace Orion.Services.Ipc;

/// <summary>
/// Ariadne, the one thing that moves the character inside a zone (D:\Dev\Ariadne, docs/consumer-ipc.md).
///
/// <para>
/// The caller hands it a point and lets it do the rest: it pathfinds on Mnemosyne's mesh, calls the
/// mount and flies when the caller asks to fly, recovers from stalls itself, walks the last stretch
/// straight where the mesh stops short, and says how the move ended in <see cref="LastResult"/>.
/// Cross-zone travel is the consumer's job — that is the Lifestream teleport in front of it.
/// </para>
///
/// <para>
/// Fails open: a missing Ariadne is "not ready" and "refused", never an exception in a run loop.
/// Ariadne's session switches (prefer flying, use aetherytes) are never touched — they are global to
/// every consumer, and Odysseus shares this Ariadne.
/// </para>
/// </summary>
public sealed class AriadneIpc
{
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly Action<string>? _log;
    private bool _warned;

    private ICallGateSubscriber<bool>? _isConnected;
    private ICallGateSubscriber<int>? _zoneStatus;
    private ICallGateSubscriber<Vector3, bool, bool>? _moveTo;
    private ICallGateSubscriber<Vector3, bool, float, bool>? _moveCloseTo;
    private ICallGateSubscriber<bool>? _pathfindInProgress;
    private ICallGateSubscriber<bool>? _isRunning;
    private ICallGateSubscriber<string>? _lastResult;
    private ICallGateSubscriber<object>? _stop;
    private ICallGateSubscriber<Vector3, float, float, Task<Vector3?>>? _nearestReachable;

    public AriadneIpc(IDalamudPluginInterface pluginInterface, Action<string>? log = null)
    {
        _pluginInterface = pluginInterface;
        _log = log;
    }

    /// <summary>Connected to Mnemosyne and this zone's mesh is in hand (ZoneStatus 2 or 3) — true ~0.1 s after zone-in.</summary>
    public bool IsReady => Try(() =>
        (_isConnected ??= _pluginInterface.GetIpcSubscriber<bool>("Ariadne.IsConnected")).InvokeFunc()
        && (_zoneStatus ??= _pluginInterface.GetIpcSubscriber<int>("Ariadne.ZoneStatus")).InvokeFunc() is 2 or 3, false);

    /// <summary>
    /// Pathfind and follow. A range above zero stops within it of the point itself (in 3D, measured to
    /// the target, not the route). <paramref name="fly"/> asks for a volume path: Ariadne calls the
    /// mount if the character is on foot, and keeps it at the end — the caller lands.
    /// False when the request was refused (already pathfinding, no player, Ariadne missing).
    /// </summary>
    public bool MoveTo(Vector3 destination, bool fly, float range) => Try(() => range > 0f
        ? (_moveCloseTo ??= _pluginInterface.GetIpcSubscriber<Vector3, bool, float, bool>("Ariadne.SimpleMove.PathfindAndMoveCloseTo"))
            .InvokeFunc(destination, fly, range)
        : (_moveTo ??= _pluginInterface.GetIpcSubscriber<Vector3, bool, bool>("Ariadne.SimpleMove.PathfindAndMoveTo"))
            .InvokeFunc(destination, fly), false);

    /// <summary>
    /// A move is under way: pathfinding, a same-zone teleport leg, waiting for the mesh, calling the
    /// mount, or following the path.
    /// </summary>
    public bool IsBusy => Try(() =>
        (_pathfindInProgress ??= _pluginInterface.GetIpcSubscriber<bool>("Ariadne.SimpleMove.PathfindInProgress")).InvokeFunc()
        || (_isRunning ??= _pluginInterface.GetIpcSubscriber<bool>("Ariadne.Path.IsRunning")).InvokeFunc(), false);

    /// <summary>
    /// How the last move ended: "goal reached", "closest reachable point, N.Ny short", "stuck (Ny short)",
    /// "no path (reason)", "stopped", "zone changed", "no such object"; while moving, what it is doing.
    /// </summary>
    public string LastResult => Try(() =>
        (_lastResult ??= _pluginInterface.GetIpcSubscriber<string>("Ariadne.SimpleMove.LastResult")).InvokeFunc(), string.Empty);

    public void Stop() => Try<object?>(() =>
    {
        (_stop ??= _pluginInterface.GetIpcSubscriber<object>("Ariadne.Path.Stop")).InvokeAction();
        return null;
    }, null);

    /// <summary>The nearest point the mesh can reach, asked off the frame; null inside the task when there is none.</summary>
    public Task<Vector3?> NearestReachablePoint(Vector3 near, float within) => Try(() =>
        (_nearestReachable ??= _pluginInterface.GetIpcSubscriber<Vector3, float, float, Task<Vector3?>>("Ariadne.Query.Mesh.NearestPointReachable"))
            .InvokeFunc(near, within, within), Task.FromResult<Vector3?>(null));

    private T Try<T>(Func<T> call, T fallback)
    {
        try
        {
            var result = call();
            _warned = false;
            return result;
        }
        catch (Exception ex)
        {
            if (!_warned)
            {
                _warned = true;
                _log?.Invoke($"Ariadne unavailable ({ex.GetType().Name}) — movement waits until it loads.");
            }
            return fallback;
        }
    }
}
