using Orion.Services.Game;

namespace Orion.Tests.Services.Game;

public sealed class ItemContextMenuTests
{
    private const uint Leather = 51;
    private const uint Arms = 10; // a weapon category: not something to hunt for

    [Fact]
    public void Offer_ForAnItemAMobIsKnownToDrop_WhateverItsCategory()
        => Assert.True(ItemContextMenu.Offer(knownDropper: true, searchCategory: Arms));

    [Fact]
    public void Offer_ForACraftingMaterial_EvenWithNoDropperKnown()
        => Assert.True(ItemContextMenu.Offer(knownDropper: false, searchCategory: Leather));

    [Theory]
    [InlineData(Arms)]
    [InlineData(0u)] // an item that cannot be sold on the market board
    public void Offer_NotForOtherItems(uint searchCategory)
        => Assert.False(ItemContextMenu.Offer(knownDropper: false, searchCategory));
}
