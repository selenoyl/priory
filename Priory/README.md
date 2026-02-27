# PRIORY: Blackpine (C# prototype)

This is a **text-first C# implementation** of the game concept we planned:
- medieval Yorkshire (Blackpine, Saint Rose Priory)
- parser + dialogue menus
- timed responses with default virtue-based fallback
- modular story content in external JSON files
- save/resume short codes with fingerprint

## Structure

- `src/Priory.Game/` - engine code
- `data/scenes/*.json` - scene content
- `data/dialogue/menus.json` - menu/dialogue trees
- `data/dialogue/timed.json` - timed events
- `data/lifepaths.json` - life-path definitions and starting rolls
- `saves/` - generated save files

## Build/run

```bash
cd Priory/src/Priory.Game
# requires .NET 8 SDK
DOTNET_ENVIRONMENT=Development dotnet run
```

## Notes

- Add story expansions by adding new JSON files and linking `nextScene` / `nextMenu` / `nextTimed`.
- Save code format is signed with `PRIORY_SAVE_SECRET`.
- This is intentionally text-only v1; ASCII/visual deluxe can be layered later without rewriting core content architecture.
