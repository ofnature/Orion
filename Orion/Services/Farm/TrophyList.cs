using System;
using System.Collections.Generic;

namespace Orion.Services.Farm;

/// <summary>One line of a hunting list: an item and the bag count to hunt it up to.</summary>
public sealed class TrophyListItem
{
    public uint ItemId { get; set; }

    /// <summary>The item's name when it was added, so the list reads without the game's sheets.</summary>
    public string Name { get; set; } = "";

    /// <summary>A bag count, as Trophy Hunt's own target is: done when the bag holds this many.</summary>
    public int TargetCount { get; set; } = 1;
}

/// <summary>
/// A named, saved list of items to hunt (the shape of Demeter's gather lists). Several can exist;
/// a run takes one, or every enabled one, and hunts whatever the bag is short of, an item at a time.
/// </summary>
public sealed class TrophyList
{
    public string Name { get; set; } = "New list";
    public bool Enabled { get; set; } = true;
    public List<TrophyListItem> Items { get; set; } = [];

    /// <summary>
    /// Put an item on the list. One already there keeps its place and takes the new count:
    /// adding it again is how its target is changed from the Trophy Hunt panel.
    /// </summary>
    public void Add(uint itemId, string name, int targetCount)
    {
        targetCount = Math.Max(1, targetCount);
        if (Items.Find(i => i.ItemId == itemId) is { } held)
        {
            held.TargetCount = targetCount;
            return;
        }
        Items.Add(new TrophyListItem { ItemId = itemId, Name = name, TargetCount = targetCount });
    }
}
