using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Orion.Config;
using Orion.Services.Farm;
using Orion.Services.Fleet;
using Orion.Services.Game;
using Orion.Services.HuntLog;
using Orion.Windows.Components;
using Orion.Windows.Sections;

namespace Orion.Windows;

/// <summary>
/// Orion's window, laid out the way Parcae's are: the header banner, a strip on the left to pick
/// the mode (and see what the other characters' Orions are doing), and the mode beside it.
/// Trophy Hunt is farm mode: a numbered idle flow ending in START, or the running dashboard with
/// STOP. Its working profile is session-only by design (Daedalus's docs/farm-mode.md); travel
/// settings live in the settings window. Hunt Log is <see cref="HuntLogPanel"/>.
/// </summary>
public sealed class MainWindow : Window
{
    private readonly FarmModeService _farm;
    private readonly HuntLogRunner _hunt;
    private readonly TrophyRunner _trophy;
    private readonly TrophyListRunner _listRun;
    private readonly HuntingListsPanel _lists;
    private readonly GarlandDropSource _dropSource;
    private readonly DropSources _drops;
    private readonly IDataManager _dataManager;
    private readonly ITargetManager _targetManager;
    private readonly IClientState _clientState;
    private readonly IObjectTable _objectTable;
    private readonly Func<bool> _navAvailable;
    private readonly HuntLogPanel _huntLog;
    private readonly FleetPublisher _fleet;
    private readonly IChocoboWorld _chocobo;
    private readonly OrionConfig _config;
    private readonly Action _saveConfig;
    private readonly Action _toggleSettings;

    /// <summary>A peer unheard for this long is drawn as stale (it publishes every two seconds).</summary>
    private static readonly TimeSpan FleetStaleAfter = TimeSpan.FromSeconds(10);

    private string _itemSearch = "";

    // What the result list was last built for; null forces a rebuild.
    private string? _lastItemSearch;

    // The dropped-items list with names, built once from the Item sheet and sorted by name.
    private List<(uint Id, string Name)>? _droppedItems;
    private readonly List<(uint Id, string Name)> _itemResults = new();
    private int _targetCountInput = 1;

    private string? _startError;

    // Where START would travel to when no spot is marked; asked once a second, not per frame.
    private TrophyLeg? _plan;
    private long? _planCheckedAt;

    // The gearset START would switch to; asked once a second, not per frame.
    private GearsetInfo? _fightClass;
    private long? _fightClassCheckedAt;
    private readonly Dictionary<ulong, string> _pendingLocations = new();
    private string _locationStatus = "";

    // "Is a navmesh plugin loaded" walks the installed-plugin list: asked once a second, not per frame.
    private bool _navOk;
    private long? _navCheckedAt;

    public MainWindow(
        FarmModeService farm,
        HuntLogRunner hunt,
        TrophyRunner trophy,
        TrophyListRunner listRun,
        HuntingListsPanel lists,
        GarlandDropSource dropSource,
        DropSources drops,
        IDataManager dataManager,
        ITargetManager targetManager,
        IClientState clientState,
        IObjectTable objectTable,
        Func<bool> navAvailable,
        HuntLogPanel huntLog,
        FleetPublisher fleet,
        IChocoboWorld chocobo,
        OrionConfig config,
        Action saveConfig,
        Action toggleSettings)
        : base("Orion — Hunt Operations###OrionMain")
    {
        _farm = farm;
        _hunt = hunt;
        _trophy = trophy;
        _listRun = listRun;
        _lists = lists;
        _dropSource = dropSource;
        _drops = drops;
        _dataManager = dataManager;
        _targetManager = targetManager;
        _clientState = clientState;
        _objectTable = objectTable;
        _navAvailable = navAvailable;
        _huntLog = huntLog;
        _fleet = fleet;
        _chocobo = chocobo;
        _config = config;
        _saveConfig = saveConfig;
        _toggleSettings = toggleSettings;

        Size = new Vector2(760, 720);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        using var style = Styling.PushWindowStyle();
        RefreshNav();

        var (status, detail, accent) = !_navOk
            ? ("SETUP", "Ariadne or vnavmesh required", Styling.AccentRose)
            : Busy
                ? ("HUNTING", RunStatus, Styling.AccentMint)
                : ("READY", "waiting for a quarry", Styling.AccentVioletSoft);

        OrionHeader.Draw(status, detail, accent,
        [
            new HeaderAction(FontAwesomeIcon.Cog, "settings", "Settings", _toggleSettings),
        ]);
        Styling.VSpace(7f);

        var sidebarWidth = 168f * ImGuiHelpers.GlobalScale;
        using (ImRaii.Child("##orion_sidebar", new Vector2(sidebarWidth, -1), border: false))
            DrawSidebar();

        ImGui.SameLine();

        using (ImRaii.Child("##orion_content", new Vector2(-1, -1), border: false))
        {
            if (_config.Mode == OrionMode.HuntLog)
                _huntLog.Draw();
            else if (Busy)
                DrawRunning();
            else
                DrawIdle();
        }
    }

    /// <summary>Open the window on a mode (the hunt log command, the dock panel, "Farm this").</summary>
    public void ShowMode(OrionMode mode)
    {
        SetMode(mode);
        IsOpen = true;
    }

    private void SetMode(OrionMode mode)
    {
        if (_config.Mode == mode)
            return;
        _config.Mode = mode;
        _saveConfig();
    }

    // ----- Left strip: modes, then the fleet -----

    private void DrawSidebar()
    {
        ImGui.Spacing();
        if (SidebarTab.Draw(ModeNames.HuntLog, FontAwesomeIcon.Book, Styling.AccentViolet, _config.Mode == OrionMode.HuntLog))
            SetMode(OrionMode.HuntLog);
        if (SidebarTab.Draw(ModeNames.TrophyHunt, FontAwesomeIcon.Crosshairs, Styling.AccentViolet, _config.Mode == OrionMode.TrophyHunt))
            SetMode(OrionMode.TrophyHunt);

        Styling.VSpace(12f);
        Styling.SectionLabel("Fleet");
        DrawFleet();

        Styling.VSpace(12f);
        Styling.SectionLabel("Chocobo");
        DrawChocobo();
    }

    /// <summary>The companion: whether it is out and for how long, the greens left, and the switch to keep it out.</summary>
    private void DrawChocobo()
    {
        var left = _chocobo.CompanionTimeLeft;
        if (left > 0f)
            ImGui.TextColored(Styling.AccentMint, $"Out, {Math.Ceiling(left / 60f):F0} min left");
        else
            ImGui.TextColored(Styling.TextDim, "Not out");

        ImGui.TextColored(GreensColor(out var greens), $"Gysahl Greens: {greens}");

        var keep = _config.KeepChocoboOut;
        if (ImGui.Checkbox("Keep it out", ref keep))
        {
            _config.KeepChocoboOut = keep;
            _saveConfig();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "While a run is going, feeds it a Gysahl Green whenever the summon has under five minutes left.\n"
                + "Only in the field, on foot and out of combat, and not until \"My Little Chocobo\" is done.");
        }
    }

    /// <summary>Greens in the bags, and the colour to say it in: rose when there are none, amber when nearly out.</summary>
    private System.Numerics.Vector4 GreensColor(out int greens)
    {
        greens = _chocobo.ItemCount(ChocoboKeeper.GysahlGreens);
        return greens == 0 ? Styling.AccentRose : greens <= 2 ? Styling.AccentAmber : Styling.TextSecondary;
    }

    /// <summary>What the other characters' Orions say they are doing, as heard over Daedalus's LAN bridge.</summary>
    private void DrawFleet()
    {
        var peers = _fleet.Roster.Peers(DateTime.UtcNow, FleetStaleAfter);
        if (peers.Count == 0)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, Styling.TextMuted))
                ImGui.TextWrapped("No other Orion heard on the LAN bridge.");
            return;
        }

        foreach (var peer in peers)
        {
            var status = peer.Status;
            var online = peer.Liveness == PeerLiveness.Online;
            var dot = !online ? Styling.TextMuted : status.Running ? Styling.AccentMint : Styling.TextDim;
            var line = !online ? "not heard lately"
                : !status.Running ? "idle"
                : status.Progress.Length > 0 ? $"{status.Goal} {status.Progress}"
                : status.Mode;

            ImGui.BeginGroup();
            ImGui.TextColored(dot, "●");
            ImGui.SameLine(0, 6f);
            ImGui.TextColored(online ? Styling.TextStrong : Styling.TextMuted, status.Character);
            ImGui.TextColored(Styling.TextDim, line);
            ImGui.EndGroup();

            if (ImGui.IsItemHovered())
            {
                var what = status.Running ? $"{status.Mode}: {status.Goal} {status.Progress}".TrimEnd() : "Idle";
                ImGui.SetTooltip(
                    $"{status.Character} @ {status.World}\n{status.Zone}\n{what}"
                    + (status.StatusLine.Length > 0 ? $"\n{status.StatusLine}" : "")
                    + $"\nheard {peer.Age.TotalSeconds:F0}s ago");
            }
            Styling.VSpace(2f);
        }
    }

    /// <summary>A farm run, or a hunt log run or hunting list (which are between farms while they travel).</summary>
    private bool Busy => _farm.IsRunning || _hunt.IsRunning || _trophy.IsRunning || _listRun.IsRunning;

    /// <summary>The status line of whichever run is driving the character.</summary>
    private string RunStatus
        => _trophy.IsRunning ? _trophy.StatusLine
            : _hunt.IsRunning && !_farm.IsRunning ? _hunt.StatusLine
            : _farm.StatusLine;

    /// <summary>
    /// Stop whatever is going. The hunt log run owns the farm while it is going, so it is the one
    /// to stop; Trophy Hunt's travel has no farm yet.
    /// </summary>
    private void StopRun()
    {
        if (_hunt.IsRunning)
            _hunt.Stop("stopped by user");
        else if (_listRun.IsRunning)
            _listRun.Stop("stopped by user");
        else if (_trophy.IsRunning)
            _trophy.Stop("stopped by user");
        else
            _farm.Stop("stopped by user");
    }

    /// <summary>
    /// START. An item run goes through Trophy Hunt's start: the class switch first, then the marked
    /// spots, or with none marked, the trip to the mobs. A hunt log target farmed here starts as it
    /// is: its kills only count on the class being played.
    /// </summary>
    private void PressStart(FarmProfile profile)
    {
        if (profile.Hunt != null)
        {
            _startError = _farm.Start();
            return;
        }

        _startError = _trophy.Start();
        if (_startError == null)
        {
            // Remembered for next time: the item, the count and the mobs come back after a reload.
            _config.Farm.LastHunt = LastHunt.Snapshot(profile);
            _saveConfig();
        }
    }

    /// <summary>Where START would travel to for the mobs listed now; null when it would not.</summary>
    private TrophyLeg? TravelPlan(FarmProfile profile)
    {
        if (profile.Hunt != null || profile.Mobs.Count == 0)
            return null;

        var now = Environment.TickCount64;
        if (_planCheckedAt is not { } last || now - last >= 1000)
        {
            _plan = _trophy.Plan();
            _planCheckedAt = now;
        }
        return _plan;
    }

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
            ShowMode(OrionMode.HuntLog);
        ImGui.SameLine();
        ImGui.TextColored(GreensColor(out var greens), $"Greens: {greens}");
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
            PressStart(profile);
        if (_startError != null)
            ImGui.TextColored(Styling.AccentRose, _startError);
    }

    // ----- Running dashboard -----

    private void DrawRunning()
    {
        var profile = _farm.Profile;

        // On the road (a hunt log run between targets, or Trophy Hunt on its way to the mobs):
        // farm mode has nothing to show until it gets there.
        if (!_farm.IsRunning)
        {
            Styling.SectionLabel(
                _listRun.IsRunning ? $"Hunting list, {_listRun.Progress}"
                : _trophy.IsRunning ? ModeNames.TrophyHunt
                : $"{_hunt.Book?.Name} hunt log");
            ImGui.TextColored(Styling.TextSecondary, RunStatus);
            Styling.VSpace(4f);
            if (StopButton())
                StopRun();
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
        if (_listRun.IsRunning)
            ImGui.TextColored(Styling.TextDim, $"Hunting list, {_listRun.Progress}");
        ImGui.TextColored(Styling.TextSecondary, _farm.StatusLine);
        Styling.VSpace(4f);

        if (StopButton())
            StopRun();
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
        if (profile.Hunt == null)
            DrawFightAs();
        var reason = BlockingReason(profile);
        if (HeroButton.Draw(FontAwesomeIcon.Play, "START", GoalSummary(profile), Styling.AccentViolet, reason == null, reason))
            PressStart(profile);

        if (_startError != null)
            ImGui.TextColored(Styling.AccentRose, _startError);

        Styling.VSpace(12f);
        _lists.Draw();
    }

    /// <summary>The class an item run fights on, and what that comes to on this character.</summary>
    private void DrawFightAs()
    {
        var farm = _config.Farm;
        ImGui.SetNextItemWidth(220);
        if (ImGui.BeginCombo("Fight as", FightClassPicker.Label(farm.FightAs)))
        {
            foreach (var choice in Enum.GetValues<FightAs>())
            {
                if (ImGui.Selectable(FightClassPicker.Label(choice), choice == farm.FightAs))
                {
                    farm.FightAs = choice;
                    _saveConfig();
                    _fightClassCheckedAt = null;
                    _planCheckedAt = null; // the zone is chosen for the class that fights
                }
            }
            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("START switches to this class's gearset before it sets off.\nRanged counts the casters as well as bow, gun and dance.");

        if (farm.FightAs == FightAs.Current)
            return;

        // Walking a hundred gearset slots: asked once a second, not per frame.
        var now = Environment.TickCount64;
        if (_fightClassCheckedAt is not { } last || now - last >= 1000)
        {
            _fightClass = _trophy.FightClass();
            _fightClassCheckedAt = now;
        }

        ImGui.SameLine();
        if (_fightClass is { } set)
            ImGui.TextColored(Styling.AccentMint, $"{set.ClassName}, level {set.Level}");
        else
            ImGui.TextColored(Styling.AccentRose, $"no {FightClassPicker.RoleName(farm.FightAs)} gearset saved");
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
        // With no spot marked, START travels to the mobs itself when it knows where one lives.
        if (profile.Spots.Count == 0 && TravelPlan(profile) == null)
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
            ImGui.SameLine();
            if (ImGui.SmallButton("add to list##orionItem"))
                _lists.Add(profile.ItemId, profile.ItemName, profile.TargetCount);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"Save this item and its target count on {_lists.TargetName} (under Hunting lists, below).");
        }

        var droppedOnly = _config.Farm.DroppedItemsOnly;
        if (ImGui.Checkbox("dropped items only", ref droppedOnly))
        {
            _config.Farm.DroppedItemsOnly = droppedOnly;
            _saveConfig();
            _lastItemSearch = null;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("List only items a mob is known to drop (FFXIV Teamcraft's list).\nThe list is not complete: untick to search every item.");

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##orionItemSearch",
            droppedOnly ? "search dropped items by name..." : "search item by name (3+ letters)...", ref _itemSearch, 64);

        // Every item needs three letters to narrow it down. The dropped list is short enough to
        // browse: it shows in full until an item is picked, and again while something is typed.
        var wantList = droppedOnly ? _itemSearch.Length > 0 || profile.ItemId == 0 : _itemSearch.Length >= 3;
        if (wantList && _itemSearch != _lastItemSearch)
        {
            _lastItemSearch = _itemSearch;
            RebuildItemResults(droppedOnly);
        }
        if (wantList && _itemResults.Count > 0)
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
                        _lastItemSearch = null;
                        FillDroppers(profile, id);
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
            SetMode(OrionMode.HuntLog);
    }

    private void DrawMobs(FarmProfile profile)
    {
        ImGui.TextColored(Styling.TextDim, "Saved when you press START, and brought back next time.");
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
        if (profile.Spots.Count == 0)
        {
            if (TravelPlan(profile) is { } plan)
            {
                var level = plan.Zone.Level > 0 ? $", level {plan.Zone.Level}" : "";
                var zone = Orion.Services.Travel.Zones.Name?.Invoke(plan.Zone.TerritoryId) ?? $"territory {plan.Zone.TerritoryId}";
                ImGui.TextColored(Styling.AccentMint, $"None marked: START will travel to {zone} for {plan.Mob.Name}{level}");
                ImGui.TextColored(Styling.AccentMint, "and mark the spots itself. Mark one here to farm where you stand instead.");
            }
            else if (profile.Hunt == null && profile.Mobs.Count > 0)
            {
                ImGui.TextColored(Styling.TextDim, "No recorded position for these mobs in a zone you can reach: go to them and mark a spot.");
            }

            // Shown whenever no spot is marked, quarry picked or not: it only appeared once an item was
            // chosen, so it could not be found to set beforehand (2026-11-21).
            var search = _config.Farm.SearchRadiusYalms;
            ImGui.SetNextItemWidth(180);
            if (ImGui.SliderFloat("search area (yalms)", ref search, 0f, SearchPoints.MaxRadiusYalms, "%.0f"))
                _config.Farm.SearchRadiusYalms = search;
            if (ImGui.IsItemDeactivatedAfterEdit())
                _saveConfig();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("With no spot marked, Orion travels to where the mob was reported and patrols this far around each place.\nRaise it for mobs spread over a large area; 0 stands on the reported places alone.\nMarking a spot yourself turns this off: the leash is used instead.\nHunt log runs roam the same area.");
            ImGui.SameLine();
            var rings = SearchPoints.Rings(search);
            ImGui.TextColored(Styling.TextDim, rings == 0 ? "reported places only" : $"{rings} ring(s) of search points");
        }
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

    /// <summary>
    /// A newly picked item brings its prey with it: the mob list becomes the item's known droppers
    /// (the bundled Teamcraft list, so no lookup goes out). An item the list does not know leaves
    /// the mob list as it was. Farm mode only ever fights the ones near a marked spot, so droppers
    /// that live in other zones are harmless in the list.
    /// </summary>
    private void FillDroppers(FarmProfile profile, uint itemId)
    {
        var names = _dataManager.GetExcelSheet<Lumina.Excel.Sheets.BNpcName>();
        var droppers = _drops.NamedMobsFor(itemId, nameId => names?.GetRowOrDefault(nameId)?.Singular.ExtractText());
        if (droppers.Count == 0)
            return;

        // New prey: spots marked for the last item's mobs no longer mean anything.
        profile.ClearMobs();
        profile.Spots.Clear();
        _planCheckedAt = null;
        foreach (var mob in droppers)
            profile.AddMob(mob.NameId, mob.Name);
        _locationStatus = $"{profile.Mobs.Count} dropper(s) listed for {profile.ItemName}. Remove any you do not want.";
    }

    private void RebuildItemResults(bool droppedOnly)
    {
        _itemResults.Clear();
        var sheet = _dataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>();
        if (sheet == null)
            return;

        if (droppedOnly)
        {
            if (_droppedItems == null)
            {
                _droppedItems = new List<(uint, string)>();
                foreach (var id in _drops.ItemIds)
                {
                    var name = sheet.GetRowOrDefault(id)?.Name.ExtractText();
                    if (!string.IsNullOrEmpty(name))
                        _droppedItems.Add((id, name));
                }
                _droppedItems.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            }

            foreach (var item in _droppedItems)
            {
                if (_itemSearch.Length == 0 || item.Name.Contains(_itemSearch, StringComparison.OrdinalIgnoreCase))
                    _itemResults.Add(item);
            }
            return;
        }

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
