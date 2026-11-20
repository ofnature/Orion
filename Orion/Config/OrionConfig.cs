using Dalamud.Configuration;

namespace Orion.Config;

/// <summary>What the main window is set to do, chosen in its left-hand strip.</summary>
public enum OrionMode
{
    /// <summary>Farm named mobs around placed spots until the bag holds a count of an item.</summary>
    TrophyHunt = 0,

    /// <summary>Work through the hunt log.</summary>
    HuntLog = 1,
}

/// <summary>What the modes are called, in the window and in the status sent to the fleet.</summary>
public static class ModeNames
{
    public const string TrophyHunt = "Trophy Hunt";
    public const string HuntLog = "Hunt Log";
}

public sealed class OrionConfig : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>The mode the main window opens on.</summary>
    public OrionMode Mode { get; set; } = OrionMode.TrophyHunt;

    /// <summary>
    /// Tell the other characters' Orions what this one is doing, over Daedalus's LAN bridge.
    /// What the others send is shown either way.
    /// </summary>
    public bool ShareFleetStatus { get; set; } = true;

    public FarmConfig Farm { get; set; } = new();

    /// <summary>
    /// Keep the chocobo companion summoned while a run is going, feeding it a Gysahl Green whenever
    /// the timer runs low. Does nothing until "My Little Chocobo" is done, and never spends a green
    /// in a city or a duty, where the companion is not allowed anyway.
    /// </summary>
    public bool KeepChocoboOut { get; set; }

    /// <summary>Show Orion's tab on the screen-edge dock shared with the rest of the suite.</summary>
    public bool EdgeDockEnabled { get; set; } = true;

    /// <summary>The edge the tab sits on, and where along it (0..1). Dragging the tab changes both.</summary>
    public Windows.Dock.DockEdge EdgeDockEdge { get; set; } = Windows.Dock.DockEdge.Right;
    public float EdgeDockAlong { get; set; } = 0.5f;
}
