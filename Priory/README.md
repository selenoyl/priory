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
  - Dockerfile + compose + persistent saves + HTTP mode for reverse proxy

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
- web server mode (`PRIORY_SERVER_MODE=true`) with session API + lightweight browser client
- lore-accurate sterling purse display (pounds / shillings / pence) plus Hanse mark exchange at Ravenscar
- Hanse Letters + Holy Roman Empire (Lübeck / Cologne) expansion arc with doctrinal disputation content

## Run locally (CLI)

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

## Docker (HTTP mode)

```bash
cd Priory
docker compose up -d --build
```

Then browse to:
- `http://<host>:8088/`

Set a real secret in production:
- `PRIORY_SAVE_SECRET=<long-random-value>`

Saves persist in:
- `Priory/saves/`


### Default Unraid compose (ready to paste)

```yaml
version: "3.9"
services:
  priory-game:
    build:
      context: /mnt/user/appdata/priory/Priory
      dockerfile: Dockerfile
    image: priory-game:latest
    container_name: priory-game
    restart: unless-stopped
    environment:
      - PRIORY_SAVE_SECRET=SET_A_LONG_RANDOM_SECRET
      - PRIORY_SERVER_MODE=true
      - ASPNETCORE_URLS=http://0.0.0.0:8080
    ports:
      - "8088:8080"
    volumes:
      - /mnt/user/appdata/priory/saves:/app/saves
```

## Unraid + Nginx Proxy Manager + Cloudflare (priory.raikes.us)

1. **Unraid pathing**
   - Put the repo under: `/mnt/user/appdata/priory/Priory`
   - Keep saves at: `/mnt/user/appdata/priory/saves`

2. **Deploy compose stack in Unraid**
   - Use Unraid's Docker Compose Manager plugin.
   - Create stack, paste `Priory/docker-compose.unraid.yml` (recommended) or adapt `Priory/docker-compose.yml`, set secret and deploy.

3. **Nginx Proxy Manager**
   - Add Proxy Host:
     - Domain: `priory.raikes.us`
     - Scheme: `http`
     - Forward Hostname/IP: your Unraid host LAN IP (or Docker bridge target)
     - Forward Port: `8088` (Priory container)
     - Enable Websockets support (safe default for future features)
   - Request SSL certificate in NPM and force SSL.

4. **Cloudflare DNS**
   - Add `A` record:
     - Name: `priory`
     - Content: your public IP
     - Proxy: enabled (orange cloud)
   - Ensure your router forwards 80/443 to the Nginx Proxy Manager host.
   - You do **not** need to expose 8088 publicly on the router when proxying through NPM.

5. **Important**
   - Keep `PRIORY_SAVE_SECRET` stable. Rotating it invalidates previously issued save and party codes.

## HTTP API quick reference

- `GET /healthz`
- `POST /api/sessions` with body:
  - `{ "mode":"solo|create|join", "playerName":"Name", "partyCode":"..." }`
  - or `{ "resumeCode":"BP-..." }`
- `POST /api/sessions/{id}/command` with `{ "input":"look" }`
- `POST /api/sessions/{id}/timed` with `{ "choice":1 }`
- `DELETE /api/sessions/{id}`

## Notes on future expansion
To add new story without engine edits, add new scene/menu/timed/quest files and reference IDs via:
- `nextScene`
- `nextMenu`
- `nextTimed`
- `script` hooks (`task:*`, `shop:*`, progress gates)

This keeps mainline development stable while expansion packs can be authored in parallel branches.
