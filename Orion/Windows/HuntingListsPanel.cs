using System;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Orion.Config;
using Orion.Services.Farm;

namespace Orion.Windows;

/// <summary>
/// The saved hunting lists, under Trophy Hunt's idle flow: several named lists, each item with a
/// bag-count target, run an item at a time through Trophy Hunt. Laid out the way Demeter's gather
/// lists are. Items come from the Trophy Hunt panel's "add to list".
/// </summary>
public sealed class HuntingListsPanel
{
    private readonly OrionConfig _config;
    private readonly Action _save;
    private readonly TrophyListRunner _runner;
    private readonly Func<uint, int> _held;
    private readonly Func<string?> _blocked;

    private int _index;
    private string? _error;

    /// <param name="held">How many of an item the bags hold.</param>
    /// <param name="blocked">Why a run cannot start right now, or null when it can.</param>
    public HuntingListsPanel(
        OrionConfig config, Action save, TrophyListRunner runner, Func<uint, int> held, Func<string?> blocked)
    {
        _config = config;
        _save = save;
        _runner = runner;
        _held = held;
        _blocked = blocked;
    }

    /// <summary>The list on screen, which "add to list" adds to; null when there is none yet.</summary>
    private TrophyList? Shown
    {
        get
        {
            var lists = _config.HuntingLists;
            if (lists.Count == 0)
                return null;
            _index = Math.Clamp(_index, 0, lists.Count - 1);
            return lists[_index];
        }
    }

    /// <summary>The name of the list "add to list" would add to, for the button's tooltip.</summary>
    public string TargetName => Shown?.Name ?? "a new list";

    /// <summary>Put an item on the list on screen, making the first list when there is none.</summary>
    public void Add(uint itemId, string name, int targetCount)
    {
        if (itemId == 0)
            return;
        var list = Shown;
        if (list == null)
        {
            list = new TrophyList();
            _config.HuntingLists.Add(list);
            _index = 0;
        }
        list.Add(itemId, name, targetCount);
        _save();
    }

    public void Draw()
    {
        Styling.SectionLabel("Hunting lists");
        var lists = _config.HuntingLists;
        var list = Shown;
        if (list == null)
        {
            ImGui.TextColored(Styling.TextDim, "No lists yet. Pick an item above and press \"add to list\", or");
            ImGui.SameLine();
            if (ImGui.SmallButton("+ New list"))
            {
                lists.Add(new TrophyList());
                _save();
            }
            return;
        }

        ImGui.SetNextItemWidth(180f);
        if (ImGui.BeginCombo("##orionHuntList", list.Name))
        {
            for (var i = 0; i < lists.Count; i++)
            {
                if (ImGui.Selectable($"{lists[i].Name}##hl{i}", i == _index))
                    _index = i;
            }
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("+ New##orionHuntList"))
        {
            lists.Add(new TrophyList());
            _index = lists.Count - 1;
            _save();
            return;
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("Delete##orionHuntList"))
        {
            lists.RemoveAt(_index);
            _save();
            return;
        }

        var name = list.Name;
        ImGui.SetNextItemWidth(180f);
        if (ImGui.InputText("##orionHuntListName", ref name, 48))
            list.Name = name;
        if (ImGui.IsItemDeactivatedAfterEdit())
            _save();
        ImGui.SameLine();
        var enabled = list.Enabled;
        if (ImGui.Checkbox("Enabled##orionHuntList", ref enabled))
        {
            list.Enabled = enabled;
            _save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("\"All enabled\" hunts every list with this ticked.");

        DrawItems(list);

        var blocked = _blocked();
        var canRunShown = blocked == null && list.Items.Count > 0;
        ImGui.BeginDisabled(!canRunShown);
        if (ImGui.Button("Hunt this list"))
            _error = _runner.Begin([new TrophyList { Name = list.Name, Enabled = true, Items = list.Items }]);
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(blocked ?? (list.Items.Count == 0 ? "Nothing on this list." : $"Hunt everything short on \"{list.Name}\", an item at a time."));

        ImGui.SameLine();
        var canRunAll = blocked == null && lists.Any(l => l.Enabled && l.Items.Count > 0);
        ImGui.BeginDisabled(!canRunAll);
        if (ImGui.Button("All enabled"))
            _error = _runner.Begin(lists);
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(blocked ?? "Hunt everything short on every list with Enabled ticked.");

        if (_error != null)
            ImGui.TextColored(Styling.AccentRose, _error);
    }

    private void DrawItems(TrophyList list)
    {
        if (list.Items.Count == 0)
        {
            ImGui.TextColored(Styling.TextDim, "Empty. Pick an item above and press \"add to list\".");
            return;
        }

        if (!ImGui.BeginTable("##orionHuntListItems", 3, ImGuiTableFlags.SizingStretchProp))
            return;

        ImGui.TableSetupColumn("item", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("count", ImGuiTableColumnFlags.WidthFixed, 130f);
        ImGui.TableSetupColumn("rm", ImGuiTableColumnFlags.WidthFixed, 64f);
        for (var i = 0; i < list.Items.Count; i++)
        {
            var item = list.Items[i];
            ImGui.PushID(i);
            var held = _held(item.ItemId);
            var done = held >= item.TargetCount;

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextColored(done ? Styling.AccentMint : Styling.TextDim, "●");
            ImGui.SameLine(0f, 6f);
            ImGui.TextColored(done ? Styling.TextDim : Styling.TextStrong, item.Name);

            ImGui.TableNextColumn();
            ImGui.TextColored(done ? Styling.AccentMint : Styling.TextSecondary, $"{held} /");
            ImGui.SameLine(0f, 4f);
            var target = item.TargetCount;
            ImGui.SetNextItemWidth(70f);
            if (ImGui.InputInt("##target", ref target, 0))
                item.TargetCount = Math.Max(1, target);
            if (ImGui.IsItemDeactivatedAfterEdit())
                _save();

            ImGui.TableNextColumn();
            var removed = ImGui.SmallButton("remove");
            ImGui.PopID();
            if (removed)
            {
                list.Items.RemoveAt(i);
                _save();
                break;
            }
        }
        ImGui.EndTable();
    }
}
