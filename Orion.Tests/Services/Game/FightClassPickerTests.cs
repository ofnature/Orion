using Orion.Services.Game;
using Xunit;

namespace Orion.Tests.Services.Game;

public class FightClassPickerTests
{
    // ClassJob rows and roles as the game has them: role 1 tank, 2 melee, 3 ranged, 4 healer, 0 crafter.
    private static readonly GearsetInfo Paladin = new(0, 19, "paladin", 1, 70);
    private static readonly GearsetInfo Warrior = new(1, 21, "warrior", 1, 90);
    private static readonly GearsetInfo Bard = new(2, 23, "bard", 3, 80);
    private static readonly GearsetInfo BlackMage = new(3, 25, "black mage", 3, 100);
    private static readonly GearsetInfo Monk = new(4, 20, "monk", 2, 100);
    private static readonly GearsetInfo WhiteMage = new(5, 24, "white mage", 4, 100);
    private static readonly GearsetInfo Carpenter = new(6, 8, "carpenter", 0, 100);

    private static readonly GearsetInfo[] All = [Paladin, Warrior, Bard, BlackMage, Monk, WhiteMage, Carpenter];

    [Fact]
    public void Pick_Ranged_TakesTheHighestRangedClass_CastersIncluded()
    {
        Assert.Equal(BlackMage, FightClassPicker.Pick(All, FightAs.Ranged, currentClassJob: 8));
    }

    [Fact]
    public void Pick_Tank_TakesTheHighestTank()
    {
        Assert.Equal(Warrior, FightClassPicker.Pick(All, FightAs.Tank, currentClassJob: 8));
    }

    [Fact]
    public void Pick_Current_IsNoSwitch()
    {
        Assert.Null(FightClassPicker.Pick(All, FightAs.Current, currentClassJob: 8));
    }

    [Fact]
    public void Pick_IsNull_WhenNoGearsetOfThatRoleIsSaved()
    {
        Assert.Null(FightClassPicker.Pick([Monk, WhiteMage, Carpenter], FightAs.Tank, currentClassJob: 20));
    }

    [Fact]
    public void Pick_OnATie_KeepsTheClassBeingPlayed()
    {
        var darkKnight = new GearsetInfo(7, 32, "dark knight", 1, 90);

        // Dark knight and warrior are both 90: no switch away from the one already on.
        Assert.Equal(darkKnight, FightClassPicker.Pick([Warrior, darkKnight], FightAs.Tank, currentClassJob: 32));
        Assert.Equal(Warrior, FightClassPicker.Pick([Warrior, darkKnight], FightAs.Tank, currentClassJob: 21));
    }

    [Fact]
    public void Pick_OnATie_WithNeitherBeingPlayed_TakesTheLowestSlot()
    {
        var darkKnight = new GearsetInfo(7, 32, "dark knight", 1, 90);

        Assert.Equal(Warrior, FightClassPicker.Pick([Warrior, darkKnight], FightAs.Tank, currentClassJob: 8));
    }

    [Fact]
    public void Pick_AHigherLevelBeatsTheClassBeingPlayed()
    {
        // On bard (80), but black mage is 100: "highest" means the switch is made.
        Assert.Equal(BlackMage, FightClassPicker.Pick(All, FightAs.Ranged, currentClassJob: 23));
    }
}
