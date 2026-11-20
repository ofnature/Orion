# Notices

Orion is AGPL-3.0 (see `COPYING`). This file records what comes from where, and on what terms.
Keep it current as sources are added.

## Data shipped with attribution

### FFXIV Teamcraft — MIT

`Orion/Assets/hunt-positions.json` is built from Teamcraft's `monsters.json`
(`ffxiv-teamcraft/ffxiv-teamcraft`, `libs/data/src/lib/json/monsters.json`, staging branch,
fetched 2026-11-19): positions players' clients reported for each mob. **It is modified**:
`Tools/import_teamcraft_monsters.py` keeps the hunt log's mobs only, drops FATE spawns, and groups
each mob's points on a map into a few spots. Every release carries
`licenses/MIT-ffxiv-teamcraft.txt` beside the data. MIT is compatible with the AGPL.

## Code from the same suite

### Daedalus (same author, AGPL-3.0)

Farm mode (`Orion/Services/Farm`, `Orion/Services/Nav`, `Orion/Services/Game/EnemyScanner.cs`)
was copied out of Daedalus (`ofnature/Daedalus`, v0.1.127) on 2026-11-19. Daedalus keeps its own
copy for now.

### Demeter (same author, AGPL-3.0)

Travel (`Orion/Services/Travel`, `Orion/Services/Game/TravelWorld.cs`, the Ariadne and Lifestream
IPC clients, and their tests) was copied from Demeter (`ofnature/Demeter`, v0.1.4).

### Odysseus (same author, AGPL-3.0)

The fleet list (`Orion/Services/Fleet`, `Orion/Services/Ipc/RelayIpc.cs`, and their tests) was
copied from Odysseus (`ofnature/Odysseus`), with Orion's own status record. It rides Daedalus's
LAN relay (`Daedalus.Relay.Publish` / `Daedalus.Relay.Message`).

### Parcae (AGPL-3.0)

The window styling, the step header, stat tile, hero button and sidebar tab components, and the
edge dock (`Orion/Windows/Styling.cs`, `Orion/Windows/Components`, `Orion/Windows/Dock`,
`Orion/Windows/EdgeDockWindow.cs`) were copied from Parcae, and the header banner is adapted from
its. Parcae is derived from FFXIV-AutoFATEGrind:

    Copyright (c) 2026 Xeldar Alz
    Modifications copyright (c) 2026 Kongool

## Read, not shipped

### Henchman (Knightmore, AGPL-3.0)

No Henchman code or data is shipped. The hunt log reader (`Orion/Services/HuntLog`) was written
against the game's own sheets and structs after reading how Henchman's `HuntDatabase` and
`HuntLogHelper` address them: the `MonsterNote` row id layout, and the kill counts in
`MonsterNoteManager.RankData`. Those are facts about the game client.

Henchman's mob spawn positions are **not** used and must not be: they live in its private
`Underlings` library, which has no public source and no licence.

### GarlandTools

The main window's "Find droppers" asks `garlandtools.org` at runtime which mobs drop an item and
where they stand. Nothing from it is bundled.
