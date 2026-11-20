using Orion.Services.Farm;

namespace Orion.Tests.Services.Farm;

/// <summary>
/// When a lost target counts as a kill. The counter once went up on every lost target, so it
/// climbed with no mob in reach at all (2026-11-19).
/// </summary>
public sealed class FarmKillCountTests
{
    [Fact]
    public void ADeadMobStillInTheTable_IsAKill_EvenIfNeverSeenFighting()
    {
        // The one-shot: targeted on one poll, dead before the next.
        Assert.True(FarmRoamPolicy.CountsAsKill(stillInTable: true, dead: true, fought: false));
    }

    [Fact]
    public void ALiveMobWhoseTargetWasLost_IsNotAKill()
    {
        // The target never took, or something else changed it, or it walked out of reach.
        Assert.False(FarmRoamPolicy.CountsAsKill(stillInTable: true, dead: false, fought: false));
        Assert.False(FarmRoamPolicy.CountsAsKill(stillInTable: true, dead: false, fought: true));
    }

    [Fact]
    public void AMobGoneFromTheTable_IsAKillOnlyIfItWasFought()
    {
        Assert.True(FarmRoamPolicy.CountsAsKill(stillInTable: false, dead: false, fought: true));
        Assert.False(FarmRoamPolicy.CountsAsKill(stillInTable: false, dead: false, fought: false));
    }
}
