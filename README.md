# Space Engineers GPS Manager

A locally-hosted Discord bot for managing Space Engineers GPS coordinates within a faction. GPS points are stored per-channel in a local SQLite database and can be searched, filtered, visualised on an interactive 3D map, and bulk-removed. An optional Space Engineers client plugin (see [`plugin/`](plugin/)) can auto-sync your in-game GPS list and live position straight into the vault.

---

## Requirements

- Python 3.8+
- `discord.py >= 2.3.0`
- `aiohttp >= 3.8.0` (also powers the optional plugin sync API)
- A Discord bot token in `DiscordToken.txt`

Run `setup.py` to create the virtual environment and install dependencies.

---

## Setup

1. Create a Discord bot and place its token in `DiscordToken.txt`.
2. Run `python setup.py` to initialise the virtual environment.
3. (Optional, for the plugin) Edit `sync_config.json` — `port` for the sync API, and `public_host` set to the address other machines (like your gaming PC) can reach this bot at, so `/create_sync_token` prints a usable endpoint. Forward/open that port on the machine running the bot.
4. Start the bot: `python VectorHandler.py`.
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
Creates a token for the [Space Engineers plugin](plugin/) to auto-sync GPS points and live position into the current (bound) channel. The token and sync endpoint are shown once, ephemerally — paste them into the plugin's `config.json`.

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

The [`plugin/`](plugin/) folder is a standalone client plugin loaded by [Pulsar](https://github.com/SpaceGT/Pulsar) — Pulsar itself is not bundled here, it's a prerequisite you install separately. Once loaded, it periodically reads your in-game GPS list and character position via the official Space Engineers Mod API and pushes them to the bot's sync API, so they show up via `/search_gps` and `/map` without manual copy-pasting. Live position is synced as a single point per player (named `◆ PlayerName`) that updates in place rather than accumulating history.

**Setup:**
1. On the bot side, `/bind` a channel and run `/create_sync_token` to get an endpoint + token.
2. In Pulsar, add this plugin as a source using the raw URL to [`plugin/GpsSyncPlugin.xml`](plugin/GpsSyncPlugin.xml) — Pulsar reads that manifest to pull the source from this repo (via `RepoId`/`Commit`), build it (pointing `GameBinPath` at your Space Engineers `Bin64` folder — see [`plugin/GpsSyncPlugin.csproj`](plugin/GpsSyncPlugin.csproj)), and list it in-game. Alternatively, build the `.csproj` yourself and load the resulting DLL via Pulsar's local/manual plugin option.
3. In Pulsar's plugin list, select **GPS Sync** and click **Configure** to open its settings dialog; set `Endpoint`/`Token` to the values from step 1 (endpoint/token changes apply immediately; interval changes need a restart).

> `plugin/GpsSyncPlugin.xml` pins a specific commit (`Commit`) — after pushing changes to `plugin/`, update that field to the new commit hash so Pulsar picks up the change.

**Sync directions:**
- **Game → Discord**: your in-game GPS list and live position (with velocity) push up periodically, and *replace* the vault's copy of your GPS list — so deleting a GPS point in-game deletes it from the vault too. (With more than one player actively syncing the same channel, each push is treated as that player's full authoritative list — a point one player still has locally can briefly reappear if their next push lands right after another player deletes it. Fine for one active syncer per channel.) Live position syncs as a single point per player (named `◆ PlayerName`) that updates in place, plus a rolling 6-hour position history used for the web map's movement trail.
- **Discord/vault → Game**: any point in the vault (added via `/add_gps`, or synced from another player) gets added to your in-game GPS list too — hidden from the HUD by default, since the vault can hold far more points than are useful cluttering it at once. Vault deletions never propagate down automatically. Use these in-game chat commands to browse and reveal them:
  - `/vault search [text]` (aliases `/vs`, `/VS`) — lists your current GPS points (optionally filtered by name), nearest first, e.g.:
    ```
    1: Ice AST (1.20 km)
    2: Iron AST (3.40 km)
    ```
  - `/vault confirm <number> [-h | -hide all]` (aliases `/vc`, `/VC`) — reveals that result on the HUD and hides the other points from that same search. Add `-h` (or `-hide all`) to also hide every other GPS point you have, not just that search's results.
  - `/vault sync [on|off]` — pauses or resumes all syncing (same switch as the plugin's "Sync Enabled" setting); with no argument, toggles.

---

## Live Web Map

`/map` (served by the same sync API, default `http://localhost:8765/map`) shows a live, browser-based version of the 3D map with a channel picker labeled `GuildName/ChannelName`. Player markers move in real time, trail their last 6 hours of positions (solid line), and show a short dashed line forecasting where they're headed (extrapolated from their last observed velocity and acceleration).

Access requires logging in with Discord — a viewer only sees channels they could actually read in that Discord server (checked live against the bot's own cached member/role/channel permissions, not stored separately). To enable login:

1. In the [Discord Developer Portal](https://discord.com/developers/applications), open your bot's application → **OAuth2** → add a redirect URL: `http://<host>:<port>/oauth/callback` (matching `sync_config.json`'s `public_host`/`port`, e.g. `http://localhost:8765/oauth/callback`).
2. Copy that page's **Client ID** and **Client Secret** into `oauth_config.json` (auto-created on first run, git-ignored) along with the exact same `redirect_uri`.
3. Restart the bot.

Until configured, `/login` shows a message instead of erroring.

---

## Project Structure

```
VectorHandler.py        # Bot entry point — all commands and database logic
sync_api.py              # HTTP API (aiohttp): plugin sync, live web map, Discord login
web_auth.py              # Discord OAuth2 login + signed session cookies for the web map
sync_config.json         # Host/port config for the sync API
oauth_config.json         # Discord OAuth client_id/secret/redirect_uri (not committed)
session_secret.key        # Auto-generated cookie-signing key (not committed)
plugin/                  # Space Engineers client plugin (loaded by Pulsar)
plugin/GpsSyncPlugin.xml # Pulsar PluginData manifest (repo, source dir, pinned commit)
plugin/Settings/         # In-game settings dialog (Pulsar's "Configure" button)
plugin/ChatCommands.cs   # In-game /vault search, /vault confirm (and /vs, /vc) commands
templates/map.html      # Interactive 3D map template (Three.js)
setup.py                # Environment setup script
DiscordToken.txt        # Bot token (not committed)
gps.db                  # SQLite database (auto-created on first run)
```

---

## Database

SQLite (`gps.db`) with these tables:

- **`gps_points`** — GPS coordinates scoped to guild + channel.
- **`guild_bindings`** — Maps each guild to its bound channel(s).
- **`sync_tokens`** — Tokens authorizing the plugin's sync API to write into a specific guild/channel.
- **`position_history`** — Rolling 6-hour position/velocity samples per live player, used for the web map's movement trail.
