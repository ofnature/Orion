namespace Orion.Services.HuntLog;

/// <summary>
/// How the game numbers the hunt log. Checked against the <c>MonsterNote</c> and <c>ClassJob</c>
/// sheets of game version 2026.11.03: 600 rows, fifty per log.
/// </summary>
public static class HuntLogIds
{
    public const int RanksPerLog = 5;
    public const int EntriesPerRank = 10;
    public const int TargetsPerEntry = 4;

    /// <summary>
    /// Logs the game keeps: the eight class logs at 0 to 7 (<c>ClassJob.MonsterNote</c>: Gladiator
    /// to Arcanist), Rogue at 11, and the three Grand Companies in the gap between.
    /// </summary>
    public const int LogCount = 12;

    /// <summary>The <c>MonsterNote</c> row of one entry of a class log. The class is the base class (Gladiator, never Paladin).</summary>
    public static uint ClassRow(uint classJobId, int rank, int entry)
        => classJobId * 10_000 + (uint)(rank * EntriesPerRank + entry + 1);

    /// <summary>The <c>MonsterNote</c> row of one entry of a Grand Company log.</summary>
    public static uint GrandCompanyRow(uint grandCompanyId, int rank, int entry)
        => grandCompanyId * 1_000_000 + (uint)(rank * EntriesPerRank + entry + 1);

    /// <summary>
    /// Where a Grand Company's log sits in the game's progress array.
    /// ASSUMED, not yet seen in game: the class logs leave exactly 8, 9 and 10 free, and the
    /// companies are numbered 1 to 3.
    /// </summary>
    public static int GrandCompanyLogIndex(uint grandCompanyId) => 7 + (int)grandCompanyId;
}
