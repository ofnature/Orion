using Orion.Services.HuntLog;

namespace Orion.Tests.Services.HuntLog;

/// <summary>
/// Row numbering checked against the MonsterNote sheet of game version 2026.11.03, where these
/// rows are "Gladiator 01", "Archer 11", "Maelstrom 01" and "Immortal Flames 21".
/// </summary>
public sealed class HuntLogIdsTests
{
    [Theory]
    [InlineData(1u, 0, 0, 10001u)]   // Gladiator 01
    [InlineData(5u, 1, 0, 50011u)]   // Archer 11
    [InlineData(29u, 4, 9, 290050u)] // Rogue's last entry
    public void ClassRow_FollowsTheSheet(uint classJobId, int rank, int entry, uint expected)
        => Assert.Equal(expected, HuntLogIds.ClassRow(classJobId, rank, entry));

    [Theory]
    [InlineData(1u, 0, 0, 1000001u)] // Maelstrom 01
    [InlineData(3u, 2, 0, 3000021u)] // Immortal Flames 21
    public void GrandCompanyRow_FollowsTheSheet(uint grandCompanyId, int rank, int entry, uint expected)
        => Assert.Equal(expected, HuntLogIds.GrandCompanyRow(grandCompanyId, rank, entry));

    [Fact]
    public void GrandCompanyLogs_FillTheGapTheClassLogsLeave()
    {
        // Class logs sit at 0 to 7 and Rogue at 11 (ClassJob.MonsterNote).
        Assert.Equal(8, HuntLogIds.GrandCompanyLogIndex(1));
        Assert.Equal(10, HuntLogIds.GrandCompanyLogIndex(3));
    }
}
