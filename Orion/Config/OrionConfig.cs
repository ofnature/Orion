using Dalamud.Configuration;

namespace Orion.Config;

public sealed class OrionConfig : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public FarmConfig Farm { get; set; } = new();

    /// <summary>Show Orion's tab on the screen-edge dock shared with the rest of the suite.</summary>
    public bool EdgeDockEnabled { get; set; } = true;

    /// <summary>The edge the tab sits on, and where along it (0..1). Dragging the tab changes both.</summary>
    public Windows.Dock.DockEdge EdgeDockEdge { get; set; } = Windows.Dock.DockEdge.Right;
    public float EdgeDockAlong { get; set; } = 0.5f;
}
