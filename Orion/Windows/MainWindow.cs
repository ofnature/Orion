using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Orion.Services.Farm;
using Orion.Services.HuntLog;
using Orion.Windows.Components;
using Orion.Windows.Sections;

namespace Orion.Windows;

/// <summary>
/// Farm setup and control, laid out the way Parcae's main window is: the header banner, then a
/// numbered idle flow ending in START, or the running dashboard with STOP. The working profile is
/// session-only by design (Daedalus's docs/farm-mode.md); travel settings live in the settings window.
/// </summary>
public sealed class MainWindow : Window
{
    private readonly FarmModeService _farm;
    private readonly HuntLogRunner _hunt;
    private readonly GarlandDropSource _dropSource;
    private readonly IDataManager _dataManager;
    private readonly ITargetManager _targetManager;
    private readonly IClientState _clientState;
    private readonly IObjectTable _objectTable;
    private readonly Func<bool> _navAvailable;
    private readonly Action _toggleHuntLog;
    private readonly Action _toggleSettings;

    private string _itemSearch = "";
    private string _lastItemSearch = "";
    private readonly List<(uint Id, string Name)> _itemResults = new();
    private int _targetCountInput = 1;

    private string? _startError;
    private readonly Dictionary<ulong, string> _pendingLocations = new();
    private string _locationStatus = "";

    // "Is a navmesh plugin loaded" walks the installed-plugin list: asked once a second, not per frame.
    private bool _navOk;
    private long? _navCheckedAt;

    public MainWindow(
        FarmModeService farm,
        HuntLogRunner hunt,
        GarlandDropSource dropSource,
        IDataManager dataManager,
        ITargetManager targetManager,
        IClientState clientState,
        IObjectTable objectTable,
        Func<bool> navAvailable,
        Action toggleHuntLog,
        Action toggleSettings)
        : base("Orion — Hunt Operations###OrionMain")
    {
        _farm = farm;
        _hunt = hunt;
        _dropSource = dropSource;
        _dataManager = dataManager;
        _targetManager = targetManager;
        _clientState = clientState;
        _objectTable = objectTable;
        _navAvailable = navAvailable;
        _toggleHuntLog = toggleHuntLog;
        _toggleSettings = toggleSettings;

        Size = new Vector2(520, 720);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        using var style = Styling.PushWindowStyle();
        RefreshNav();

        var (status, detail, accent) = !_navOk
            ? ("SETUP", "Ariadne or vnavmesh required", Styling.AccentRose)
            : Busy
                ? ("HUNTING", _hunt.IsRunning ? _hunt.StatusLine : _farm.StatusLine, Styling.AccentMint)
                : ("READY", "waiting for a quarry", Styling.AccentVioletSoft);

        OrionHeader.Draw(status, detail, accent,
        [
            new HeaderAction(FontAwesomeIcon.Book, "huntlog", "Hunt log", _toggleHuntLog),
            new HeaderAction(FontAwesomeIcon.Cog, "settings", "Settings", _toggleSettings),
        ]);
        Styling.VSpace(7f);

        if (Busy)
            DrawRunning();
        else
            DrawIdle();
    }

    /// <summary>A farm run, or a hunt log run (which is between farms while it travels).</summary>
    private bool Busy => _farm.IsRunning || _hunt.IsRunning;

    private void RefreshNav()
    {
        var now = Environment.TickCount64;
        if (_navCheckedAt is not { } last || now - last >= 1000)
        {
            _navOk = _navAvailable();
            _navCheckedAt = now;
        }
    }

    /// <summary>
    /// The edge-dock tab's panel: the running dashboard while a run is on, otherwise what is set up
    /// and START when nothing blocks it. The panel has a fixed width, as the tiles size off it.
    /// </summary>
    public void DrawDockPanel()
    {
        using var style = Styling.PushWindowStyle();
        RefreshNav();

        if (ImGui.SmallButton("Open Orion##dock"))
            IsOpen = true;
        ImGui.SameLine();
        if (ImGui.SmallButton("Hunt log##dock"))
            _toggleHuntLog();
        ImGui.Separator();

        if (Busy)
        {
            DrawRunning();
            return;
        }

        var profile = _farm.Profile;
        var reason = BlockingReason(profile);
        ImGui.TextColored(Styling.TextSecondary, GoalSummary(profile) ?? "nothing set up");
        if (HeroButton.Draw(FontAwesomeIcon.Play, "START", null, Styling.AccentViolet, reason == null, reason))
            _startError = _farm.Start();
        if (_startError != null)
            ImGui.TextColored(Styling.AccentRose, _startError);
    }

    // ----- Running dashboard -----

    private void DrawRunning()
    {
        var profile = _farm.Profile;

        // A hunt log run on the road: farm mode has nothing to show until it reaches the mob.
        if (!_farm.IsRunning)
        {
            Styling.SectionLabel($"{_hunt.Book?.Name} hunt log");
            ImGui.TextColored(Styling.TextSecondary, _hunt.StatusLine);
            Styling.VSpace(4f);
            if (StopButton())
                _hunt.Stop("stopped by user");
            return;
        }
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var tileWidth = (ImGui.GetContentRegionAvail().X - spacing * 2f) / 3f;

        StatTile.Draw("Kills", _farm.Kills.ToString(), null, Styling.AccentBlue, tileWidth);
        ImGui.SameLine();
        if (profile.Hunt is { } hunt)
        {
            StatTile.Draw("Hunt log", $"{hunt.Needed - _farm.HuntKillsLeft} / {hunt.Needed}", hunt.MobName, Styling.AccentAmber, tileWidth);
        }
        else
        {
            // Live bag read (not the service's poll snapshot) so the display can never lag drops.
            StatTile.Draw("In bag", $"{_farm.PeekItemCount(profile.ItemId)} / {profile.TargetCount}", profile.ItemName, Styling.AccentAmber, tileWidth);
        }
        ImGui.SameLine();
        StatTile.Draw("Spot", $"{_farm.ActiveSpotIndex + 1} / {profile.Spots.Count}", null, Styling.AccentMint, tileWidth);

        Styling.VSpace(4f);
        ImGui.TextColored(Styling.TextSecondary, _farm.StatusLine);
        Styling.VSpace(4f);

        if (StopButton())
        {
            // The hunt log run owns the farm while it is going, so it is the one to stop.
            if (_hunt.IsRunning)
                _hunt.Stop("stopped by user");
            else
                _farm.Stop("stopped by user");
        }
    }

    private bool StopButton()
        => HeroButton.Draw(FontAwesomeIcon.Stop, "STOP", "running", Styling.AccentRose, true);

    // ----- Idle flow -----

    private void DrawIdle()
    {
        var profile = _farm.Profile;

        StepHeader.Draw(1, "Choose the quarry", GoalSummary(profile));
        DrawGoal(profile);

        ImGui.Spacing();
        StepHeader.Draw(2, "Name the prey", profile.Mobs.Count > 0 ? $"{profile.Mobs.Count} mob kind(s)" : null);
        DrawMobs(profile);

        ImGui.Spacing();
        StepHeader.Draw(3, "Mark the ground", profile.Spots.Count > 0 ? $"{profile.Spots.Count} spot(s)" : null);
        DrawSpots(profile);

        ImGui.Spacing();
        ImGui.Spacing();
        var reason = BlockingReason(profile);
        if (HeroButton.Draw(FontAwesomeIcon.Play, "START", GoalSummary(profile), Styling.AccentViolet, reason == null, reason))
            _startError = _farm.Start();

        if (_startError != null)
            ImGui.TextColored(Styling.AccentRose, _startError);
    }

    private static string? GoalSummary(FarmProfile profile)
    {
        if (profile.Hunt is { } hunt)
            return $"hunt log: {hunt.MobName}";
        return profile.ItemId != 0 ? $"{profile.ItemName} ×{profile.TargetCount}" : null;
    }

    /// <summary>What still stops a run, or null when START can be pressed.</summary>
    private string? BlockingReason(FarmProfile profile)
    {
        if (!_navOk)
            return "install Ariadne or vnavmesh";
        if (profile.Hunt == null && profile.ItemId == 0)
            return "pick an item or a hunt log target";
        if (profile.Mobs.Count == 0)
            return "list at least one mob";
        if (profile.Spots.Count == 0)
            return "mark at least one spot";
        return null;
    }

    private void DrawGoal(FarmProfile profile)
    {
        if (profile.Hunt is { } hunt)
        {
            ImGui.Text($"Hunt log: {hunt.MobName}");
            ImGui.SameLine();
            ImGui.TextColored(Styling.TextDim, $"({hunt.Needed - _farm.HuntKillsLeft} / {hunt.Needed} killed)");
            ImGui.SameLine();
            if (ImGui.SmallButton("farm an item instead"))
                profile.Hunt = null;
            return;
        }

        if (profile.ItemId != 0)
        {
            ImGui.Text(profile.ItemName);
            ImGui.SameLine();
            ImGui.TextColored(Styling.TextDim, $"(id {profile.ItemId}, in bag: {_farm.PeekItemCount(profile.ItemId)})");
            ImGui.SameLine();
            if (ImGui.SmallButton("clear##orionItem"))
            {
                profile.ItemId = 0;
                profile.ItemName = "";
                _startError = null;
            }
        }

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##orionItemSearch", "search item by name (3+ letters)...", ref _itemSearch, 64);
        if (_itemSearch.Length >= 3 && _itemSearch != _lastItemSearch)
        {
            _lastItemSearch = _itemSearch;
            RebuildItemResults();
        }
        if (_itemSearch.Length >= 3 && _itemResults.Count > 0)
        {
            if (ImGui.BeginListBox("##orionItemResults", new Vector2(-1, Math.Min(_itemResults.Count, 6) * ImGui.GetTextLineHeightWithSpacing())))
            {
                foreach (var (id, name) in _itemResults)
                {
                    if (ImGui.Selectable($"{name}##item{id}", profile.ItemId == id))
                    {
                        profile.ItemId = id;
                        profile.ItemName = name;
                        _itemSearch = "";
                        _lastItemSearch = "";
                    }
                }
                ImGui.EndListBox();
            }
        }

        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("target count", ref _targetCountInput))
            profile.TargetCount = Math.Max(1, _targetCountInput);
        _targetCountInput = profile.TargetCount;

        ImGui.SameLine();
        if (ImGui.SmallButton("or pick from the hunt log"))
            _toggleHuntLog();
    }

    private void DrawMobs(FarmProfile profile)
    {
        ImGui.TextColored(Styling.TextDim, "The list is not saved: it lasts the session.");
        if (ImGui.Button("Add current target"))
        {
            if (_targetManager.Target is IBattleNpc npc && npc.NameId != 0)
                profile.AddMob(npc.NameId, npc.Name.ToString());
        }

        // GarlandTools dropper lookup (same source Monster Loot Hunter uses — it has no IPC).
        if (profile.Hunt == null && profile.ItemId != 0)
        {
            ImGui.SameLine();
            var cached = _dropSource.TryGetCached(profile.ItemId);
            if (cached == null)
            {
                ImGui.BeginDisabled(_dropSource.IsBusy);
                if (ImGui.Button(_dropSource.IsBusy ? "Looking up..." : "Find droppers"))
                    _dropSource.BeginLookup(profile.ItemId);
                ImGui.EndDisabled();
                if (_dropSource.LastError != null)
                    ImGui.TextColored(Styling.AccentRose, _dropSource.LastError);
            }
            else if (cached.Count == 0)
            {
                ImGui.TextColored(Styling.TextDim, "GarlandTools lists no mob droppers for this item.");
            }
            else
            {
                ImGui.TextColored(Styling.TextDim, "Drops from (GarlandTools):");
                foreach (var candidate in cached)
                {
                    var meta = candidate.LevelText.Length > 0 ? $"Lv{candidate.LevelText}" : "";
                    if (candidate.ZoneName.Length > 0)
                        meta = meta.Length > 0 ? $"{meta}, {candidate.ZoneName}" : candidate.ZoneName;
                    var label = meta.Length > 0 ? $"{candidate.Name} ({meta})" : candidate.Name;

                    if (candidate.NameId != 0)
                    {
                        if (ImGui.SmallButton($"+ {label}##drop{candidate.GarlandId}"))
                        {
                            profile.AddMob(candidate.NameId, candidate.Name);
                            // Auto-locate: flag the mob's spawn on the map, and add a farm spot
                            // when it's in the current zone (resolved async from its Garland doc).
                            if (candidate.GarlandId != 0)
                            {
                                _dropSource.BeginMobLocationLookup(candidate.GarlandId);
                                _pendingLocations[candidate.GarlandId] = candidate.Name;
                            }
                        }
                    }
                    else
                    {
                        ImGui.TextColored(Styling.TextDim, $"   {label} (couldn't match to a game mob)");
                    }
                }
            }
        }

        ProcessPendingLocations(profile);
        if (_locationStatus.Length > 0)
            ImGui.TextColored(Styling.TextDim, _locationStatus);

        if (profile.Mobs.Count > 1 && ImGui.SmallButton("clear all##orionMobs"))
            profile.ClearMobs();

        for (var i = 0; i < profile.Mobs.Count; i++)
        {
            var mob = profile.Mobs[i];
            ImGui.Text($"• {mob.Name}");
            ImGui.SameLine();
            ImGui.TextColored(Styling.TextDim, $"(NameId {mob.NameId})");
            ImGui.SameLine();
            if (ImGui.SmallButton($"remove##mob{i}"))
            {
                profile.RemoveMobAt(i);
                break;
            }
        }
    }

    private void DrawSpots(FarmProfile profile)
    {
        ImGui.TextColored(Styling.TextDim, "Orion roams between the spots; the first is the anchor.");
        if (ImGui.Button("Add spot (my position)"))
        {
            var player = _objectTable.LocalPlayer;
            if (player != null)
            {
                profile.Spots.Add(player.Position);
                profile.TerritoryId = (ushort)_clientState.TerritoryType;
            }
        }
        if (profile.Spots.Count > 1)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("clear all##orionSpots"))
                profile.Spots.Clear();
        }

        for (var i = 0; i < profile.Spots.Count; i++)
        {
            var spot = profile.Spots[i];
            ImGui.Text($"• spot {i + 1}: {spot.X:F0}, {spot.Y:F0}, {spot.Z:F0}");
            ImGui.SameLine();
            if (ImGui.SmallButton($"remove##spot{i}"))
            {
                profile.Spots.RemoveAt(i);
                break;
            }
        }

        var leash = profile.LeashRadiusYalms;
        ImGui.SetNextItemWidth(180);
        if (ImGui.SliderFloat("leash (yalms)", ref leash, 20f, 100f, "%.0f"))
            profile.LeashRadiusYalms = leash;
    }

    /// <summary>
    /// Completes async mob-location lookups on the draw thread: sets the in-game map flag at the
    /// mob's spawn (any zone) and adds a farm spot when the mob lives in the current zone.
    /// The spot's height is estimated from the player (the navmesh snaps to the floor when pathing).
    /// </summary>
    private void ProcessPendingLocations(FarmProfile profile)
    {
        if (_pendingLocations.Count == 0)
            return;

        ulong? completed = null;
        foreach (var (garlandId, mobName) in _pendingLocations)
        {
            if (!_dropSource.TryGetMobLocation(garlandId, out var location))
                continue;
            completed = garlandId;

            if (location == null)
            {
                _locationStatus = $"{mobName}: no location data on GarlandTools.";
                break;
            }

            var zone = FarmLocationHelper.ResolveZoneByName(_dataManager, location.ZoneName);
            if (zone == null)
            {
                _locationStatus = $"{mobName}: couldn't resolve zone \"{location.ZoneName}\".";
                break;
            }

            var flagged = FarmLocationHelper.SetMapFlag(zone.Value, location.MapX, location.MapY);

            if (zone.Value.TerritoryId == _clientState.TerritoryType)
            {
                var player = _objectTable.LocalPlayer;
                if (player != null)
                {
                    var worldX = FarmLocationHelper.MapCoordToWorld(location.MapX, zone.Value.SizeFactor, zone.Value.OffsetX);
                    var worldZ = FarmLocationHelper.MapCoordToWorld(location.MapY, zone.Value.SizeFactor, zone.Value.OffsetY);
                    profile.Spots.Add(new Vector3(worldX, player.Position.Y, worldZ));
                    profile.TerritoryId = (ushort)_clientState.TerritoryType;
                    _locationStatus = $"{mobName}: spot added at ({location.MapX:F1}, {location.MapY:F1}){(flagged ? " + map flag set" : "")}.";
                }
            }
            else
            {
                _locationStatus = flagged
                    ? $"{mobName} lives in {zone.Value.Name} — map flag set (farm spots must be in your current zone)."
                    : $"{mobName} lives in {zone.Value.Name} ({location.MapX:F1}, {location.MapY:F1}).";
            }
            break;
        }

        if (completed.HasValue)
            _pendingLocations.Remove(completed.Value);
    }

    private void RebuildItemResults()
    {
        _itemResults.Clear();
        var sheet = _dataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>();
        if (sheet == null)
            return;

        foreach (var row in sheet)
        {
            var name = row.Name.ExtractText();
            if (name.Length == 0)
                continue;
            if (!name.Contains(_itemSearch, StringComparison.OrdinalIgnoreCase))
                continue;

            _itemResults.Add((row.RowId, name));
            if (_itemResults.Count >= 25)
                break;
        }
    }
}
