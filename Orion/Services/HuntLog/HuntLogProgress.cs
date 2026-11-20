using System;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace Orion.Services.HuntLog;

/// <summary>
/// Where the character stands in the hunt log, read from the game's <c>MonsterNoteManager</c>.
/// The game keeps kill counts for the current rank of each log only: earlier ranks are done and
/// later ones are locked. Every read fails to "nothing known" rather than throwing.
/// </summary>
public sealed unsafe class HuntLogProgress
{
    /// <summary>The rank a log is on (0 to 4), 5 once the log is finished, or -1 when it cannot be read.</summary>
    public int CurrentRank(int logIndex)
    {
        try
        {
            var manager = MonsterNoteManager.Instance();
            if (manager == null || logIndex < 0 || logIndex >= manager->RankData.Length)
                return -1;
            return manager->RankData[logIndex].Rank;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>Kills counted toward a target of the log's current rank; 0 for any other rank.</summary>
    public int Kills(HuntLogTarget target)
    {
        try
        {
            var manager = MonsterNoteManager.Instance();
            if (manager == null || target.LogIndex < 0 || target.LogIndex >= manager->RankData.Length)
                return 0;

            ref var log = ref manager->RankData[target.LogIndex];
            if (log.Rank != target.Rank || target.Entry >= log.RankData.Length)
                return 0;

            var counts = log.RankData[target.Entry].Counts;
            return target.Slot < counts.Length ? counts[target.Slot] : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Kills a target still wants. 0 when its rank is already behind the character; the full count
    /// when the rank is still locked or the log cannot be read.
    /// </summary>
    public int KillsLeft(HuntLogTarget target)
    {
        var rank = CurrentRank(target.LogIndex);
        if (rank > target.Rank)
            return 0;
        if (rank < target.Rank)
            return target.Needed;
        return Math.Max(0, target.Needed - Kills(target));
    }
}
