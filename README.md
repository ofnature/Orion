# Orion

A Dalamud plugin for Final Fantasy XIV that farms overworld mobs and works through the hunt log.
Orion picks the targets and does the travelling; [Daedalus](https://github.com/ofnature/Daedalus)
does the fighting.

The window has two modes, picked on its left side:

- **Trophy Hunt**: name an item and a count, list the mobs that drop it, place one or more spots.
  Orion kills those mobs around the spots, mounted between the long legs, until the bag holds the
  count.
- **Hunt Log**: shows the current rank of each class and Grand Company log with kills still owed.
  START works through the rank unattended: it travels to each mob, kills what the log still wants,
  and rolls into the next rank. Dungeon and FATE mobs are skipped. "Farm this" does one target by
  hand.

Under the modes is the **fleet**: what the Orions on your other characters are doing, heard over
Daedalus's LAN bridge.

`/orion` opens the window, `/orion log` the hunt log, `/orion stop` stops a run. Orion also sits
as a tab on the screen edge, with the rest of the suite's.

## Needs

- Daedalus, for the fighting. It fights for Orion while `Orion.IsBusy` is true.
- Ariadne or vnavmesh, for the walking in farm mode.
- Ariadne and Lifestream, for the hunt log run's travel.

## Building

```
dotnet build Orion.sln -c Debug
dotnet build Orion.sln -c Release
dotnet test Orion.Tests
```

AGPL-3.0. See `NOTICE.md` for what came from where.
