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
    private readonly GarlandDropSource _dropSource;
    private readonly OrionIpc _ipc;
    private readonly MainWindow _mainWindow;
    private readonly HuntLogWindow _huntLogWindow;
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
            catalog, huntProgress, positions, _farm, new Traveller(world), world, nav.FloorAt,
            () => ObjectTable.LocalPlayer?.ClassJob.RowId ?? 0, GrandCompany, MissingTravelPlugin, Say);

        _settingsWindow = new SettingsWindow(_config, SaveConfig, mounts);
        _huntLogWindow = new HuntLogWindow(catalog, huntProgress, positions, _hunt, _farm, ObjectTable, ClientState, OpenMain);
        _mainWindow = new MainWindow(
            _farm, _hunt, _dropSource, DataManager, TargetManager, ClientState, ObjectTable,
            () => nav.IsAvailable, _huntLogWindow.Toggle, _settingsWindow.Toggle);

        _ipc = new OrionIpc(PluginInterface, () => _farm.IsRunning || _hunt.IsRunning);

        _windowSystem.AddWindow(_mainWindow);
        _windowSystem.AddWindow(_huntLogWindow);
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
        _hunt.Dispose();
        _farm.Notify -= Say;
        _farm.Dispose();
        Zones.Name = null;
        _dropSource.Dispose();
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        _hunt.Tick();
        _farm.Update();
    }

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "stop":
                StopAll("stopped by command");
                break;
            case "log":
                _huntLogWindow.IsOpen = true;
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
