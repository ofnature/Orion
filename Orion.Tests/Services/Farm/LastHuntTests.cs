using System.Numerics;
using Orion.Config;
using Orion.Services.Farm;
using Xunit;

namespace Orion.Tests.Services.Farm;

public class LastHuntTests
{
    [Fact]
    public void ASavedHunt_ComesBackWithItsItemCountAndMobs_ButNoSpots()
    {
        var hunted = new FarmProfile { ItemId = 5001, ItemName = "Wyvern Skin", TargetCount = 12, LeashRadiusYalms = 80f };
        hunted.AddMob(3576, "Wyvern");
        hunted.AddMob(3577, "Elder Wyvern");
        hunted.Spots.Add(new Vector3(1f, 2f, 3f)); // belongs to the zone it was marked in

        var restored = new FarmProfile();
        restored.Spots.Add(new Vector3(9f, 9f, 9f));
        LastHunt.Restore(LastHunt.Snapshot(hunted), restored);

        Assert.Equal(5001u, restored.ItemId);
        Assert.Equal("Wyvern Skin", restored.ItemName);
        Assert.Equal(12, restored.TargetCount);
        Assert.Equal(80f, restored.LeashRadiusYalms);
        Assert.Equal([new FarmMob(3576, "Wyvern"), new FarmMob(3577, "Elder Wyvern")], restored.Mobs);
        Assert.Empty(restored.Spots);
    }

    [Fact]
    public void NothingSaved_LeavesTheProfileAlone()
    {
        var profile = new FarmProfile { ItemId = 7, ItemName = "kept" };

        LastHunt.Restore(null, profile);
        LastHunt.Restore(new SavedFarmProfile(), profile);

        Assert.Equal(7u, profile.ItemId);
        Assert.Equal("kept", profile.ItemName);
    }

    [Fact]
    public void TrophyList_Add_AnItemAlreadyThere_TakesTheNewCount_AndKeepsItsPlace()
    {
        var list = new TrophyList();
        list.Add(1, "hide", 5);
        list.Add(2, "fang", 3);
        list.Add(1, "hide", 20);
        list.Add(3, "wing", 0); // a count below one is one

        Assert.Equal([1u, 2u, 3u], list.Items.Select(i => i.ItemId));
        Assert.Equal([20, 3, 1], list.Items.Select(i => i.TargetCount));
    }

    [Fact]
    public void NamedMobsFor_CapitalisesSheetNames_AndLeavesOutTheNameless()
    {
        var path = Path.Combine(Path.GetTempPath(), $"orion-drops-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{ "5001": [10, 11, 12] }""");
        try
        {
            var drops = new DropSources(path, _ => { });

            var mobs = drops.NamedMobsFor(5001, id => id switch { 10 => "ground squirrel", 11 => "", _ => "Odin" });

            Assert.Equal([new FarmMob(10, "Ground squirrel"), new FarmMob(12, "Odin")], mobs);
            Assert.Empty(drops.NamedMobsFor(9999, _ => "anything"));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
