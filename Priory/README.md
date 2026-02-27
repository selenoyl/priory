# PRIORY: Blackpine (C# text adventure)

A **C# text-first implementation** of the Blackpine / Saint Rose concept:
- medieval Yorkshire setting with Dominican focus,
- parser + context menus + timed responses,
- virtue-driven defaults in timed events,
- priory management stats (food/morale/piety/security/relations/treasury),
- liturgical time flow (day + segments),
- modular story content in JSON,
- signed save/resume codes with fingerprints.

## Project layout

- `src/Priory.Game/` - engine, parser, save codec, CLI.
- `data/scenes/*.json` - world scenes and map structure.
- `data/dialogue/menus.json` - branching dialogue and choices.
- `data/dialogue/timed.json` - timed encounter definitions.
- `data/lifepaths.json` - fully authored life-path starts.
- `saves/` - generated save-state files.

## Run locally

```bash
cd Priory/src/Priory.Game
# requires .NET 8 SDK
dotnet run
```

## Docker / server readiness

```bash
cd Priory
docker compose run --rm priory-game
```

For persistent saves and secure resume codes in production, set:
- `PRIORY_SAVE_SECRET` to a long random value.

If you want this exposed under a subdomain, run this container behind your reverse proxy stack (NPM/SWAG/Traefik) with an interactive terminal frontend (e.g., wetty/ttyd) or integrate a web host in a follow-up phase.

## Content and expansion model

This build includes:
- full life-path intros for all five starts,
- Blackpine opening arc,
- priory hub loop with repeatable major task tracks,
- village/york/winter escalation arcs,
- ending resolution tied to priory-state outcomes.

Add expansions by creating/linking new scenes and menus with `nextScene`, `nextMenu`, `nextTimed`, and `script` hooks.
