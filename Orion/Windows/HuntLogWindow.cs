using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Orion.Services.Farm;
using Orion.Services.HuntLog;
using Orion.Windows.Components;

namespace Orion.Windows;

/// <summary>
/// The hunt log's current rank: what it still wants, how many, and where the log says to look.
/// START runs the whole rank unattended; "Farm this" hands one target to the main window's farm,
/// which then ends when the target is filled.
/// </summary>
public sealed class HuntLogWindow : Window
{
    private readonly HuntLogCatalog _catalog;
    private readonly HuntLogProgress _progress;
    private readonly HuntPositions _positions;
    private readonly HuntLogRunner _runner;
    private readonly FarmModeService _farm;
    private readonly IObjectTable _objectTable;
    private readonly IClientState _clientState;
    private readonly Action _openMain;

    private HuntLogBook? _book;
    private int _shownRank = -1;
    private IReadOnlyList<HuntLogTarget> _targets = [];
    private string? _startError;

    public HuntLogWindow(
        HuntLogCatalog catalog,
        HuntLogProgress progress,
        HuntPositions positions,
        HuntLogRunner runner,
        FarmModeService farm,
        IObjectTable objectTable,
        IClientState clientState,
        Action openMain)
        : base("Orion — Hunt Log###OrionHuntLog")
    {
        _catalog = catalog;
        _progress = progress;
        _positions = positions;
        _runner = runner;
        _farm = farm;
        _objectTable = objectTable;
        _clientState = clientState;
        _openMain = openMain;

        Size = new Vector2(680, 560);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        using var style = Styling.PushWindowStyle();

        // Opens on the log of the class being played; a job without one (or none read yet) gets the first.
        _book ??= _catalog.BookForClassJob(_objectTable.LocalPlayer?.ClassJob.RowId ?? 0)
                  ?? (_catalog.Books.Count > 0 ? _catalog.Books[0] : null);
        if (_book is not { } book)
        {
            ImGui.TextColored(Styling.AccentRose, "The hunt log could not be read from the game's sheets.");
            return;
        }

        ImGui.SetNextItemWidth(220);
        if (ImGui.BeginCombo("##orionHuntBook", book.Name))
        {
            foreach (var candidate in _catalog.Books)
            {
                if (ImGui.Selectable($"{candidate.Name}##book{candidate.Index}", candidate.Index == book.Index))
                {
                    _book = book = candidate;
                    _shownRank = -1;
                    _startError = null;
                }
            }
            ImGui.EndCombo();
        }

        var rank = _progress.CurrentRank(book.Index);
        ImGui.SameLine();
        if (rank < 0)
        {
            ImGui.TextColored(Styling.TextDim, "progress not readable (logged in?)");
            return;
        }
        if (rank >= HuntLogIds.RanksPerLog)
        {
            ImGui.TextColored(Styling.AccentMint, "log complete");
            return;
        }
        ImGui.TextColored(Styling.TextSecondary, $"rank {rank + 1} of {HuntLogIds.RanksPerLog}");

        if (rank != _shownRank)
        {
            _shownRank = rank;
            _targets = _catalog.Targets(book, rank);
        }

        if (_targets.Count == 0)
        {
            ImGui.TextColored(Styling.TextDim, "This rank lists no mobs.");
            return;
        }

        Styling.VSpace(4f);
        if (!ImGui.BeginTable("##orionHuntTargets", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp))
            return;

        ImGui.TableSetupColumn("Mob", ImGuiTableColumnFlags.WidthStretch, 3f);
        ImGui.TableSetupColumn("Kills", ImGuiTableColumnFlags.WidthFixed, 60f);
        ImGui.TableSetupColumn("Where the log says", ImGuiTableColumnFlags.WidthStretch, 5f);
        ImGui.TableSetupColumn("##act", ImGuiTableColumnFlags.WidthFixed, 80f);
        ImGui.TableHeadersRow();

        var here = _clientState.TerritoryType;
        foreach (var target in _targets)
        {
            var kills = _progress.Kills(target);
            var done = kills >= target.Needed;

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextColored(done ? Styling.TextMuted : Styling.TextStrong, target.MobName);

            ImGui.TableNextColumn();
            ImGui.TextColored(done ? Styling.AccentMint : Styling.TextSecondary, $"{Math.Min(kills, target.Needed)} / {target.Needed}");

            ImGui.TableNextColumn();
            var first = true;
            foreach (var zone in target.Zones)
            {
                if (!first)
                    ImGui.SameLine(0, 0);
                var text = (first ? "" : ",  ") + (zone.AreaName.Length > 0 ? $"{zone.ZoneName} ({zone.AreaName})" : zone.ZoneName);
                ImGui.TextColored(zone.TerritoryId != 0 && zone.TerritoryId == here ? Styling.AccentMint : Styling.TextDim, text);
                first = false;
            }
            if (!done)
            {
                // What the unattended run makes of this target.
                if (_runner.SkipReason(target) is { } skipped)
                    ImGui.TextColored(Styling.AccentAmber, $"skipped: {skipped}");
                else if (_positions.For(target.NameId).Count == 0)
                    ImGui.TextColored(Styling.TextMuted, "no recorded position: the run will skip it");
            }

            ImGui.TableNextColumn();
            if (!done)
            {
                ImGui.BeginDisabled(_farm.IsRunning || _runner.IsRunning);
                if (ImGui.SmallButton($"Farm this##hunt{target.Entry}_{target.Slot}"))
                {
                    var profile = _farm.Profile;
                    profile.ClearMobs();
                    profile.AddMob(target.NameId, target.MobName);
                    profile.Hunt = target;
                    _openMain();
                }
                ImGui.EndDisabled();
            }
        }

        ImGui.EndTable();

        Styling.VSpace(6f);
        if (_runner.IsRunning)
        {
            ImGui.TextColored(Styling.TextSecondary, _runner.StatusLine);
            if (HeroButton.Draw(FontAwesomeIcon.Stop, "STOP", _runner.Book?.Name, Styling.AccentRose, true))
                _runner.Stop("stopped by user");
        }
        else
        {
            var blocked = _farm.IsRunning ? "a farm run is going" : null;
            if (HeroButton.Draw(FontAwesomeIcon.Play, "START", $"run {book.Name}, rank {rank + 1}", Styling.AccentViolet, blocked == null, blocked))
                _startError = _runner.Start(book);
            if (_startError != null)
                ImGui.TextColored(Styling.AccentRose, _startError);
        }

        Styling.VSpace(4f);
        ImGui.TextColored(Styling.TextDim, "START travels to each mob and kills what the rank still wants, one target after another.");
        ImGui.TextColored(Styling.TextDim, "\"Farm this\" is the manual way: stand by the mobs, press it, then mark a spot in the main window.");
    }
}
