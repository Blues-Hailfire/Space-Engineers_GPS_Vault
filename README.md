# Space Engineers GPS Manager

A locally-hosted Discord bot for managing Space Engineers GPS coordinates within a faction. GPS points are stored per-channel in a local SQLite database and can be searched, filtered, visualised on an interactive 3D map, and bulk-removed. An optional Space Engineers client plugin (see [`EventHorizon_Plugin/`](EventHorizon_Plugin/) / [`Pulsar_Plugin/`](Pulsar_Plugin/)) can auto-sync your in-game GPS list and live position straight into the vault.

---

## Requirements

- Python 3.8+
- `discord.py >= 2.3.0`
- `aiohttp >= 3.8.0` (also powers the optional plugin sync API)
- A Discord bot token, in `bot_config.json`'s `discord_token`

Run `setup.py` to create the virtual environment and install dependencies (and, on Linux, optionally install the bot as a systemd service).

---

## Setup

1. Create a Discord bot and put its token, and everything else below, in `bot_config.json` (auto-created with null placeholders on first run — see [`bot_config.py`](bot_config.py); one file for the bot token, sync API host/port, and Discord OAuth credentials, all git-ignored since it holds secrets).
2. Run `python3 setup.py` (as your normal user, not with sudo). It creates `.venv/`, installs `requirements.txt` into it, creates `bot_config.json` if missing and tells you what's still unset. On Linux it also offers to install a systemd service (`gps-vault`) that starts the bot at boot and restarts it on failure — pass `--service` / `--no-service` to skip the prompt, `--mode bot|web` to change what the service runs, or `--uninstall-service` to remove it. Re-running is safe and picks up config changes.
3. (Optional, for the plugin) In `bot_config.json`, set `port` for the sync API, and `public_host` to the address other machines (like your gaming PC) can reach this bot at, so `/create_sync_token` prints a usable endpoint. Forward/open that port on the machine running the bot.
   - By default this serves plain HTTP — fine for same-machine/LAN testing, but the plugin's sync token and your GPS data travel unencrypted. To enable HTTPS, set `cert_file`/`key_file` to a PEM cert+key pair (run `scripts/gen_dev_cert.sh` for a self-signed one to test with; swap in a real cert once you have a domain). To migrate an already-deployed bot without breaking existing plugin installs, also set `https_port` to a different port than `port` — the server then serves plain HTTP on `port` (for old installs) *and* HTTPS on `https_port` at the same time, until everyone's moved over.
4. Start the bot: `.venv/bin/python VectorHandler.py` (`.venv\Scripts\python.exe` on Windows) — or, if you installed the service, `sudo systemctl start gps-vault` (logs: `journalctl -u gps-vault -f`).
5. In your target Discord channel, run `/bind` (requires Administrator) to lock the bot to that channel.

---

## Commands

### `/bind`
Binds the bot to the current channel. All other commands will only work in the bound channel.

> Requires **Administrator** permission.

---

### `/add_gps`
Adds one or more GPS points to the channel's database.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `gps_string` | string | Yes | One or more GPS points in Space Engineers format: `GPS:Name:X:Y:Z:Color:` |

- Accepts multiple points separated by spaces or newlines.
- Supports optional ARGB hex colours (`#AARRGGBB`).

---

### `/search_gps`
Searches the channel's GPS points with optional keyword and distance filtering.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `search_string` | string | No | Keyword to match against point names (case-insensitive) |
| `reference_gps` | string | No | A GPS point used as the distance origin (`GPS:Name:X:Y:Z:Color:`) |
| `distance_km` | float | No | Filters out points beyond this distance (km) from the reference |

**Behaviour:**
- No parameters → returns all points.
- `search_string` only → returns matching points.
- `reference_gps` + `distance_km` → returns points within range, sorted nearest first.
- `reference_gps` + `search_string` → returns matches sorted by distance from reference.

---

### `/map`
Generates an interactive 3D HTML map of all GPS points in the channel and sends it as a downloadable attachment.

The map renders in any modern browser with no dependencies — open the file locally. Features include:

- **3D globe view** with OrbitControls (pan, zoom, rotate)
- **Animated pulse rings** that emit from the selected point at a frequency matching its glow, with ring radius scaling to camera zoom
- **Distance measurement** mode with on-screen readout
- **Sidebar** with searchable, filterable point list — collapses to expand the canvas
- **Point detail panel** showing coordinates and tags
- **Hit detection** — the pulse wave briefly illuminates points it passes through
- Click a selected point (on the globe or in the sidebar) to deselect it

---

### `/remove_gps_by_index_range`
Removes a contiguous range of GPS points by their 1-based index.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `start_index` | integer | Yes | First index to remove (inclusive) |
| `end_index` | integer | Yes | Last index to remove (inclusive) |

---

### `/create_sync_token`
Creates a token for the Space Engineers plugin (see [Space Engineers Plugin](#space-engineers-plugin-auto-sync) below) to auto-sync GPS points and live position into the current (bound) channel. The token and sync endpoint are shown once, ephemerally — paste them into the plugin's settings dialog in Pulsar.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `label` | string | No | A name to help you recognise this token later (e.g. a player's name) |

> Requires **Administrator** permission.

---

### `/revoke_sync_token`
Revokes a sync token by its label prefix (as shown by `/list_sync_tokens`), disabling further syncs from the plugin using it.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `token_prefix` | string | Yes | The first characters of the token to revoke |

> Requires **Administrator** permission.

---

### `/list_sync_tokens`
Lists active sync tokens for the current channel (label and creation date only — never the full token).

> Requires **Administrator** permission.

---

## Space Engineers Plugin (auto-sync)

There are two variants of the client plugin, both loaded by [Pulsar](https://github.com/SpaceGT/Pulsar) — Pulsar itself is not bundled here, it's a prerequisite you install separately. Once loaded, either one periodically reads your in-game GPS list and character position via the official Space Engineers Mod API and pushes them to the bot's sync API, so they show up via `/search_gps` and `/map` without manual copy-pasting. Live position is synced as a single point per player (named `◆ PlayerName`) that updates in place rather than accumulating history.

- **[`EventHorizon_Plugin/`](EventHorizon_Plugin/)** — in-game name **GPS Vault - Mindrazer**. The base plugin described below.
- **[`Pulsar_Plugin/`](Pulsar_Plugin/)** — in-game name **GPS Vault - Datalink**. Same base plugin, with these differences:
  - It syncs less: no planets, asteroids, WeaponCore combat data (ship DPS, incoming missiles, threat ratings) or weapon-lock targets are read or sent. It also has no "Host Locally" option; it only talks to a server you point it at (see [Datalink networking](#datalink-networking) below).
  - After an hour with no keyboard/mouse/joystick input, it prints "Player AFK Vault-Sync Deactivated!" to chat and pauses automatic sync (GPS, position, ore pulls) until input resumes.
  - Signals (antenna/sensor contacts) and your own ore-detector deposits are never pushed automatically — only when you run `/vault capture`, which sends whatever is on your HUD at that moment, once. Other players' ore deposits are still pulled down automatically.
  - Neutral/enemy contacts are reported at a randomized point within an uncertainty radius of their true position instead of their exact location — 10m at 1km, 1km at 10km, 10km at 100km, 100km at 1000km — rather than the exact spot. That radius is only ever reported rounded up to a power-of-two bucket (e.g. 16m at 1km, 1024m at 10km). The offset is rolled once per contact per bucket and reused while the contact stays in that bucket (or returns to it), and only the bucket radius is reported, never the actual offset, so repeated reads can't be averaged or solved back to the truth. Allied/owned/unidentified contacts, and your own GPS/position, are unaffected.
  - Its log is written to `%AppData%\SpaceEngineers\Storage\GpsSyncPluginPulsar\gps-sync.log`, next to its config, not to the game's install folder.

#### Datalink networking

The Datalink plugin makes HTTP(S) requests only to the **Endpoint** URL of your active host profile (the GPS Vault bot's sync API, e.g. `https://your-server:4040`). It contacts no other server: no telemetry, no update checks, no downloads. Every request carries the profile's sync token (`Authorization: Bearer <token>`) and channel ID (`X-Channel-Id`), with a 10-second timeout; request bodies over 32 KB are gzip-compressed. Use an `https://` endpoint, since plain `http://` sends the token unencrypted.

Automatic traffic runs only while sync is on for the current world + host profile (off by default in every world) and you're not AFK:

| Request | When | Data |
|---|---|---|
| `POST /sync/gps` | After an in-game GPS edit, and on joining a world, turning sync on or returning from AFK (pulls first) | Your GPS list (name, coordinates, color) and the names of vault points you deleted in-game |
| `GET /sync/gps` | Every 30 s by default (GPS interval setting) | The vault's GPS list, merged into your in-game list |
| `POST /sync/position` | Every 5 s by default (position interval setting) | Player name, position, velocity, acceleration, local gravity, ship rotation |
| `GET /sync/ores` | Every 30 s | The channel's shared ore deposits, added as hidden `[ORE]` GPS points |

On demand only:

| Request | When | Data |
|---|---|---|
| `POST /sync/hostiles` | `/vault capture` | Signals on your HUD (neutral/enemy positions fuzzed, see above) |
| `POST /sync/ores` | `/vault capture` | Your ore-detector deposits |
| `GET /sync/players` | Every 5 s while `/vault players` is on | Other players' current positions |
| `GET /sync/whoami` | **Test Connection** in the settings dialog | The Discord server/channel name for that profile |

The **Bring Your Own Bot** button in the settings dialog opens the bot's GitHub repo in your browser; the plugin itself makes no request there.

**Setup** (pick one plugin folder and its manifest below — steps are identical either way):
1. On the bot side, `/bind` a channel and run `/create_sync_token` to get an endpoint + token.
2. In Pulsar, add the plugin as a source using the raw URL to its manifest — [`Plugins/GpsSyncPlugin.xml`](Plugins/GpsSyncPlugin.xml) for EventHorizon (Mindrazer), or [`Plugins/GpsSyncPluginPulsar.xml`](Plugins/GpsSyncPluginPulsar.xml) for the Pulsar variant (Datalink). Pulsar reads that manifest to pull the source from this repo (via `RepoId`/`Commit`/`SourceDirectories`), build it (pointing `GameBinPath` at your Space Engineers `Bin64` folder — see the plugin folder's `GpsSyncPlugin.csproj`), and list it in-game. Alternatively, build the `.csproj` yourself and load the resulting DLL via Pulsar's local/manual plugin option.
3. In Pulsar's plugin list, select the plugin you added and click **Configure** to open its settings dialog; pick or add a sync profile and set its `Endpoint`/`Token`/`Channel ID` to the values from step 1 (endpoint/token/channel changes apply immediately, but turn sync off for the current world until you re-enable it; interval changes need a restart). You can save several profiles (e.g. one per Discord server) and switch between them from the dropdown — use "Look Up Server/Channel Names" to label a profile with its real Discord names instead of raw IDs. Sync is off by default in every new world/server; enabling it sticks to that world+profile combination across restarts.

> Each manifest (`Plugins/GpsSyncPlugin.xml`, `Plugins/GpsSyncPluginPulsar.xml`, and each plugin folder's own copy) pins a specific commit (`Commit`) — after pushing changes to that plugin's folder, update its `Commit` field(s) to the new commit hash so Pulsar picks up the change. The two plugins share this one repo, so `SourceDirectories` in each manifest is what keeps Pulsar building only that plugin's own folder.

**Sync directions:**
- **Game → Discord**: your in-game GPS list and live position (with velocity) push up to the vault, and deleting a vault GPS point in-game moves it to the vault's trash too. Only points you actually had and then deleted are trashed — a push never removes points other players added that you haven't pulled yet — and when you join a world, turn sync on, or return from being AFK, the plugin pulls the vault first, then pushes. A trashed point stays trashed (a player who still has it locally can't bring it back) until `/empty_trash` erases it. Live position syncs as a single point per player (named `◆ PlayerName`) that updates in place, plus a rolling 6-hour position history used for the web map's movement trail.
- **Discord/vault → Game**: any point in the vault (added via `/add_gps`, or synced from another player) gets added to your in-game GPS list too — hidden from the HUD by default, since the vault can hold far more points than are useful cluttering it at once. Vault deletions never propagate down automatically. Use these in-game chat commands to browse and reveal them:
  - `/vault search [text]` (aliases `/vs`, `/VS`) — lists your current GPS points (optionally filtered by name), nearest first, e.g.:
    ```
    1: Ice AST (1.20 km)
    2: Iron AST (3.40 km)
    ```
  - `/vault confirm <number> [-h | -hide all]` (aliases `/vc`, `/VC`) — reveals that result on the HUD and hides the other points from that same search. Add `-h` (or `-hide all`) to also hide every other GPS point you have, not just that search's results.
  - `/vault sync [on|off]` — pauses or resumes syncing for the current world/server and active sync profile (same switch as the plugin's "Sync Enabled" setting); with no argument, toggles.
  - `/vault capture` (Datalink/Pulsar plugin only) — sends the signals and ore deposits currently on your HUD to the vault, once.
  - `/vault host "name"` (aliases `/vh`, `/VH`) — switches the active sync profile ("host profile") to the one with that nickname; doesn't change that world's sync on/off state, since that's saved per world+profile.
  - `/vault list profiles` / `/vault list hosts` (aliases `/vlh`, `/VLH`) — lists your saved host profiles, marking the active one.
  - `/vault` / `/vault help` / `/vault -h` (aliases `/v`, `/V`) — pops up an in-game dialog listing every command above and what it does.

---

## Live Web Map

`/map` (served by the same sync API, default `http://localhost:4040/map`) shows a live, browser-based version of the 3D map with a channel picker labeled `GuildName/ChannelName`. Player markers move in real time, trail their last 6 hours of positions (solid line), and show a short dashed line forecasting where they're headed (extrapolated from their last observed velocity and acceleration).

Access requires logging in with Discord — a viewer only sees channels they could actually read in that Discord server (checked live against the bot's own cached member/role/channel permissions, not stored separately). To enable login:

1. In the [Discord Developer Portal](https://discord.com/developers/applications), open your bot's application → **OAuth2** → add a redirect URL: `http://<host>:<port>/oauth/callback` (matching `bot_config.json`'s `public_host`/`port`, e.g. `http://localhost:4040/oauth/callback`).
2. Copy that page's **Client ID** and **Client Secret** into `bot_config.json`'s `oauth_client_id`/`oauth_client_secret`, along with the exact same `redirect_uri` in `oauth_redirect_uri`.
3. Restart the bot.

Until configured, `/login` shows a message instead of erroring.

---

## Project Structure

```
VectorHandler.py        # Bot entry point — all commands and database logic
sync_api.py              # HTTP API (aiohttp): plugin sync, live web map, Discord login
web_auth.py              # Discord OAuth2 login + signed session cookies for the web map
bot_config.py             # Loads bot_config.json (see below); shared by all three files above
bot_config.json           # Bot token, sync API host/port, Discord OAuth creds — one file, not committed
session_secret.key        # Auto-generated cookie-signing key (not committed)
EventHorizon_Plugin/     # Space Engineers client plugin (loaded by Pulsar) — "GPS Vault - Mindrazer"
Pulsar_Plugin/           # Leaner variant: capture-only signal/ore sends, AFK comms cutoff, fuzzed neutral/enemy contacts — "GPS Vault - Datalink"
Plugins/                 # Root-level Pulsar PluginData manifests (repo, source dir, pinned commit) for both above
EventHorizon_Plugin/GpsSyncPlugin.xml, Pulsar_Plugin/DatalinkPlugin.xml # Per-plugin copy of its own manifest
*_Plugin/Settings/       # In-game settings dialog (Pulsar's "Configure" button)
*_Plugin/ChatCommands.cs # In-game /vault search, /vault confirm (and /vs, /vc) commands
templates/map.html      # Interactive 3D map template (Three.js) — shared by the live web map and the Discord /map command
local_server.py          # Standalone web server (built into an exe by .github/workflows); formerly started by Datalink's removed "Host Locally" option
setup.py                # Installer: venv + dependencies + bot_config.json, optional Linux systemd service
VaultDatabase.db          # SQLite database (auto-created on first run)
```

---

## Database

SQLite (`VaultDatabase.db`; an existing `gps.db` is renamed automatically on first start) with these tables:

- **`gps_points`** — GPS coordinates scoped to guild + channel.
- **`guild_bindings`** — Maps each guild to its bound channel(s).
- **`sync_tokens`** — Tokens authorizing the plugin's sync API to write into a specific guild/channel.
- **`position_history`** — Rolling 6-hour position/velocity samples per live player, used for the web map's movement trail.
