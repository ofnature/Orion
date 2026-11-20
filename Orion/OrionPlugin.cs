using System;
using System.Linq;
using System.Reflection;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Orion.Config;
using Orion.Services.Farm;
using Orion.Services.Fleet;
using Orion.Services.Game;
using Orion.Services.HuntLog;
using Orion.Services.Ipc;
using Orion.Services.Nav;
using Orion.Services.Travel;
using Orion.Windows;
using static Orion.Service;

namespace Orion;

/// <summary>
/// Orion — overworld mob farming and the hunt log, for the plugin suite.
///
/// <para>
/// The farm mode that grew up inside Daedalus, moved out so the rotation plugin is not also a
/// farmer. Orion picks the targets and does the travelling; Daedalus does the fighting, for as long
/// as <c>Orion.IsBusy</c> reads true. Everything runs off one frame tick.
/// </para>
/// </summary>
public sealed class OrionPlugin : IDalamudPlugin
{
    private const string CommandMain = "/orion";

    public static string PluginVersion { get; } =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    private readonly WindowSystem _windowSystem = new("Orion");
    private readonly OrionConfig _config;
    private readonly FarmModeService _farm;
    private readonly HuntLogRunner _hunt;
    private readonly TrophyRunner _trophy;
    private readonly GarlandDropSource _dropSource;
    private readonly OrionIpc _ipc;
    private readonly MainWindow _mainWindow;
    private readonly FleetPublisher _fleet;
    private readonly SettingsWindow _settingsWindow;
    private readonly EdgeDockWindow _edgeDock;

    public OrionPlugin(IDalamudPluginInterface pluginInterface)
    {
        // Create<T> constructs a T. It must be given the service holder, never this plugin — see
        // Service for why that distinction silently kills the client.
        pluginInterface.Create<Service>();

        _config = PluginInterface.GetPluginConfig() as OrionConfig ?? new OrionConfig();

        var nav = new VNavService(PluginInterface, Log);
        var mounts = new FarmMountHelper(Condition, DataManager, ClientState, Log);
        var huntProgress = new HuntLogProgress();
        var daedalus = new DaedalusIpc(PluginInterface);
        _farm = new FarmModeService(
            _config, ObjectTable, TargetManager, new EnemyScanner(ObjectTable), nav, ItemCount,
            huntProgress, daedalus, ClientState, mounts, Log);
        _farm.Notify += Say;
        _dropSource = new GarlandDropSource(DataManager, Log);

        // The hunt log run: Lifestream into the zone and Ariadne to the spot (Demeter's travel),
        // then farm mode with the log's target as its goal.
        var world = new TravelWorld(
            ClientState, ObjectTable, Condition, DataManager,
            new AriadneIpc(PluginInterface, Warn), new LifestreamIpc(PluginInterface, Warn), daedalus,
            new AetheryteCatalog(DataManager, Warn), message => Log.Information(message));
        var territories = DataManager.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>();
        Zones.Name = id => territories.GetRowOrDefault(id)?.PlaceName.ValueNullable?.Name.ExtractText();
        var catalog = new HuntLogCatalog(DataManager);
        var positions = new HuntPositions(HuntPositions.PathBeside(PluginInterface.AssemblyLocation.DirectoryName), DataManager, Warn);
        _hunt = new HuntLogRunner(
            catalog, huntProgress, positions, _farm, new SpotTravel(new Traveller(world), world, nav.FloorAt), world,
            () => ObjectTable.LocalPlayer?.ClassJob.RowId ?? 0, GrandCompany, MissingTravelPlugin, Say);

        // Trophy Hunt with no spot marked: the same travel, to a zone one of the item's droppers lives in.
        _trophy = new TrophyRunner(
            _farm, positions, new SpotTravel(new Traveller(world), world, nav.FloorAt), world,
            () => ObjectTable.LocalPlayer?.Level ?? 0, () => _hunt.IsRunning, MissingTravelPlugin, Say);

        _settingsWindow = new SettingsWindow(_config, SaveConfig, mounts);
        // What this character is doing goes out on Daedalus's LAN bridge, and what the others' Orions
        // send comes back in for the main window's fleet list.
        _fleet = new FleetPublisher(new RelayIpc(PluginInterface, Warn), FleetSnapshot, () => _config.ShareFleetStatus);

        var huntLogPanel = new HuntLogPanel(
            catalog, huntProgress, positions, _hunt, _farm, ObjectTable, ClientState,
            () => _mainWindow!.ShowMode(OrionMode.TrophyHunt),
            () => _trophy.IsRunning || (_farm.IsRunning && !_hunt.IsRunning));
        var drops = new DropSources(DropSources.PathBeside(PluginInterface.AssemblyLocation.DirectoryName), Warn);
        _mainWindow = new MainWindow(
            _farm, _hunt, _trophy, _dropSource, drops, DataManager, TargetManager, ClientState, ObjectTable,
            () => nav.IsAvailable, huntLogPanel, _fleet, _config, SaveConfig, _settingsWindow.Toggle);

        _ipc = new OrionIpc(PluginInterface, () => _farm.IsRunning || _hunt.IsRunning || _trophy.IsRunning);

        _windowSystem.AddWindow(_mainWindow);
        _windowSystem.AddWindow(_settingsWindow);

        // Orion's tab on the screen edge, stacked with the rest of the suite's. Any edge, any spot along it.
        _edgeDock = new EdgeDockWindow(
            PluginInterface,
            [new EdgeDockWindow.Tab("main", "Orion", () => ClientState.IsLoggedIn, _mainWindow.DrawDockPanel, 360f)],
            _ => (_config.EdgeDockEdge, _config.EdgeDockAlong),
            (_, edge, along) =>
            {
                (_config.EdgeDockEdge, _config.EdgeDockAlong) = (edge, along);
                SaveConfig();
            },
            () => _config.EdgeDockEnabled);
        _windowSystem.AddWindow(_edgeDock);

        PluginInterface.UiBuilder.Draw += _windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += OpenMain;
        PluginInterface.UiBuilder.OpenConfigUi += _settingsWindow.Toggle;
        Framework.Update += OnFrameworkUpdate;

        CommandManager.AddHandler(CommandMain, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Orion. \"/orion log\" the hunt log, \"/orion stop\" stops a run.",
        });

        Log.Information($"Orion v{PluginVersion} loaded.");
    }

    public void Dispose()
    {
        CommandManager.RemoveHandler(CommandMain);
        Framework.Update -= OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw -= _windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= OpenMain;
        PluginInterface.UiBuilder.OpenConfigUi -= _settingsWindow.Toggle;
        _windowSystem.RemoveAllWindows();
        _edgeDock.Dispose();
        _ipc.Dispose();
        _fleet.Dispose();
        _hunt.Dispose();
        _trophy.Dispose();
        _farm.Notify -= Say;
        _farm.Dispose();
        Zones.Name = null;
        _dropSource.Dispose();
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        _hunt.Tick();
        _trophy.Tick();
        _farm.Update();
        _fleet.Tick();
    }

    /// <summary>This character's line for the fleet, or null when there is no character to speak for.</summary>
    private FleetStatus? FleetSnapshot()
    {
        if (ObjectTable.LocalPlayer is not { } player)
            return null;

        var name = player.Name.TextValue;
        var world = player.HomeWorld.ValueNullable?.Name.ExtractText() ?? string.Empty;
        var profile = _farm.Profile;
        var running = _farm.IsRunning || _hunt.IsRunning || _trophy.IsRunning;

        var progress = string.Empty;
        if (_farm.IsRunning)
        {
            progress = profile.Hunt is { } hunt
                ? $"{hunt.Needed - _farm.HuntKillsLeft} / {hunt.Needed}"
                : $"{_farm.PeekItemCount(profile.ItemId)} / {profile.TargetCount}";
        }

        return new FleetStatus
        {
            SenderId = $"{name}@{world}",
            Character = name,
            World = world,
            Running = running,
            Mode = !running ? string.Empty : _hunt.IsRunning ? ModeNames.HuntLog : ModeNames.TrophyHunt,
            Goal = !_farm.IsRunning ? string.Empty : profile.Hunt?.MobName ?? profile.ItemName,
            Progress = progress,
            StatusLine = _hunt.IsRunning ? _hunt.StatusLine
                : _trophy.IsRunning ? _trophy.StatusLine
                : _farm.IsRunning ? _farm.StatusLine : string.Empty,
            Zone = Zones.Name?.Invoke(ClientState.TerritoryType) ?? string.Empty,
            SentUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
    }

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "stop":
                StopAll("stopped by command");
                break;
            case "log":
                _mainWindow.ShowMode(OrionMode.HuntLog);
                break;
            default:
                OpenMain();
                break;
        }
    }

    private void OpenMain() => _mainWindow.IsOpen = true;

    private void SaveConfig() => PluginInterface.SavePluginConfig(_config);

    /// <summary>The hunt log run owns the farm while it is going, so it is the one to stop.</summary>
    private void StopAll(string reason)
    {
        if (_hunt.IsRunning)
            _hunt.Stop(reason);
        else if (_trophy.IsRunning)
            _trophy.Stop(reason);
        else
            _farm.Stop(reason);
    }

    private static void Say(string message) => ChatGui.Print($"[Orion] {message}");

    private static void Warn(string message) => Log.Warning(message);

    /// <summary>Lifestream or Ariadne, whichever the hunt log run would miss first; null when both are loaded.</summary>
    private static string? MissingTravelPlugin()
    {
        foreach (var name in new[] { "Lifestream", "Ariadne" })
        {
            if (!PluginInterface.InstalledPlugins.Any(p => p.IsLoaded && string.Equals(p.InternalName, name, StringComparison.OrdinalIgnoreCase)))
                return name;
        }
        return null;
    }

    /// <summary>The Grand Company the character serves (1 to 3), 0 for none.</summary>
    private static unsafe uint GrandCompany()
    {
        try
        {
            var state = PlayerState.Instance();
            return state == null ? 0u : state->GrandCompany;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Normal-quality copies of an item in the bag, as Daedalus's farm counted them (overworld
    /// drops are never high quality).
    /// </summary>
    private static unsafe uint ItemCount(uint itemId)
    {
        try
        {
            var manager = InventoryManager.Instance();
            if (manager == null)
                return 0;
            var count = manager->GetInventoryItemCount(itemId, false);
            return count < 0 ? 0u : (uint)count;
        }
        catch
        {
            return 0;
        }
    }
}
