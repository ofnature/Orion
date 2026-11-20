using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Orion.Config;
using Orion.Services.Farm;

namespace Orion.Windows;

/// <summary>
/// Travel settings (persisted, unlike the session-only farm profile): mount mode, the specific
/// mount picker (unlocked mounts only), the fly toggle and the two tuning sliders.
/// </summary>
public sealed class SettingsWindow : Window
{
    private readonly OrionConfig _configuration;
    private readonly Action _saveConfiguration;
    private readonly IFarmMountHelper _mountHelper;

    // Unlocked-mount picker cache: enumerated once per session (unlocks mid-session are rare;
    // the refresh button covers them).
    private List<(uint Id, string Name)>? _unlockedMounts;

    public SettingsWindow(OrionConfig configuration, Action saveConfiguration, IFarmMountHelper mountHelper)
        : base("Orion — Settings###OrionSettings")
    {
        _configuration = configuration;
        _saveConfiguration = saveConfiguration;
        _mountHelper = mountHelper;

        Size = new Vector2(420, 420);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        using var style = Styling.PushWindowStyle();

        var farm = _configuration.Farm;
        var changed = false;

        Styling.SectionLabel("Travel");
        ImGui.TextColored(Styling.TextDim, "Mounts up for long legs and flies where the zone allows it.");

        var roulette = farm.MountMode == FarmMountMode.Roulette;
        if (ImGui.RadioButton("Mount Roulette", roulette))
        {
            farm.MountMode = FarmMountMode.Roulette;
            changed = true;
        }
        ImGui.SameLine();
        if (ImGui.RadioButton("Specific mount", !roulette))
        {
            farm.MountMode = FarmMountMode.Specific;
            changed = true;
        }

        if (farm.MountMode == FarmMountMode.Specific)
        {
            _unlockedMounts ??= new List<(uint, string)>(_mountHelper.GetUnlockedMounts());

            var currentName = "pick a mount...";
            foreach (var (id, name) in _unlockedMounts)
            {
                if (id == farm.SpecificMountId)
                {
                    currentName = name;
                    break;
                }
            }

            ImGui.SetNextItemWidth(200);
            if (ImGui.BeginCombo("##orionMountPick", currentName))
            {
                foreach (var (id, name) in _unlockedMounts)
                {
                    if (ImGui.Selectable($"{name}##mount{id}", id == farm.SpecificMountId))
                    {
                        farm.SpecificMountId = id;
                        changed = true;
                    }
                }
                ImGui.EndCombo();
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("refresh##mounts"))
                _unlockedMounts = null;

            if (_unlockedMounts is { Count: 0 })
                ImGui.TextColored(Styling.TextDim, "No mounts unlocked on this character — Roulette will be used.");
            else if (farm.SpecificMountId != 0 && !_mountHelper.IsMountUnlocked(farm.SpecificMountId))
                ImGui.TextColored(Styling.AccentRose, "Selected mount not unlocked here — falls back to Roulette.");
        }

        var flyWhenPossible = farm.FlyWhenPossible;
        if (ImGui.Checkbox("Fly when possible", ref flyWhenPossible))
        {
            farm.FlyWhenPossible = flyWhenPossible;
            changed = true;
        }

        var mountThreshold = farm.MountDistanceThresholdYalms;
        ImGui.SetNextItemWidth(180);
        if (ImGui.SliderFloat("mount when farther than (yalms)", ref mountThreshold, 20f, 120f, "%.0f"))
        {
            farm.MountDistanceThresholdYalms = mountThreshold;
            changed = true;
        }

        var scanRadius = farm.ScanRadiusYalms;
        ImGui.SetNextItemWidth(180);
        if (ImGui.SliderFloat("mob scan radius (yalms)", ref scanRadius, 10f, 100f, "%.0f"))
        {
            farm.ScanRadiusYalms = scanRadius;
            changed = true;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("How far around the character to look for the listed mobs.\nMobs must still be inside the spot leash.");

        Styling.VSpace(6f);
        Styling.SectionLabel("Edge tab");
        var edgeDock = _configuration.EdgeDockEnabled;
        if (ImGui.Checkbox("Show Orion's tab on the screen edge", ref edgeDock))
        {
            _configuration.EdgeDockEnabled = edgeDock;
            changed = true;
        }
        ImGui.TextColored(Styling.TextDim, "Point at the tab for the panel, click to pin it, drag it to move it.");

        Styling.VSpace(6f);
        Styling.SectionLabel("Fleet");
        var share = _configuration.ShareFleetStatus;
        if (ImGui.Checkbox("Tell the other characters what this one is doing", ref share))
        {
            _configuration.ShareFleetStatus = share;
            changed = true;
        }
        ImGui.TextColored(Styling.TextDim, "Sent over Daedalus's LAN bridge. What the others send is shown either way.");

        if (changed)
            _saveConfiguration();
    }
}
