using System.Collections.Generic;

namespace Orion.Services.Game;

/// <summary>Which class Trophy Hunt does its killing on.</summary>
public enum FightAs
{
    /// <summary>Whatever the character is when START is pressed.</summary>
    Current = 0,

    /// <summary>The highest-level ranged class with a gearset: bow, gun and dance, and the casters.</summary>
    Ranged = 1,

    /// <summary>The highest-level tank with a gearset.</summary>
    Tank = 2,
}

/// <summary>A saved gearset: its slot, its class, that class's role and the character's level in it.</summary>
public readonly record struct GearsetInfo(int Id, uint ClassJobId, string ClassName, int Role, int Level);

/// <summary>The gearsets and the class being played, and the switch between them.</summary>
public interface IFightClassWorld
{
    uint CurrentClassJob { get; }

    /// <summary>The saved gearsets of classes the character has levelled.</summary>
    IReadOnlyList<GearsetInfo> Gearsets();

    /// <summary>Equip a gearset by its slot. False when the game refused.</summary>
    bool EquipGearset(int id);
}

/// <summary>Pure choice of the gearset to fight on.</summary>
public static class FightClassPicker
{
    // ClassJob.Role: 1 tank, 2 melee, 3 ranged (physical and magical), 4 healer; 0 for crafters and gatherers.
    private const int TankRole = 1;
    private const int RangedRole = 3;

    /// <summary>
    /// The gearset to fight on: the highest-level one of the wanted role, the class already being
    /// played winning a tie (no switch for nothing), then the lowest slot. Null for
    /// <see cref="FightAs.Current"/>, and when no gearset of that role is saved.
    /// </summary>
    public static GearsetInfo? Pick(IReadOnlyList<GearsetInfo> gearsets, FightAs choice, uint currentClassJob)
    {
        var role = choice switch
        {
            FightAs.Ranged => RangedRole,
            FightAs.Tank => TankRole,
            _ => 0,
        };
        if (role == 0)
            return null;

        GearsetInfo? best = null;
        foreach (var set in gearsets)
        {
            if (set.Role != role)
                continue;
            if (best is not { } held
                || set.Level > held.Level
                || (set.Level == held.Level && set.ClassJobId == currentClassJob && held.ClassJobId != currentClassJob))
                best = set;
        }
        return best;
    }

    public static string Label(FightAs choice) => choice switch
    {
        FightAs.Ranged => "Highest ranged class",
        FightAs.Tank => "Highest tank",
        _ => "Current class",
    };

    /// <summary>The role in words, for "no gearset saved for a ... class".</summary>
    public static string RoleName(FightAs choice) => choice == FightAs.Tank ? "tank" : "ranged";
}
