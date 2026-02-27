# PRIORY: Blackpine (C# modular campaign engine)

`PRIORY` is a Catholic-historical text adventure focused on rebuilding Saint Rose Priory in medieval Yorkshire.

## Design goals
- **Modular content authoring** to reduce merge conflicts:
  - one file per scene/menu/timed event/life path/quest/item/shop
  - recursive loading from folders
- **Engine/content separation**:
  - C# runtime in `src/Priory.Game/`
  - authored narrative/gameplay data in `data/`
- **Long-form playability**:
  - main questline: revitalizing Saint Rose
  - side quests: Franciscans, Carmelites, convents, Benedictines, guilds, York contacts
  - repeatable task loops + escalating arcs
- **Server readiness**:
  - Dockerfile + compose + persistent saves

## Content layout (modular)

- `data/scenes/**/*.json` - one scene per file
- `data/dialogue/menus/**/*.json` - one menu per file
- `data/dialogue/timed/**/*.json` - one timed encounter per file
- `data/lifepaths/*.json` - one life path per file
- `data/quests/*.json` - quest metadata
- `data/items/*.json` - item metadata
- `data/shops/*.json` - shop definitions

> Practical workflow: story updates usually touch a single file, minimizing merge overlap.

## Core systems
- parser with synonym verbs
- menu-driven dialogue and branch logic
- timed responses with virtue-based default behavior
- virtues + priory stats + time segments
- quest log (active/completed)
- item economy + scripted shops
- signed save/resume codes (`PRIORY_SAVE_SECRET`)
- optional party mode with shareable party codes, shared priory/world state, and cross-player lore remarks
- quest metadata hooks for future synchronized multi-person quest flows

## Run locally

```bash
cd Priory/src/Priory.Game
# .NET 8 SDK required
dotnet run
```

Useful commands in-game:
- `help`
- `look`
- `inventory`
- `status`
- `quests`
- `party`
- `save`

On a new game you will be prompted to set a character name and choose solo, create-party, or join-party mode.

## Docker

```bash
cd Priory
docker compose run --rm priory-game
```

Set a real secret in production:
- `PRIORY_SAVE_SECRET=<long-random-value>`

Saves persist in:
- `Priory/saves/`

## Notes on future expansion
To add new story without engine edits, add new scene/menu/timed/quest files and reference IDs via:
- `nextScene`
- `nextMenu`
- `nextTimed`
- `script` hooks (`task:*`, `shop:*`, progress gates)

This keeps mainline development stable while expansion packs can be authored in parallel branches.
