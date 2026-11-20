using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace Orion.Services.HuntLog;

/// <summary>One of the twelve logs: a class's or a Grand Company's.</summary>
/// <param name="Index">Its place in the game's progress array.</param>
/// <param name="ClassJobId">The base class, or 0 for a Grand Company log.</param>
/// <param name="GrandCompanyId">The Grand Company, or 0 for a class log.</param>
public sealed record HuntLogBook(int Index, string Name, uint ClassJobId, uint GrandCompanyId);

/// <summary>A place the game's log names for a mob: the overworld zone and the area within it.</summary>
public sealed record HuntLogZone(uint TerritoryId, string ZoneName, string AreaName);

/// <summary>One mob of one log entry, and how many of it the entry wants.</summary>
/// <param name="Entry">0 to 9 within the rank.</param>
/// <param name="Slot">0 to 3 within the entry.</param>
/// <param name="NameId">BNpcName row id: what the object table reports for the mob.</param>
public sealed record HuntLogTarget(
    int LogIndex, int Rank, int Entry, int Slot, uint NameId, string MobName, int Needed, IReadOnlyList<HuntLogZone> Zones);

/// <summary>
/// The hunt log as the game's sheets describe it: which mobs each rank of each log wants, how
/// many, and the zones it names for them. The sheets carry no coordinates, only place names.
/// </summary>
public sealed class HuntLogCatalog
{
    /// <summary><c>TerritoryIntendedUse</c> of an ordinary overworld zone.</summary>
    private const uint OverworldUse = 1;

    private readonly IDataManager _data;
    private readonly Dictionary<uint, uint> _territoryByPlaceName = new();

    public IReadOnlyList<HuntLogBook> Books { get; }

    public HuntLogCatalog(IDataManager data)
    {
        _data = data;

        // Towns and duties share place names with the overworld zone; the overworld one is wanted.
        foreach (var territory in data.GetExcelSheet<TerritoryType>())
        {
            if (territory.TerritoryIntendedUse.RowId == OverworldUse && territory.PlaceName.RowId != 0)
                _territoryByPlaceName.TryAdd(territory.PlaceName.RowId, territory.RowId);
        }

        var books = new List<HuntLogBook>();
        var seen = new HashSet<uint>();
        foreach (var job in data.GetExcelSheet<ClassJob>())
        {
            // A class and its jobs share one log; the class comes first in the sheet and owns the
            // rows. Rows without a name are placeholders that also read 0.
            var index = job.MonsterNote.RowId;
            if (job.RowId == 0 || index >= HuntLogIds.LogCount || job.Name.IsEmpty || !seen.Add(index))
                continue;
            books.Add(new HuntLogBook((int)index, Capitalize(job.Name.ExtractText()), job.RowId, 0));
        }

        foreach (var company in data.GetExcelSheet<GrandCompany>())
        {
            if (company.RowId is 0 or > 3)
                continue;
            books.Add(new HuntLogBook(HuntLogIds.GrandCompanyLogIndex(company.RowId), company.Name.ExtractText(), 0, company.RowId));
        }

        Books = books;
    }

    /// <summary>The log of the class (or job) with this id, or null when it has none.</summary>
    public HuntLogBook? BookForClassJob(uint classJobId)
    {
        if (classJobId == 0 || _data.GetExcelSheet<ClassJob>().GetRowOrDefault(classJobId) is not { } job)
            return null;
        foreach (var book in Books)
        {
            if (book.GrandCompanyId == 0 && book.Index == (int)job.MonsterNote.RowId)
                return book;
        }
        return null;
    }

    /// <summary>Every mob one rank of a log wants, in the log's own order.</summary>
    public IReadOnlyList<HuntLogTarget> Targets(HuntLogBook book, int rank)
    {
        var targets = new List<HuntLogTarget>();
        if (rank is < 0 or >= HuntLogIds.RanksPerLog)
            return targets;

        var notes = _data.GetExcelSheet<MonsterNote>();
        for (var entry = 0; entry < HuntLogIds.EntriesPerRank; entry++)
        {
            var rowId = book.GrandCompanyId != 0
                ? HuntLogIds.GrandCompanyRow(book.GrandCompanyId, rank, entry)
                : HuntLogIds.ClassRow(book.ClassJobId, rank, entry);
            if (notes.GetRowOrDefault(rowId) is not { } note)
                continue;

            for (var slot = 0; slot < HuntLogIds.TargetsPerEntry && slot < note.MonsterNoteTarget.Count; slot++)
            {
                if (note.MonsterNoteTarget[slot].ValueNullable is not { } target || target.RowId == 0 || note.Count[slot] == 0)
                    continue;
                if (target.BNpcName.ValueNullable is not { } name || name.RowId == 0)
                    continue;

                targets.Add(new HuntLogTarget(
                    book.Index, rank, entry, slot, name.RowId, Capitalize(name.Singular.ExtractText()),
                    note.Count[slot], ZonesOf(target)));
            }
        }
        return targets;
    }

    /// <summary>
    /// A target lists up to three places, one per starting city, and leaves the others at 0.
    /// </summary>
    private List<HuntLogZone> ZonesOf(MonsterNoteTarget target)
    {
        var zones = new List<HuntLogZone>();
        for (var i = 0; i < target.PlaceNameZone.Count; i++)
        {
            if (target.PlaceNameZone[i].ValueNullable is not { } zone || zone.RowId == 0)
                continue;
            _territoryByPlaceName.TryGetValue(zone.RowId, out var territoryId);
            var area = i < target.PlaceNameLocation.Count ? target.PlaceNameLocation[i].ValueNullable?.Name.ExtractText() : null;
            zones.Add(new HuntLogZone(territoryId, zone.Name.ExtractText(), area ?? string.Empty));
        }
        return zones;
    }

    /// <summary>Sheet names are lowercase ("little ladybug", "gladiator").</summary>
    private static string Capitalize(string name)
        => name.Length > 0 && char.IsLower(name[0]) ? char.ToUpperInvariant(name[0]) + name[1..] : name;
}
