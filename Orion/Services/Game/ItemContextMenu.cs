using System;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Plugin.Services;

namespace Orion.Services.Game;

/// <summary>
/// "Hunt with Orion" on the right-click menu of an item in the bags, as Monster Loot Hunter adds
/// its "Loot search" (danielbrenom/MonsterLootHunter, MIT). Only the inventory's menu: the item
/// there comes from Dalamud, where the recipe and market board menus would have to be read out of
/// the game's memory at fixed offsets.
/// </summary>
public sealed class ItemContextMenu : IDisposable
{
    /// <summary>
    /// The market board's categories for what mobs drop: ingredients, stone, metal, cloth,
    /// leather, bone and reagents (<c>ItemSearchCategory</c> rows; Monster Loot Hunter's list).
    /// </summary>
    private static readonly uint[] MaterialCategories = [44, 47, 48, 50, 51, 52, 53];

    private readonly IContextMenu _menu;
    private readonly Func<uint, bool> _knownDropper;
    private readonly Func<uint, uint> _searchCategory;
    private readonly Action<uint> _hunt;

    /// <param name="knownDropper">Whether a mob is known to drop an item.</param>
    /// <param name="searchCategory">An item's <c>ItemSearchCategory</c> row, 0 for none.</param>
    /// <param name="hunt">Sets the item up as Trophy Hunt's quarry.</param>
    public ItemContextMenu(IContextMenu menu, Func<uint, bool> knownDropper, Func<uint, uint> searchCategory, Action<uint> hunt)
    {
        _menu = menu;
        _knownDropper = knownDropper;
        _searchCategory = searchCategory;
        _hunt = hunt;
        _menu.OnMenuOpened += OnMenuOpened;
    }

    /// <summary>
    /// The entry is for things mobs drop, not every item in the bags: one a mob is known to drop,
    /// or a crafting material, which is what the drop lists are missing entries for.
    /// </summary>
    internal static bool Offer(bool knownDropper, uint searchCategory)
        => knownDropper || Array.IndexOf(MaterialCategories, searchCategory) >= 0;

    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        if (args.Target is not MenuTargetInventory { TargetItem: { } item })
            return;

        // The base id: a high-quality or collectable copy is still the same item to hunt.
        var itemId = item.BaseItemId;
        if (itemId == 0 || !Offer(_knownDropper(itemId), _searchCategory(itemId)))
            return;

        args.AddMenuItem(new MenuItem
        {
            Name = "Hunt with Orion",
            PrefixChar = 'O',
            OnClicked = _ => _hunt(itemId),
        });
    }

    public void Dispose() => _menu.OnMenuOpened -= OnMenuOpened;
}
