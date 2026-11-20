using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace Orion.Services.Nav;

/// <summary>
/// Navmesh IPC adapter, copied from Daedalus and cut down to what farm mode calls: Ariadne when it
/// is loaded, otherwise vnavmesh (Ariadne is a vnavmesh fork with the same call shapes under
/// <c>Ariadne.*</c>). Fail-open when the plugin or navmesh is unavailable.
/// <para>
/// Only the calls that MOVE the character follow that choice. The floor query stays on the
/// <c>vnavmesh.*</c> name, answered by vnavmesh when it is loaded and by Ariadne's compatibility
/// gates when it is not: Ariadne's own version returns a task rather than an answer.
/// </para>
/// </summary>
public sealed class VNavService : IVNavService
{
    private const string AriadneName = "Ariadne";
    private const string VnavmeshName = "vnavmesh";
    private const float DefaultFloorQueryHalfExtent = 1f;

    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly IPluginLog? _log;

    /// <summary>One plugin's movement gates.</summary>
    private sealed class Gates
    {
        public ICallGateSubscriber<bool>? NavIsReady;
        public ICallGateSubscriber<bool>? PathIsRunning;
        public ICallGateSubscriber<bool>? PathfindInProgress;
        public ICallGateSubscriber<Vector3, bool, bool>? PathfindAndMoveTo;
        public ICallGateSubscriber<object>? PathStop;
    }

    private readonly Dictionary<string, Gates> _gates = new();
    private ICallGateSubscriber<Vector3, bool, float, Vector3?>? _queryPointOnFloor;

    // The plugin that was driving when this service last started a move, so Ariadne loading or
    // unloading mid-path still stops the walk the old one is running.
    private string? _lastMover;

    public VNavService(IDalamudPluginInterface pluginInterface, IPluginLog? log = null)
    {
        _pluginInterface = pluginInterface;
        _log = log;
    }

    /// <summary>The plugin to drive this call: Ariadne when loaded, otherwise vnavmesh.</summary>
    private string Mover => AriadneLoaded ? AriadneName : VnavmeshName;

    // "Is Ariadne loaded" walks the installed-plugin list; movement asks several times a frame, and
    // a plugin loading or unloading is not a per-frame event. Nullable on purpose: a long.MinValue
    // start overflows the subtraction and the check never runs (Daedalus v0.1.100 to v0.1.104).
    private bool _ariadneLoaded;
    private long? _ariadneCheckedAt;

    private bool AriadneLoaded
    {
        get
        {
            var now = Environment.TickCount64;
            if (_ariadneCheckedAt is not { } last || now - last >= 1000)
            {
                _ariadneLoaded = IsPluginLoaded(AriadneName);
                _ariadneCheckedAt = now;
            }
            return _ariadneLoaded;
        }
    }

    private Gates GatesFor(string plugin)
    {
        if (!_gates.TryGetValue(plugin, out var g))
        {
            var p = plugin + ".";
            g = new Gates
            {
                NavIsReady = _pluginInterface.GetIpcSubscriber<bool>(p + "Nav.IsReady"),
                PathIsRunning = _pluginInterface.GetIpcSubscriber<bool>(p + "Path.IsRunning"),
                PathfindInProgress = _pluginInterface.GetIpcSubscriber<bool>(p + "SimpleMove.PathfindInProgress"),
                PathfindAndMoveTo = _pluginInterface.GetIpcSubscriber<Vector3, bool, bool>(p + "SimpleMove.PathfindAndMoveTo"),
                PathStop = _pluginInterface.GetIpcSubscriber<object>(p + "Path.Stop"),
            };
            _gates[plugin] = g;
        }
        return g;
    }

    private Gates Current => GatesFor(Mover);

    public bool IsAvailable => IsPluginLoaded(Mover);

    public bool IsNavReady => TryInvoke(() => Current.NavIsReady?.InvokeFunc() ?? false);

    public bool IsPathRunning => TryInvoke(() => Current.PathIsRunning?.InvokeFunc() ?? false);

    public bool IsPathfindInProgress => TryInvoke(() => Current.PathfindInProgress?.InvokeFunc() ?? false);

    public VNavMoveResult PathfindAndMoveTo(Vector3 destination, bool fly = false)
    {
        if (!IsAvailable)
            return VNavMoveResult.PluginUnavailable;

        if (!IsNavReady)
            return VNavMoveResult.NavmeshNotReady;

        _lastMover = Mover;

        try
        {
            return Current.PathfindAndMoveTo?.InvokeFunc(destination, fly) == true
                ? VNavMoveResult.Queued
                : VNavMoveResult.Busy;
        }
        catch (Exception ex)
        {
            _log?.Warning(ex, "[VNavService] PathfindAndMoveTo failed.");
            return VNavMoveResult.PluginUnavailable;
        }
    }

    public void Stop()
    {
        // The plugin that started the walk, if the mover changed since, then the current one.
        if (_lastMover is { } last && last != Mover && IsPluginLoaded(last))
            TryInvoke(() => GatesFor(last).PathStop?.InvokeAction());

        if (!IsAvailable)
            return;

        TryInvoke(() => Current.PathStop?.InvokeAction());
    }

    public Vector3 SnapToFloor(Vector3 position)
    {
        // The vnavmesh.* floor query has an answerer: vnavmesh, or Ariadne's compatibility gates.
        if (!IsPluginLoaded(VnavmeshName) && !IsPluginLoaded(AriadneName))
            return position;

        _queryPointOnFloor ??= _pluginInterface.GetIpcSubscriber<Vector3, bool, float, Vector3?>("vnavmesh.Query.Mesh.PointOnFloor");

        try
        {
            var snapped = _queryPointOnFloor.InvokeFunc(position, false, DefaultFloorQueryHalfExtent);
            return snapped ?? position;
        }
        catch (Exception ex)
        {
            _log?.Debug(ex, "[VNavService] SnapToFloor failed; using raw position.");
            return position;
        }
    }

    public Vector3? FloorAt(float x, float z)
    {
        if (!IsPluginLoaded(VnavmeshName) && !IsPluginLoaded(AriadneName))
            return null;

        _queryPointOnFloor ??= _pluginInterface.GetIpcSubscriber<Vector3, bool, float, Vector3?>("vnavmesh.Query.Mesh.PointOnFloor");

        try
        {
            // The query finds the highest floor below the point: asked from the top of the world,
            // with the half-extent Ariadne itself uses for a map flag.
            return _queryPointOnFloor.InvokeFunc(new Vector3(x, 1024f, z), false, 5f);
        }
        catch (Exception ex)
        {
            _log?.Debug(ex, "[VNavService] FloorAt failed.");
            return null;
        }
    }

    private bool IsPluginLoaded(string internalName)
    {
        return _pluginInterface.InstalledPlugins.Any(p =>
            (p.InternalName.Equals(internalName, StringComparison.OrdinalIgnoreCase)
             || p.Name.Equals(internalName, StringComparison.OrdinalIgnoreCase))
            && p.IsLoaded);
    }

    private static T TryInvoke<T>(Func<T> func, T fallback = default!)
    {
        try
        {
            return func();
        }
        catch
        {
            return fallback;
        }
    }

    private static void TryInvoke(Action action)
    {
        try
        {
            action();
        }
        catch
        {
            // fail-open
        }
    }
}
