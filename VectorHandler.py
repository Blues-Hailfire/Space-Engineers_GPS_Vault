import argparse
import asyncio
import discord
from discord.ext import commands
import io
import json
import math
import os
import secrets
import sqlite3
import sys
import time
from contextlib import contextmanager
from typing import Optional

import bot_config
import sync_api

bot = commands.Bot(command_prefix="/", intents=discord.Intents.all())

# Whether on_ready should also start the sync/map web server (see --mode in
# the __main__ block below). True by default so importing/running this
# module without going through argparse (e.g. a one-off inspection script,
# or the old pre-"--mode" behavior) still starts both, unchanged.
RUN_WEB_SERVER = True

# Set by local_server.py (the Datalink plugin's "Host Locally" server): no
# Discord bot is connected there, so every permission check that normally
# reads the bot's guild/member cache would fail closed. The server only
# listens on 127.0.0.1 in that mode, so whoever can reach it is the local
# player — grant access instead.
LOCAL_ACCESS = False

DB_PATH = "VaultDatabase.db"
TEMPLATES_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), "templates")
MAP_TEMPLATE_PATH = os.path.join(TEMPLATES_DIR, "map.html")


DEFAULT_GPS_COLOR = "#FFFFFFFF"


class Vector3D:
    def __init__(self, name, x, y, z, color=DEFAULT_GPS_COLOR):
        self.name = name
        self.x = x
        self.y = y
        self.z = z
        self.color = color or DEFAULT_GPS_COLOR

    def __str__(self):
        return f"GPS:{self.name}:{self.x}:{self.y}:{self.z}:{self.color}:"


def parse_gps_data(gps_string):
    try:
        data = gps_string.replace("GPS:", "").replace("\n", "").strip(":")
        parts = data.split(":")
        name = parts[0]
        x = float(parts[1])
        y = float(parts[2])
        z = float(parts[3])
        color = parts[4] if len(parts) > 4 and parts[4] else DEFAULT_GPS_COLOR
    except (IndexError, ValueError) as e:
        raise Exception("Not A Valid GPS point") from e

    if len(name) > 32:
        raise Exception(f"GPS name exceeds 32 character limit ({len(name)} characters)")
    return Vector3D(name, x, y, z, color)


def euclidean_distance(v1, v2):
    return math.sqrt((v1.x - v2.x) ** 2 + (v1.y - v2.y) ** 2 + (v1.z - v2.z) ** 2)


@contextmanager
def db():
    parent = os.path.dirname(os.path.abspath(DB_PATH))
    if parent and not os.path.isdir(parent):
        os.makedirs(parent, exist_ok=True)
    conn = sqlite3.connect(DB_PATH)
    conn.row_factory = sqlite3.Row
    try:
        yield conn
        conn.commit()
    finally:
        conn.close()


LEGACY_DB_NAME = "gps.db"


def migrate_legacy_db():
    """Servers created before the VaultDatabase.db rename still have their
    data in gps.db next to it. Renames it (and any SQLite sidecar files) into
    place the first time the new name is wanted, so no data is lost. Never
    overwrites an existing VaultDatabase.db."""
    new_path = os.path.abspath(DB_PATH)
    legacy_path = os.path.join(os.path.dirname(new_path), LEGACY_DB_NAME)
    if os.path.exists(new_path) or not os.path.exists(legacy_path):
        return
    for suffix in ("", "-wal", "-shm", "-journal"):
        src = legacy_path + suffix
        if os.path.exists(src):
            os.replace(src, new_path + suffix)
    print(f"[db] migrated legacy {LEGACY_DB_NAME} -> {os.path.basename(new_path)}")

def init_db():
    migrate_legacy_db()
    fresh = not os.path.exists(DB_PATH)
    if fresh:
        print(f"[db] no database found, creating new one at {os.path.abspath(DB_PATH)}")
    with db() as conn:
        conn.execute("""
            CREATE TABLE IF NOT EXISTS gps_points (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                guild_id   INTEGER NOT NULL,
                channel_id INTEGER NOT NULL,
                name       TEXT    NOT NULL,
                x          REAL    NOT NULL,
                y          REAL    NOT NULL,
                z          REAL    NOT NULL,
                color      TEXT    NOT NULL DEFAULT '#FFFFFFFF',
                color_override INTEGER NOT NULL DEFAULT 0,
                updated_at REAL,
                deleted_at REAL
            )
        """)
        conn.execute("""
            CREATE INDEX IF NOT EXISTS idx_gps_guild_channel
            ON gps_points (guild_id, channel_id, id)
        """)
        # Add color column if upgrading from a pre-color schema.
        existing = {row["name"] for row in conn.execute("PRAGMA table_info(gps_points)")}
        if "color" not in existing:
            conn.execute(
                "ALTER TABLE gps_points ADD COLUMN color TEXT NOT NULL DEFAULT '#FFFFFFFF'"
            )
        # color_override: a manually (re)colored live position marker keeps
        # its own color instead of the next /sync/position push (or the
        # "player" category's admin-set color) overwriting it — see
        # upsert_gps_point/edit_gps_point and apply_player_category_settings.
        if "color_override" not in existing:
            conn.execute("ALTER TABLE gps_points ADD COLUMN color_override INTEGER NOT NULL DEFAULT 0")
        # updated_at: when a live position marker (◆ prefix) was last synced —
        # the "player" category's admin-configured retention (see
        # CATEGORY_SETTING_DEFAULTS/prune_stale_player_markers) uses this to
        # decide when a player who stopped syncing disappears from the map.
        # Vault GPS points (added via /add_gps etc.) never get pruned by that
        # mechanism regardless of this column's value — see
        # prune_stale_player_markers's name-prefix filter.
        if "updated_at" not in existing:
            conn.execute("ALTER TABLE gps_points ADD COLUMN updated_at REAL")
            # Back-fill existing live markers to "now" rather than NULL/0, so
            # upgrading to this version doesn't instantly expire every
            # currently-active player the next time the map is viewed.
            conn.execute(
                "UPDATE gps_points SET updated_at = ? WHERE name LIKE ? AND updated_at IS NULL",
                (time.time(), sync_api.LIVE_POSITION_PREFIX + "%"),
            )
        # deleted_at: a "trash" for vault GPS points — delete_gps_point,
        # remove_vectors_by_indices and upsert_and_trash_gps_points's
        # deletions all set this instead of actually deleting the row, so a point
        # stays recoverable (and can't be silently re-added by another
        # player's stale local list still containing it — see
        # upsert_and_trash_gps_points) until /empty_trash permanently erases
        # every trashed row in a channel. NULL = active; every read path
        # (load_vectors, find_duplicate_coords, ...) filters it out.
        if "deleted_at" not in existing:
            conn.execute("ALTER TABLE gps_points ADD COLUMN deleted_at REAL")
        conn.execute("""
            CREATE TABLE IF NOT EXISTS guild_bindings (
                guild_id   INTEGER NOT NULL,
                channel_id INTEGER NOT NULL,
                PRIMARY KEY (guild_id, channel_id)
            )
        """)
        # Migrate old single-channel-per-guild schema if present.
        old = conn.execute(
            "SELECT sql FROM sqlite_master WHERE type='table' AND name='guild_bindings'"
        ).fetchone()
        if old and "PRIMARY KEY (guild_id, channel_id)" not in old["sql"]:
            conn.execute("ALTER TABLE guild_bindings RENAME TO guild_bindings_old")
            conn.execute("""
                CREATE TABLE guild_bindings (
                    guild_id   INTEGER NOT NULL,
                    channel_id INTEGER NOT NULL,
                    PRIMARY KEY (guild_id, channel_id)
                )
            """)
            conn.execute("INSERT OR IGNORE INTO guild_bindings SELECT guild_id, channel_id FROM guild_bindings_old")
            conn.execute("DROP TABLE guild_bindings_old")
        conn.execute("""
            CREATE TABLE IF NOT EXISTS migrated_files (
                path       TEXT PRIMARY KEY,
                migrated_at DATETIME DEFAULT CURRENT_TIMESTAMP
            )
        """)
        conn.execute("""
            CREATE TABLE IF NOT EXISTS sync_tokens (
                token      TEXT PRIMARY KEY,
                guild_id   INTEGER NOT NULL,
                channel_id INTEGER NOT NULL,
                label      TEXT,
                created_at DATETIME DEFAULT CURRENT_TIMESTAMP
            )
        """)
        conn.execute("""
            CREATE INDEX IF NOT EXISTS idx_sync_tokens_channel
            ON sync_tokens (guild_id, channel_id)
        """)
        # Durable per-player record of who has ever synced into a channel via
        # a sync token — unlike position_history/gps_points (pruned down to
        # the replay/retention window), this is never pruned, so the web
        # dashboard's Players tab can show "last seen 3 weeks ago" for a
        # player who stopped playing. token_label is a snapshot of the
        # token's label as of that player's most recent sync (refreshed each
        # time), so a renamed label stays current while the token is still
        # alive, and a revoked token's last-known label is still shown.
        conn.execute("""
            CREATE TABLE IF NOT EXISTS known_players (
                guild_id      INTEGER NOT NULL,
                channel_id    INTEGER NOT NULL,
                player_name   TEXT    NOT NULL,
                token         TEXT,
                token_label   TEXT,
                first_seen_at REAL    NOT NULL,
                last_seen_at  REAL    NOT NULL,
                PRIMARY KEY (guild_id, channel_id, player_name)
            )
        """)
        conn.execute("""
            CREATE TABLE IF NOT EXISTS position_history (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                guild_id    INTEGER NOT NULL,
                channel_id  INTEGER NOT NULL,
                player_name TEXT    NOT NULL,
                x           REAL    NOT NULL,
                y           REAL    NOT NULL,
                z           REAL    NOT NULL,
                vx          REAL    NOT NULL DEFAULT 0,
                vy          REAL    NOT NULL DEFAULT 0,
                vz          REAL    NOT NULL DEFAULT 0,
                ax          REAL    NOT NULL DEFAULT 0,
                ay          REAL    NOT NULL DEFAULT 0,
                az          REAL    NOT NULL DEFAULT 0,
                locked           INTEGER NOT NULL DEFAULT 0,
                lock_target_name TEXT,
                lock_target_x    REAL    NOT NULL DEFAULT 0,
                lock_target_y    REAL    NOT NULL DEFAULT 0,
                lock_target_z    REAL    NOT NULL DEFAULT 0,
                lock_distance    REAL    NOT NULL DEFAULT 0,
                recorded_at REAL    NOT NULL
            )
        """)
        # Add acceleration columns if upgrading from a pre-acceleration schema.
        existing_ph_cols = {row["name"] for row in conn.execute("PRAGMA table_info(position_history)")}
        for col in ("ax", "ay", "az"):
            if col not in existing_ph_cols:
                conn.execute(f"ALTER TABLE position_history ADD COLUMN {col} REAL NOT NULL DEFAULT 0")
        # Gravity at the sample (gx/gy/gz, m/s^2) and the controlled grid's
        # angular velocity (wx/wy/wz, rad/s) — for path prediction that falls
        # under gravity and follows a turning ship (see sync_api._predict_path).
        for col in ("gx", "gy", "gz", "wx", "wy", "wz"):
            if col not in existing_ph_cols:
                conn.execute(f"ALTER TABLE position_history ADD COLUMN {col} REAL NOT NULL DEFAULT 0")
        # Weapon-lock columns (MyTargetLockingComponent — the player's OWN
        # lock on something, via a lock-capable weapon like the rocket
        # launcher). Nullable/zero when not locked.
        if "locked" not in existing_ph_cols:
            conn.execute("ALTER TABLE position_history ADD COLUMN locked INTEGER NOT NULL DEFAULT 0")
        if "lock_target_name" not in existing_ph_cols:
            conn.execute("ALTER TABLE position_history ADD COLUMN lock_target_name TEXT")
        for col in ("lock_target_x", "lock_target_y", "lock_target_z", "lock_distance"):
            if col not in existing_ph_cols:
                conn.execute(f"ALTER TABLE position_history ADD COLUMN {col} REAL NOT NULL DEFAULT 0")
        conn.execute("""
            CREATE INDEX IF NOT EXISTS idx_position_history_lookup
            ON position_history (guild_id, channel_id, player_name, recorded_at)
        """)
        conn.execute("""
            CREATE TABLE IF NOT EXISTS planets (
                guild_id   INTEGER NOT NULL,
                channel_id INTEGER NOT NULL,
                name       TEXT    NOT NULL,
                x          REAL    NOT NULL,
                y          REAL    NOT NULL,
                z          REAL    NOT NULL,
                radius     REAL    NOT NULL,
                updated_at REAL    NOT NULL,
                PRIMARY KEY (guild_id, channel_id, name)
            )
        """)
        # Same shape as planets — irregular voxel bodies reported with a
        # bounding-sphere radius (see the plugin's GameStateReader.
        # ReadAsteroids) rather than planets' precise physical radius, but
        # "just a big static sphere on the map" either way.
        conn.execute("""
            CREATE TABLE IF NOT EXISTS asteroids (
                guild_id   INTEGER NOT NULL,
                channel_id INTEGER NOT NULL,
                name       TEXT    NOT NULL,
                x          REAL    NOT NULL,
                y          REAL    NOT NULL,
                z          REAL    NOT NULL,
                radius     REAL    NOT NULL,
                updated_at REAL    NOT NULL,
                PRIMARY KEY (guild_id, channel_id, name)
            )
        """)
        # An asteroid's real shape, read straight from voxel geometry by the
        # plugin's AsteroidShape instead of approximated as a sphere (see
        # the `asteroids` table above): center_x/y/z + mesh_scale + the
        # plugin's base64 vertex/index blobs. cell_size/cells are leftovers
        # from an earlier format and stay NOT NULL in existing databases, so
        # rows just write placeholders (0.0 / '[]') into them.
        # Keyed by voxel_name only (never more than one shape per asteroid);
        # the plugin periodically re-scans and re-sends it to pick up mining
        # changes.
        # Planet terrain, streamed as cube-sphere LOD tiles (see the plugin's
        # TerrainTiles.cs and templates/map.html): one row per tile, data being
        # the plugin's base64 65x65 uint16 samples scaled between vmin and vmax.
        # Planets are static, so a tile is cached until manually cleared.
        conn.execute("""
            CREATE TABLE IF NOT EXISTS terrain_tiles (
                guild_id   INTEGER NOT NULL,
                channel_id INTEGER NOT NULL,
                planet     TEXT    NOT NULL,
                face       INTEGER NOT NULL,
                level      INTEGER NOT NULL,
                tx         INTEGER NOT NULL,
                ty         INTEGER NOT NULL,
                vmin       REAL    NOT NULL,
                vmax       REAL    NOT NULL,
                data       TEXT    NOT NULL,
                updated_at REAL    NOT NULL,
                PRIMARY KEY (guild_id, channel_id, planet, face, level, tx, ty)
            )
        """)
        conn.execute("""
            CREATE TABLE IF NOT EXISTS asteroid_shapes (
                guild_id   INTEGER NOT NULL,
                channel_id INTEGER NOT NULL,
                voxel_name TEXT    NOT NULL,
                format     TEXT    NOT NULL DEFAULT 'cells',
                cell_size  REAL    NOT NULL,
                cells      TEXT    NOT NULL,
                center_x   REAL,
                center_y   REAL,
                center_z   REAL,
                mesh_scale REAL,
                vertices   TEXT,
                indices    TEXT,
                updated_at REAL    NOT NULL,
                PRIMARY KEY (guild_id, channel_id, voxel_name)
            )
        """)
        existing_shape_cols = {row["name"] for row in conn.execute("PRAGMA table_info(asteroid_shapes)")}
        for col, ddl in [
            ("format", "TEXT NOT NULL DEFAULT 'cells'"),
            ("center_x", "REAL"),
            ("center_y", "REAL"),
            ("center_z", "REAL"),
            ("mesh_scale", "REAL"),
            ("vertices", "TEXT"),
            ("indices", "TEXT"),
        ]:
            if col not in existing_shape_cols:
                conn.execute(f"ALTER TABLE asteroid_shapes ADD COLUMN {col} {ddl}")
        # Migrate an older install's per-ore-type rows (deposit_id was
        # "voxel|ore|bucket", one row per vein, no `name`/`ores` columns) to
        # the new one-row-per-body shape below. Those old rows are keyed
        # wrong for the new upsert, so they're just stale noise now — drop
        # the table once; the plugin repopulates it in the new shape on its
        # next sync.
        if conn.execute(
            "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'ore_deposits'"
        ).fetchone():
            existing_ore_cols = {row["name"] for row in conn.execute("PRAGMA table_info(ore_deposits)")}
            if "name" not in existing_ore_cols:
                conn.execute("DROP TABLE ore_deposits")
        # Ore-detector deposits, accumulated rather than expired: a detector
        # only sees ~150m, so the value is in remembering everything anyone
        # has ever scanned. deposit_id is the voxel body's name (see
        # OreDeposits.ReadOreDeposits), so there's never more than one row
        # per asteroid/planet — every ore type found on it is folded into
        # `name`/`ores` instead of getting its own row. `locations` (JSON
        # array of {ore, x, y, z}) preserves an asteroid's individual vein
        # positions that were folded into that one row's x/y/z average —
        # always '[]' for a planet's area-level rows, which are already
        # granular enough on their own (see OreDeposits.ReadOreDeposits).
        conn.execute("""
            CREATE TABLE IF NOT EXISTS ore_deposits (
                guild_id   INTEGER NOT NULL,
                channel_id INTEGER NOT NULL,
                deposit_id TEXT    NOT NULL,
                name       TEXT    NOT NULL,
                ores       TEXT    NOT NULL,
                voxel_name TEXT    NOT NULL,
                is_planet  INTEGER NOT NULL DEFAULT 0,
                x          REAL    NOT NULL,
                y          REAL    NOT NULL,
                z          REAL    NOT NULL,
                locations  TEXT    NOT NULL DEFAULT '[]',
                updated_at REAL    NOT NULL,
                PRIMARY KEY (guild_id, channel_id, deposit_id)
            )
        """)
        existing_ore_cols2 = {row["name"] for row in conn.execute("PRAGMA table_info(ore_deposits)")}
        if "locations" not in existing_ore_cols2:
            conn.execute("ALTER TABLE ore_deposits ADD COLUMN locations TEXT NOT NULL DEFAULT '[]'")
        # Current known hostile signals (antenna/sensor-detected enemy grids),
        # separate from gps_points since these are ephemeral radar telemetry,
        # not vault waypoints — never editable via /revise, never pushed into
        # a player's in-game GPS list. signal_id is the game's stable EntityId
        # (display names commonly collide across pirate/NPC spawns).
        conn.execute("""
            CREATE TABLE IF NOT EXISTS hostile_signals (
                guild_id   INTEGER NOT NULL,
                channel_id INTEGER NOT NULL,
                signal_id  INTEGER NOT NULL,
                name       TEXT    NOT NULL,
                x          REAL    NOT NULL,
                y          REAL    NOT NULL,
                z          REAL    NOT NULL,
                color      TEXT    NOT NULL DEFAULT '#FFFF3030',
                category   TEXT    NOT NULL DEFAULT 'enemy',
                color_override INTEGER NOT NULL DEFAULT 0,
                updated_at REAL    NOT NULL,
                PRIMARY KEY (guild_id, channel_id, signal_id)
            )
        """)
        # Migrate in the category column for installs that predate it.
        existing_hs_cols = {row["name"] for row in conn.execute("PRAGMA table_info(hostile_signals)")}
        if "category" not in existing_hs_cols:
            conn.execute("ALTER TABLE hostile_signals ADD COLUMN category TEXT NOT NULL DEFAULT 'enemy'")
        # color_override: a manually (re)colored signal keeps its own color
        # instead of being recolored to match its category's admin-configured
        # color every time get_hostile_signals reads it — see edit_hostile_signal_color.
        if "color_override" not in existing_hs_cols:
            conn.execute("ALTER TABLE hostile_signals ADD COLUMN color_override INTEGER NOT NULL DEFAULT 0")
        # radius: uncertainty radius (meters) the Pulsar plugin variant fuzzes
        # a neutral/enemy contact's reported position within — NULL for every
        # other signal/plugin variant, whose position is exact.
        # is_static: the reporting grid's IMyCubeGrid.IsStatic — lets the map
        # draw a station icon vs. a ship icon (see get_hostile_signals).
        if "radius" not in existing_hs_cols:
            conn.execute("ALTER TABLE hostile_signals ADD COLUMN radius REAL")
        if "is_static" not in existing_hs_cols:
            conn.execute("ALTER TABLE hostile_signals ADD COLUMN is_static INTEGER NOT NULL DEFAULT 0")
        # threat: WeaponCore threat rating from a reporting player's ship's
        # threat list. dps: WeaponCore optimal DPS of the grid (Mindrazer
        # plugin only). Both NULL when WeaponCore isn't in play.
        if "threat" not in existing_hs_cols:
            conn.execute("ALTER TABLE hostile_signals ADD COLUMN threat REAL")
        if "dps" not in existing_hs_cols:
            conn.execute("ALTER TABLE hostile_signals ADD COLUMN dps REAL")
        conn.execute("""
            CREATE TABLE IF NOT EXISTS hostile_signal_history (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                guild_id    INTEGER NOT NULL,
                channel_id  INTEGER NOT NULL,
                signal_id   INTEGER NOT NULL,
                x           REAL    NOT NULL,
                y           REAL    NOT NULL,
                z           REAL    NOT NULL,
                vx          REAL    NOT NULL DEFAULT 0,
                vy          REAL    NOT NULL DEFAULT 0,
                vz          REAL    NOT NULL DEFAULT 0,
                ax          REAL    NOT NULL DEFAULT 0,
                ay          REAL    NOT NULL DEFAULT 0,
                az          REAL    NOT NULL DEFAULT 0,
                recorded_at REAL    NOT NULL
            )
        """)
        conn.execute("""
            CREATE INDEX IF NOT EXISTS idx_hostile_signal_history_lookup
            ON hostile_signal_history (guild_id, channel_id, signal_id, recorded_at)
        """)
        # name/category: denormalized off hostile_signals at record time, so
        # a signal's history survives (and stays labeled/colorable) after its
        # hostile_signals row itself is pruned or the signal is dismissed —
        # see record_hostile_signal_history/get_replay_snapshot. NULL on rows
        # written before this column existed; get_replay_snapshot falls back
        # to the current hostile_signals row for those.
        existing_hsh_cols = {row["name"] for row in conn.execute("PRAGMA table_info(hostile_signal_history)")}
        for col in ("gx", "gy", "gz", "wx", "wy", "wz"):
            if col not in existing_hsh_cols:
                conn.execute(f"ALTER TABLE hostile_signal_history ADD COLUMN {col} REAL NOT NULL DEFAULT 0")
        if "name" not in existing_hsh_cols:
            conn.execute("ALTER TABLE hostile_signal_history ADD COLUMN name TEXT")
        if "category" not in existing_hsh_cols:
            conn.execute("ALTER TABLE hostile_signal_history ADD COLUMN category TEXT")
        # Per-channel admin overrides for signal category enabled/color —
        # see get_category_settings/set_category_setting. No row means "use
        # the default" (CATEGORY_SETTING_DEFAULTS below), so a channel that's
        # never touched this has no rows at all.
        conn.execute("""
            CREATE TABLE IF NOT EXISTS signal_category_settings (
                guild_id   INTEGER NOT NULL,
                channel_id INTEGER NOT NULL,
                category   TEXT    NOT NULL,
                enabled    INTEGER NOT NULL,
                color      TEXT    NOT NULL,
                retention_seconds INTEGER,
                trail_seconds INTEGER,
                prediction_seconds INTEGER,
                PRIMARY KEY (guild_id, channel_id, category)
            )
        """)
        # Add retention_seconds/trail_seconds/prediction_seconds if upgrading
        # from an older schema. NULL (not a hardcoded number) so
        # get_category_settings falls back to that category's
        # CATEGORY_SETTING_DEFAULTS value rather than freezing every existing
        # row at whatever the old fixed constant used to be.
        existing_scs_cols = {row["name"] for row in conn.execute("PRAGMA table_info(signal_category_settings)")}
        for _col in ("retention_seconds", "trail_seconds", "prediction_seconds"):
            if _col not in existing_scs_cols:
                conn.execute(f"ALTER TABLE signal_category_settings ADD COLUMN {_col} INTEGER")


def migrate_legacy_files(root_dir="./guild_data"):
    """Import any legacy ./guild_data/{guild_id}/{channel_id}_GPS_Data.txt
    files into the SQLite database. Each file is imported at most once,
    tracked via the migrated_files table."""
    if not os.path.isdir(root_dir):
        return

    imported_files = 0
    imported_points = 0
    for guild_name in os.listdir(root_dir):
        guild_path = os.path.join(root_dir, guild_name)
        if not os.path.isdir(guild_path):
            continue
        try:
            guild_id = int(guild_name)
        except ValueError:
            continue

        for fname in os.listdir(guild_path):
            if not fname.endswith("_GPS_Data.txt"):
                continue
            channel_str = fname[:-len("_GPS_Data.txt")]
            try:
                channel_id = int(channel_str)
            except ValueError:
                continue

            full_path = os.path.abspath(os.path.join(guild_path, fname))

            with db() as conn:
                already = conn.execute(
                    "SELECT 1 FROM migrated_files WHERE path = ?",
                    (full_path,),
                ).fetchone()
                if already:
                    continue

                rows = []
                try:
                    with open(full_path, "r") as f:
                        for line in f:
                            line = line.strip()
                            if not line:
                                continue
                            try:
                                v = parse_gps_data(line)
                                rows.append((guild_id, channel_id, v.name, v.x, v.y, v.z, v.color))
                            except Exception as e:
                                print(f"[migrate] skipping bad line in {full_path}: {e}")
                except OSError as e:
                    print(f"[migrate] could not read {full_path}: {e}")
                    continue

                if rows:
                    conn.executemany(
                        "INSERT INTO gps_points (guild_id, channel_id, name, x, y, z, color) "
                        "VALUES (?, ?, ?, ?, ?, ?, ?)",
                        rows,
                    )
                conn.execute(
                    "INSERT INTO migrated_files (path) VALUES (?)",
                    (full_path,),
                )
                imported_files += 1
                imported_points += len(rows)
                print(f"[migrate] imported {len(rows)} points from {full_path}")

    if imported_files:
        print(f"[migrate] done: {imported_points} points across {imported_files} file(s)")


def load_vectors(guild_id, channel_id):
    with db() as conn:
        rows = conn.execute(
            "SELECT id, name, x, y, z, color FROM gps_points "
            "WHERE guild_id = ? AND channel_id = ? AND deleted_at IS NULL ORDER BY id",
            (guild_id, channel_id),
        ).fetchall()
    return [(i + 1, r["id"], Vector3D(r["name"], r["x"], r["y"], r["z"], r["color"]))
            for i, r in enumerate(rows)]


def get_vault_points_as_of(guild_id, channel_id, ts):
    """Same shape as load_vectors, but for the replay scrubber (see
    sync_api.py's map_replay): a point deleted AFTER `ts` is still included
    (deleted_at > ts means it was still present as of that moment), so
    scrubbing to a time before a deletion shows the point again instead of
    it staying gone just because it's gone NOW. A point deleted at/before
    `ts` is excluded, same as load_vectors excludes anything currently
    trashed. There's no gps_points creation timestamp to also suppress a
    point that didn't exist yet as of `ts` (only updated_at, which a later
    edit can bump well past its real creation time) — this only restores
    deletions, it doesn't reconstruct "didn't exist yet"."""
    with db() as conn:
        rows = conn.execute(
            "SELECT id, name, x, y, z, color FROM gps_points "
            "WHERE guild_id = ? AND channel_id = ? AND (deleted_at IS NULL OR deleted_at > ?) ORDER BY id",
            (guild_id, channel_id, ts),
        ).fetchall()
    return [(i + 1, r["id"], Vector3D(r["name"], r["x"], r["y"], r["z"], r["color"]))
            for i, r in enumerate(rows)]


def get_player_marker_override_names(guild_id, channel_id):
    """Names of live position markers (LIVE_POSITION_PREFIX) that have a
    manually-set color override (see edit_gps_point) — used by
    apply_player_category_settings in sync_api.py to skip recoloring those
    to the "player" category's admin-set color."""
    with db() as conn:
        rows = conn.execute(
            "SELECT name FROM gps_points WHERE guild_id = ? AND channel_id = ? "
            "AND color_override = 1 AND name LIKE ? AND deleted_at IS NULL",
            (guild_id, channel_id, sync_api.LIVE_POSITION_PREFIX + "%"),
        ).fetchall()
    return {r["name"] for r in rows}


def prune_stale_player_markers(guild_id, channel_id, retention_seconds):
    """Deletes live position markers (◆ prefix) not synced within
    retention_seconds — the "player" category's admin-configured retention
    (see CATEGORY_SETTING_DEFAULTS/get_category_settings). Unlike hostile
    signals, live markers had no expiry at all before this — a player who
    stopped syncing used to linger on the map forever. Never touches vault
    GPS points (added via /add_gps etc.), regardless of their updated_at.
    Returns the set of deleted marker names, so a caller holding an
    already-loaded points list can drop them from that too without a second
    query (see apply_player_category_settings in sync_api.py).

    Also sweeps position_history down to get_replay_retention_seconds() —
    record_position_history only prunes a player's OWN rows on their next
    sync, so a player who stops syncing entirely would otherwise leave their
    history past the replay window forever; this (called on every map
    view/poll, same lazy "no background job needed" pattern as
    get_hostile_signals) is what actually catches that, for every player in
    the channel, not just whichever one is currently over retention_seconds."""
    cutoff = time.time() - retention_seconds
    replay_cutoff = time.time() - get_replay_retention_seconds()
    with db() as conn:
        rows = conn.execute(
            "SELECT name FROM gps_points WHERE guild_id = ? AND channel_id = ? "
            "AND name LIKE ? AND updated_at < ?",
            (guild_id, channel_id, sync_api.LIVE_POSITION_PREFIX + "%", cutoff),
        ).fetchall()
        stale_names = {r["name"] for r in rows}
        if stale_names:
            placeholders = ",".join("?" * len(stale_names))
            conn.execute(
                f"DELETE FROM gps_points WHERE guild_id = ? AND channel_id = ? AND name IN ({placeholders})",
                (guild_id, channel_id, *stale_names),
            )
        conn.execute(
            "DELETE FROM position_history WHERE guild_id = ? AND channel_id = ? AND recorded_at < ?",
            (guild_id, channel_id, replay_cutoff),
        )
    return stale_names


def find_duplicate_coords(guild_id, channel_id, vector):
    with db() as conn:
        row = conn.execute(
            "SELECT name FROM gps_points WHERE guild_id = ? AND channel_id = ? "
            "AND x = ? AND y = ? AND z = ? AND deleted_at IS NULL",
            (guild_id, channel_id, vector.x, vector.y, vector.z),
        ).fetchone()
    return row["name"] if row else None


def add_vector(guild_id, channel_id, vector):
    with db() as conn:
        conn.execute(
            "INSERT INTO gps_points (guild_id, channel_id, name, x, y, z, color) "
            "VALUES (?, ?, ?, ?, ?, ?, ?)",
            (guild_id, channel_id, vector.name, vector.x, vector.y, vector.z, vector.color),
        )


def upsert_gps_point(guild_id, channel_id, vector):
    """Insert a GPS point, or update its coordinates/color in place if a point
    with the same name already exists in this channel. Used by the plugin sync
    API, where re-syncing the same waypoint (or a moving live-position marker)
    must not accumulate duplicate rows. Leaves `color` alone for a point a
    viewer has manually recolored (color_override — see edit_gps_point),
    otherwise every position sync would immediately clobber it back.

    Only ever updates an active (not trashed) row: this is also the live
    position sync path, and a live marker can end up trashed by mistake (its
    name/index looks like any other point to remove_vectors_by_indices).
    Unlike a real vault GPS point, a live marker isn't something a player
    deliberately deleted — filtering deleted_at out here means the very next
    position sync just inserts it fresh again instead of silently updating
    the invisible trashed row underneath it, which would otherwise leave the
    player missing from the map until it aged out naturally."""
    now = time.time()
    with db() as conn:
        cur = conn.execute(
            "UPDATE gps_points SET x = ?, y = ?, z = ?, updated_at = ?, "
            "color = CASE WHEN color_override = 1 THEN color ELSE ? END "
            "WHERE guild_id = ? AND channel_id = ? AND name = ? AND deleted_at IS NULL",
            (vector.x, vector.y, vector.z, now, vector.color, guild_id, channel_id, vector.name),
        )
        if cur.rowcount == 0:
            conn.execute(
                "INSERT INTO gps_points (guild_id, channel_id, name, x, y, z, color, updated_at) "
                "VALUES (?, ?, ?, ?, ?, ?, ?, ?)",
                (guild_id, channel_id, vector.name, vector.x, vector.y, vector.z, vector.color, now),
            )


def upsert_and_trash_gps_points(guild_id, channel_id, vectors, deleted_names):
    """Upsert every given point, then trash (see delete_gps_point) exactly
    the vault points named in deleted_names — the ones this player had
    synced and has since deleted in-game — staying recoverable until
    /empty_trash actually erases them. Live position markers (◆ prefix) are
    never touched here; they're managed separately by
    record_position_history/upsert via /sync/position.

    Deliberately NOT "trash whatever's missing from vectors": with more than
    one player syncing the same channel, a push from a player who hasn't
    pulled lately (e.g. right after joining) would otherwise trash every
    point the others added since — and since a trashed name is never
    re-inserted (below), those points would be gone for good from everyone's
    sync. deleted_names may be empty (an upsert-only push).

    A trashed point is never resurrected by this: a point one player still
    has locally (because they haven't pulled the deletion yet) is skipped
    rather than upserted, so their stale push can't silently bring a deleted
    point back — it stays trashed until /empty_trash erases it for good."""
    live_prefix_pattern = sync_api.LIVE_POSITION_PREFIX + "%"
    now = time.time()
    deleted = 0
    with db() as conn:
        for v in vectors:
            cur = conn.execute(
                "UPDATE gps_points SET x = ?, y = ?, z = ?, color = ? "
                "WHERE guild_id = ? AND channel_id = ? AND name = ? AND deleted_at IS NULL",
                (v.x, v.y, v.z, v.color, guild_id, channel_id, v.name),
            )
            if cur.rowcount == 0:
                trashed = conn.execute(
                    "SELECT 1 FROM gps_points WHERE guild_id = ? AND channel_id = ? "
                    "AND name = ? AND deleted_at IS NOT NULL",
                    (guild_id, channel_id, v.name),
                ).fetchone()
                if trashed:
                    continue
                conn.execute(
                    "INSERT INTO gps_points (guild_id, channel_id, name, x, y, z, color) "
                    "VALUES (?, ?, ?, ?, ?, ?, ?)",
                    (guild_id, channel_id, v.name, v.x, v.y, v.z, v.color),
                )

        # A name both pushed and listed as deleted is a client bug — keep it.
        pushed = {v.name for v in vectors}
        for name in set(deleted_names) - pushed:
            cur = conn.execute(
                "UPDATE gps_points SET deleted_at = ? WHERE guild_id = ? AND channel_id = ? "
                "AND name = ? AND deleted_at IS NULL AND name NOT LIKE ?",
                (now, guild_id, channel_id, name, live_prefix_pattern),
            )
            deleted += cur.rowcount
    return deleted


def get_replay_retention_seconds():
    """How far back the web map's replay scrubber can rewind, in seconds —
    bot_config.json's "replay_hours" (default 24), reloaded from disk on
    every call like the rest of bot_config-backed settings (see
    sync_api.load_config). This is the FLOOR every position_history/
    hostile_signal_history prune uses (record_position_history,
    record_hostile_signal_history, prune_stale_player_markers,
    get_hostile_signals) — trail_seconds still governs what's visible as a
    live/replay trail line (get_position_trail/get_hostile_signal_trail),
    this just keeps the raw samples around long enough to scrub back to."""
    hours = bot_config.load().get("replay_hours") or 24
    seconds = int(float(hours) * 3600)
    return max(MIN_CATEGORY_RETENTION_SECONDS, min(seconds, MAX_CATEGORY_RETENTION_SECONDS))


# Historical default, kept only as record_position_history's fallback
# parameter value — actual trail retention is per-category and configurable
# now (see CATEGORY_SETTING_DEFAULTS' trail_seconds / get_category_settings);
# callers should pass the "player" category's trail_seconds explicitly.
POSITION_HISTORY_RETENTION_SECONDS = 600  # matches DEFAULT_TRAIL_SECONDS, defined later in this file

# Cap on how many samples a rendered trail line ever carries, independent of
# how long trail_seconds is configured for. A long trail_seconds at the
# plugin's default 5s tick can accumulate thousands of samples — with many
# signals on screen at once that's a lot of points being fetched from the
# DB, sent over the wire, and rebuilt into a Three.js line geometry on every
# ~3s map poll, which is what actually causes the map to lag/struggle, not
# the marker count itself. Only the most recent TRAIL_DISPLAY_LIMIT samples
# are ever returned for display; retention (whether the signal/player exists
# at all) is unaffected, and is a separate, larger *_RETENTION_SECONDS-style
# window from trail_seconds — see get_hostile_signals/
# prune_stale_player_markers for that.
TRAIL_DISPLAY_LIMIT = 1000  # a trail_seconds set well beyond this still only shows its most recent slice


def record_position_history(guild_id, channel_id, player_name, x, y, z, vx, vy, vz, ax=0.0, ay=0.0, az=0.0,
                             locked=False, lock_target_name=None, lock_target_x=0.0, lock_target_y=0.0,
                             lock_target_z=0.0, lock_distance=0.0, retention_seconds=POSITION_HISTORY_RETENTION_SECONDS,
                             gx=0.0, gy=0.0, gz=0.0, wx=0.0, wy=0.0, wz=0.0):
    """Append a position sample for a live player and prune anything older
    than retention_seconds (the "player" category's configured trail_seconds
    — see CATEGORY_SETTING_DEFAULTS/get_category_settings), so the map can
    draw a recent movement trail. Acceleration is the plugin's real
    engine-computed value (whatever's actually controlled — grid physics
    when piloting, character physics when on foot), not estimated from
    consecutive samples here. locked/lock_* are the player's OWN weapon lock
    (MyTargetLockingComponent) at that moment — not whether anyone has
    locked onto the player, which the game doesn't track at all.

    The prune cutoff is never shorter than get_replay_retention_seconds(),
    regardless of how short retention_seconds (trail_seconds) is configured
    — trail_seconds only controls what get_position_trail returns for the
    live/replay trail line, not how long raw samples survive for the replay
    scrubber to rewind through (see get_replay_snapshot)."""
    now = time.time()
    cutoff = now - max(retention_seconds, get_replay_retention_seconds())
    with db() as conn:
        conn.execute(
            "INSERT INTO position_history "
            "(guild_id, channel_id, player_name, x, y, z, vx, vy, vz, ax, ay, az, "
            "locked, lock_target_name, lock_target_x, lock_target_y, lock_target_z, lock_distance, recorded_at, "
            "gx, gy, gz, wx, wy, wz) "
            "VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
            (guild_id, channel_id, player_name, x, y, z, vx, vy, vz, ax, ay, az,
             1 if locked else 0, lock_target_name, lock_target_x, lock_target_y, lock_target_z, lock_distance, now,
             gx, gy, gz, wx, wy, wz),
        )
        conn.execute(
            "DELETE FROM position_history "
            "WHERE guild_id = ? AND channel_id = ? AND player_name = ? AND recorded_at < ?",
            (guild_id, channel_id, player_name, cutoff),
        )


def get_position_trail(guild_id, channel_id, player_name, retention_seconds=POSITION_HISTORY_RETENTION_SECONDS):
    """Most recent position samples for a player within retention_seconds
    (the "player" category's configured trail_seconds; capped at
    TRAIL_DISPLAY_LIMIT — see its comment), oldest first."""
    cutoff = time.time() - retention_seconds
    with db() as conn:
        rows = conn.execute(
            "SELECT x, y, z, vx, vy, vz, ax, ay, az, gx, gy, gz, wx, wy, wz, "
            "locked, lock_target_name, lock_target_x, lock_target_y, lock_target_z, lock_distance, recorded_at "
            "FROM position_history "
            "WHERE guild_id = ? AND channel_id = ? AND player_name = ? AND recorded_at >= ? "
            "ORDER BY recorded_at DESC LIMIT ?",
            (guild_id, channel_id, player_name, cutoff, TRAIL_DISPLAY_LIMIT),
        ).fetchall()
    return list(reversed(rows))


ACTIVE_PLAYER_WINDOW_SECONDS = 300  # 5 minutes — matches the web map's "ACTIVE" count


def get_active_live_players(guild_id, channel_id, exclude_name=None):
    """Each currently-active player's latest known position in this channel
    (last reported within ACTIVE_PLAYER_WINDOW_SECONDS), optionally excluding
    one name. Used by the plugin's /vault players ("/vp") command."""
    cutoff = time.time() - ACTIVE_PLAYER_WINDOW_SECONDS
    with db() as conn:
        rows = conn.execute(
            "SELECT player_name, x, y, z, recorded_at FROM position_history "
            "WHERE guild_id = ? AND channel_id = ? AND recorded_at >= ? "
            "ORDER BY recorded_at DESC",
            (guild_id, channel_id, cutoff),
        ).fetchall()

    latest = {}
    for r in rows:
        if exclude_name is not None and r["player_name"] == exclude_name:
            continue
        if r["player_name"] not in latest:
            latest[r["player_name"]] = r
    return list(latest.values())


def upsert_planets(guild_id, channel_id, planets):
    """Upsert each {name, x, y, z, radius} by name. Planets are effectively
    static, so this is just a plain upsert (no deletion-on-absence like GPS
    points) — a planet the plugin doesn't currently report just goes stale
    rather than vanishing from the map."""
    now = time.time()
    with db() as conn:
        for p in planets:
            conn.execute(
                "INSERT INTO planets (guild_id, channel_id, name, x, y, z, radius, updated_at) "
                "VALUES (?, ?, ?, ?, ?, ?, ?, ?) "
                "ON CONFLICT (guild_id, channel_id, name) DO UPDATE SET "
                "x = excluded.x, y = excluded.y, z = excluded.z, "
                "radius = excluded.radius, updated_at = excluded.updated_at",
                (guild_id, channel_id, p["name"], p["x"], p["y"], p["z"], p["radius"], now),
            )


def get_planets(guild_id, channel_id):
    with db() as conn:
        rows = conn.execute(
            "SELECT name, x, y, z, radius FROM planets WHERE guild_id = ? AND channel_id = ?",
            (guild_id, channel_id),
        ).fetchall()
    return rows


def upsert_asteroids(guild_id, channel_id, asteroids):
    """Upsert each {name, x, y, z, radius} by name — same shape/semantics as
    upsert_planets (see there for why this is a plain no-deletion upsert)."""
    now = time.time()
    with db() as conn:
        for a in asteroids:
            conn.execute(
                "INSERT INTO asteroids (guild_id, channel_id, name, x, y, z, radius, updated_at) "
                "VALUES (?, ?, ?, ?, ?, ?, ?, ?) "
                "ON CONFLICT (guild_id, channel_id, name) DO UPDATE SET "
                "x = excluded.x, y = excluded.y, z = excluded.z, "
                "radius = excluded.radius, updated_at = excluded.updated_at",
                (guild_id, channel_id, a["name"], a["x"], a["y"], a["z"], a["radius"], now),
            )


def get_asteroids(guild_id, channel_id):
    with db() as conn:
        rows = conn.execute(
            "SELECT name, x, y, z, radius FROM asteroids WHERE guild_id = ? AND channel_id = ?",
            (guild_id, channel_id),
        ).fetchall()
    return rows


def upsert_asteroid_shapes(guild_id, channel_id, shapes):
    """Upsert each shape by voxel_name — never more than one shape row per
    asteroid. Each shape is {voxel_name, center_x/y/z, mesh_scale, vertices,
    indices} — vertices/indices are the plugin's base64 blobs, stored as-is
    and decoded client-side (templates/map.html). Never deletes: an asteroid
    that stops being synced (player left, plugin restarted) just keeps its
    last-known shape."""
    now = time.time()
    with db() as conn:
        for s in shapes:
            conn.execute(
                "INSERT INTO asteroid_shapes "
                "(guild_id, channel_id, voxel_name, format, cell_size, cells, "
                "center_x, center_y, center_z, mesh_scale, vertices, indices, updated_at) "
                "VALUES (?, ?, ?, 'mesh', 0.0, '[]', ?, ?, ?, ?, ?, ?, ?) "
                "ON CONFLICT (guild_id, channel_id, voxel_name) DO UPDATE SET "
                "format = 'mesh', cell_size = 0.0, cells = '[]', "
                "center_x = excluded.center_x, center_y = excluded.center_y, "
                "center_z = excluded.center_z, mesh_scale = excluded.mesh_scale, "
                "vertices = excluded.vertices, indices = excluded.indices, "
                "updated_at = excluded.updated_at",
                (
                    guild_id, channel_id, s["voxel_name"],
                    s["center_x"], s["center_y"], s["center_z"], s["mesh_scale"],
                    s["vertices"], s["indices"], now,
                ),
            )


def get_asteroid_shapes(guild_id, channel_id, embed_meshes=True):
    """Every asteroid shape for the channel. embed_meshes=False leaves the
    (large) mesh vertex/index blobs out and instead flags each row with
    lazy_mesh=True - the live web map fetches those one at a time from
    /map/asteroid_shape (see get_asteroid_shape_mesh) so the page itself
    stays small. The downloadable /map file keeps the default (embedded)."""
    mesh_cols = "vertices, indices" if embed_meshes else "NULL AS vertices, NULL AS indices"
    with db() as conn:
        rows = conn.execute(
            f"SELECT voxel_name, format, center_x, center_y, center_z, mesh_scale, {mesh_cols} "
            "FROM asteroid_shapes WHERE guild_id = ? AND channel_id = ? AND format = 'mesh'",
            (guild_id, channel_id),
        ).fetchall()
    result = []
    for r in rows:
        d = dict(r)
        d["lazy_mesh"] = not embed_meshes
        result.append(d)
    return result


def get_asteroid_shape_mesh(guild_id, channel_id, voxel_name):
    """One shape's heavy blobs, or None: {format:'mesh', mesh_scale, vertices,
    indices}."""
    with db() as conn:
        row = conn.execute(
            "SELECT mesh_scale, vertices, indices FROM asteroid_shapes "
            "WHERE guild_id = ? AND channel_id = ? AND voxel_name = ? AND format = 'mesh'",
            (guild_id, channel_id, voxel_name),
        ).fetchone()
    if not row:
        return None
    return {
        "format": "mesh", "mesh_scale": row["mesh_scale"],
        "vertices": row["vertices"], "indices": row["indices"],
    }


def upsert_terrain_tiles(guild_id, channel_id, tiles):
    """Upsert tiles [{planet, face, level, x, y, vmin, vmax, data}]."""
    now = time.time()
    with db() as conn:
        for t in tiles:
            conn.execute(
                "INSERT INTO terrain_tiles (guild_id, channel_id, planet, face, level, tx, ty, vmin, vmax, data, updated_at) "
                "VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?) "
                "ON CONFLICT (guild_id, channel_id, planet, face, level, tx, ty) DO UPDATE SET "
                "vmin = excluded.vmin, vmax = excluded.vmax, data = excluded.data, updated_at = excluded.updated_at",
                (guild_id, channel_id, t["planet"], t["face"], t["level"], t["x"], t["y"], t["vmin"], t["vmax"], t["data"], now),
            )


def get_terrain_tiles(guild_id, channel_id, planet, keys):
    """The stored tiles among keys [(face, level, x, y)], as {key: {vmin, vmax, data}}."""
    if not keys:
        return {}
    marks = ",".join(["(?, ?, ?, ?)"] * len(keys))
    flat = [v for k in keys for v in k]
    with db() as conn:
        rows = conn.execute(
            "SELECT face, level, tx, ty, vmin, vmax, data FROM terrain_tiles "
            "WHERE guild_id = ? AND channel_id = ? AND planet = ? "
            f"AND (face, level, tx, ty) IN (VALUES {marks})",
            (guild_id, channel_id, planet, *flat),
        ).fetchall()
    return {
        (r["face"], r["level"], r["tx"], r["ty"]): {"vmin": r["vmin"], "vmax": r["vmax"], "data": r["data"]}
        for r in rows
    }


def upsert_ore_deposits(guild_id, channel_id, deposits):
    """Upsert each {deposit_id, name, ores, voxel_name, is_planet, x, y, z,
    locations} by deposit_id — never more than one row per asteroid (see the
    ore_deposits table comment). `locations` is a list of {ore, x, y, z}
    dicts, stored JSON-encoded. Never deletes."""
    now = time.time()
    with db() as conn:
        for d in deposits:
            conn.execute(
                "INSERT INTO ore_deposits "
                "(guild_id, channel_id, deposit_id, name, ores, voxel_name, is_planet, x, y, z, locations, updated_at) "
                "VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?) "
                "ON CONFLICT (guild_id, channel_id, deposit_id) DO UPDATE SET "
                "name = excluded.name, ores = excluded.ores, voxel_name = excluded.voxel_name, "
                "is_planet = excluded.is_planet, "
                "x = excluded.x, y = excluded.y, z = excluded.z, locations = excluded.locations, "
                "updated_at = excluded.updated_at",
                (guild_id, channel_id, d["deposit_id"], d["name"], d["ores"], d["voxel_name"],
                 int(d["is_planet"]), d["x"], d["y"], d["z"], json.dumps(d.get("locations") or []), now),
            )


def get_ore_deposits(guild_id, channel_id):
    with db() as conn:
        rows = conn.execute(
            "SELECT deposit_id, name, ores, voxel_name, is_planet, x, y, z, locations "
            "FROM ore_deposits WHERE guild_id = ? AND channel_id = ?",
            (guild_id, channel_id),
        ).fetchall()
    result = []
    for r in rows:
        d = dict(r)
        try:
            d["locations"] = json.loads(d["locations"])
        except (TypeError, ValueError):
            d["locations"] = []
        result.append(d)
    return result


DEFAULT_HOSTILE_SIGNAL_COLOR = "#FFFF3030"

# Default "last seen" retention per category, in seconds — how long a signal
# (or, for "player", a live position marker) stays on the map after it stops
# being re-reported, before get_hostile_signals/apply_player_category_settings
# prunes it. All admin-editable now (see CATEGORY_SETTING_DEFAULTS below); the
# named constants remain just as documented starting points.
HOSTILE_SIGNAL_RETENTION_SECONDS = 6 * 3600       # enemy/neutral/owned/player default
UNIDENTIFIED_SIGNAL_RETENTION_SECONDS = 5 * 60    # unidentified default — noisy, short-lived
ALLIED_SIGNAL_RETENTION_SECONDS = 12 * 3600       # allied default — worth tracking longer

# Admin-configurable categories (see get_category_settings/set_category_setting
# and signal_category_settings above) — enabled/color/retention_seconds, all
# editable from the map's settings gear icon. "player" governs live position
# markers (◆ prefix, gps_points) rather than a hostile_signals row — see
# apply_player_category_settings/prune_stale_player_markers in sync_api.py.
CATEGORY_SETTING_DEFAULTS = {
    "unidentified": {"enabled": True,  "color": "#FFEDAA38", "retention_seconds": UNIDENTIFIED_SIGNAL_RETENTION_SECONDS},
    "enemy":        {"enabled": True,  "color": "#FFFF3030", "retention_seconds": HOSTILE_SIGNAL_RETENTION_SECONDS},
    "neutral":      {"enabled": True,  "color": "#FFFFCF00", "retention_seconds": HOSTILE_SIGNAL_RETENTION_SECONDS},
    "allied":       {"enabled": True,  "color": "#FF00FF0E", "retention_seconds": ALLIED_SIGNAL_RETENTION_SECONDS},
    "owned":        {"enabled": False, "color": "#FF3080FF", "retention_seconds": HOSTILE_SIGNAL_RETENTION_SECONDS},
    "player":       {"enabled": True,  "color": "#FFFFFFFF", "retention_seconds": HOSTILE_SIGNAL_RETENTION_SECONDS},
}

# trail_seconds/prediction_seconds defaults for every category: 10 minutes,
# matching what the map already effectively showed before these were
# configurable (the old fixed PREDICTION_HORIZON_SECONDS, and — despite the
# old 6h/12h *_RETENTION_SECONDS constants above sounding generous — the old
# hardcoded TRAIL_DISPLAY_LIMIT of 120 rows at a ~5s tick already capped the
# visible trail to ~10 minutes regardless; see TRAIL_DISPLAY_LIMIT below).
DEFAULT_TRAIL_SECONDS = 600
DEFAULT_PREDICTION_SECONDS = 600
for _settings in CATEGORY_SETTING_DEFAULTS.values():
    _settings["trail_seconds"] = DEFAULT_TRAIL_SECONDS
    _settings["prediction_seconds"] = DEFAULT_PREDICTION_SECONDS
del _settings

MIN_CATEGORY_RETENTION_SECONDS = 30      # floor: a setting this low would just thrash prune/re-detect every tick
MAX_CATEGORY_RETENTION_SECONDS = 30 * 24 * 3600  # ceiling: 30 days, generous but bounded
# Same bounds reused for trail_seconds/prediction_seconds — one pair of
# limits is enough to keep any of the three from being 0/negative or
# absurdly huge; TRAIL_DISPLAY_LIMIT (below) is what actually keeps a long
# trail_seconds from ballooning payload size, not this range check.

CATEGORY_SECONDS_FIELDS = ("retention_seconds", "trail_seconds", "prediction_seconds")


def get_category_settings(guild_id, channel_id):
    """{category: {"enabled": bool, "color": "#AARRGGBB", "retention_seconds":
    int, "trail_seconds": int, "prediction_seconds": int}} for the six
    admin-configurable categories, falling back to CATEGORY_SETTING_DEFAULTS
    for any category with no stored row yet, or whose stored row predates
    one of these *_seconds columns (NULL)."""
    settings = {cat: dict(v) for cat, v in CATEGORY_SETTING_DEFAULTS.items()}
    with db() as conn:
        rows = conn.execute(
            "SELECT category, enabled, color, retention_seconds, trail_seconds, prediction_seconds "
            "FROM signal_category_settings WHERE guild_id = ? AND channel_id = ?",
            (guild_id, channel_id),
        ).fetchall()
    for row in rows:
        if row["category"] not in settings:
            continue
        defaults = CATEGORY_SETTING_DEFAULTS[row["category"]]
        settings[row["category"]] = {
            "enabled": bool(row["enabled"]),
            "color": normalize_argb_hex(row["color"]),
            **{f: (row[f] if row[f] is not None else defaults[f]) for f in CATEGORY_SECONDS_FIELDS},
        }
    return settings


def set_category_setting(guild_id, channel_id, category, enabled, color, retention_seconds, trail_seconds, prediction_seconds):
    """Upsert one category's enabled/color/retention_seconds/trail_seconds/
    prediction_seconds. Raises ValueError for an unrecognized category, or
    any of the three *_seconds values outside [MIN_CATEGORY_RETENTION_SECONDS,
    MAX_CATEGORY_RETENTION_SECONDS]."""
    if category not in CATEGORY_SETTING_DEFAULTS:
        raise ValueError(f"'{category}' is not an admin-configurable category")
    values = {
        "retention_seconds": int(retention_seconds),
        "trail_seconds": int(trail_seconds),
        "prediction_seconds": int(prediction_seconds),
    }
    for field, value in values.items():
        if not (MIN_CATEGORY_RETENTION_SECONDS <= value <= MAX_CATEGORY_RETENTION_SECONDS):
            raise ValueError(
                f"{field} must be between {MIN_CATEGORY_RETENTION_SECONDS} and {MAX_CATEGORY_RETENTION_SECONDS}"
            )
    with db() as conn:
        conn.execute(
            "INSERT INTO signal_category_settings "
            "(guild_id, channel_id, category, enabled, color, retention_seconds, trail_seconds, prediction_seconds) "
            "VALUES (?, ?, ?, ?, ?, ?, ?, ?) "
            "ON CONFLICT (guild_id, channel_id, category) DO UPDATE SET "
            "enabled = excluded.enabled, color = excluded.color, "
            "retention_seconds = excluded.retention_seconds, trail_seconds = excluded.trail_seconds, "
            "prediction_seconds = excluded.prediction_seconds",
            (guild_id, channel_id, category, 1 if enabled else 0, normalize_argb_hex(color),
             values["retention_seconds"], values["trail_seconds"], values["prediction_seconds"]),
        )


def upsert_hostile_signal(guild_id, channel_id, signal_id, name, x, y, z, color=None, category=None,
                           radius=None, is_static=False, threat=None, dps=None):
    """Record/refresh a currently-detected hostile signal's latest known
    position. Keyed by signal_id (the game's EntityId), not name — pirate/NPC
    spawns commonly reuse identical display names. Leaves `color` (and its
    color_override flag) alone for a signal a viewer has manually recolored
    via edit_hostile_signal_color — otherwise every plugin re-detection would
    immediately clobber the override back to the category's default color.

    radius: the Pulsar plugin variant's neutral/enemy position-uncertainty
    radius (meters), None from every other signal — lets the map draw an
    uncertainty sphere instead of implying an exact fix (see
    get_hostile_signals/hostile_points_and_extras_for).
    is_static: the reporting grid's IMyCubeGrid.IsStatic — station vs. ship,
    for the map's marker icon.
    threat/dps: WeaponCore threat rating / optimal DPS, None without it."""
    now = time.time()
    with db() as conn:
        conn.execute(
            "INSERT INTO hostile_signals "
            "(guild_id, channel_id, signal_id, name, x, y, z, color, category, radius, is_static, threat, dps, updated_at) "
            "VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?) "
            "ON CONFLICT (guild_id, channel_id, signal_id) DO UPDATE SET "
            "name = excluded.name, x = excluded.x, y = excluded.y, z = excluded.z, "
            "color = CASE WHEN hostile_signals.color_override = 1 THEN hostile_signals.color ELSE excluded.color END, "
            "category = excluded.category, radius = excluded.radius, is_static = excluded.is_static, "
            "threat = excluded.threat, dps = excluded.dps, "
            "updated_at = excluded.updated_at",
            (
                guild_id, channel_id, signal_id, name, x, y, z,
                color or DEFAULT_HOSTILE_SIGNAL_COLOR, category or "enemy",
                radius, 1 if is_static else 0, threat, dps, now,
            ),
        )


def set_hostile_signal_color(guild_id, channel_id, signal_id, color) -> bool:
    """Manually (re)color one signal, overriding its category's admin-set
    color from then on (see upsert_hostile_signal/get_hostile_signals).
    Returns True if a matching signal was found."""
    with db() as conn:
        cur = conn.execute(
            "UPDATE hostile_signals SET color = ?, color_override = 1 "
            "WHERE guild_id = ? AND channel_id = ? AND signal_id = ?",
            (color, guild_id, channel_id, signal_id),
        )
    return cur.rowcount > 0


def record_hostile_signal_history(guild_id, channel_id, signal_id, name, category, x, y, z, vx, vy, vz,
                                   ax=0.0, ay=0.0, az=0.0, retention_seconds=HOSTILE_SIGNAL_RETENTION_SECONDS,
                                   gx=0.0, gy=0.0, gz=0.0, wx=0.0, wy=0.0, wz=0.0):
    """Append a position sample for a hostile signal and prune anything older
    than retention_seconds (that signal's category's configured
    trail_seconds), mirroring record_position_history.

    name/category are denormalized onto the row (not just looked up from
    hostile_signals) so this signal's history stays labeled/colorable by the
    replay scrubber even after its hostile_signals row is gone — see
    get_replay_snapshot. The prune cutoff, like record_position_history's, is
    never shorter than get_replay_retention_seconds() regardless of how
    short retention_seconds is configured."""
    now = time.time()
    cutoff = now - max(retention_seconds, get_replay_retention_seconds())
    with db() as conn:
        conn.execute(
            "INSERT INTO hostile_signal_history "
            "(guild_id, channel_id, signal_id, name, category, x, y, z, vx, vy, vz, ax, ay, az, recorded_at, "
            "gx, gy, gz, wx, wy, wz) "
            "VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
            (guild_id, channel_id, signal_id, name, category, x, y, z, vx, vy, vz, ax, ay, az, now,
             gx, gy, gz, wx, wy, wz),
        )
        conn.execute(
            "DELETE FROM hostile_signal_history "
            "WHERE guild_id = ? AND channel_id = ? AND signal_id = ? AND recorded_at < ?",
            (guild_id, channel_id, signal_id, cutoff),
        )


def get_hostile_signal_trail(guild_id, channel_id, signal_id, retention_seconds=HOSTILE_SIGNAL_RETENTION_SECONDS):
    """Most recent position samples for a hostile signal within
    retention_seconds (that signal's category's configured trail_seconds;
    capped at TRAIL_DISPLAY_LIMIT — see its comment), oldest first — mirrors
    get_position_trail."""
    cutoff = time.time() - retention_seconds
    with db() as conn:
        rows = conn.execute(
            "SELECT x, y, z, vx, vy, vz, ax, ay, az, gx, gy, gz, wx, wy, wz, recorded_at FROM hostile_signal_history "
            "WHERE guild_id = ? AND channel_id = ? AND signal_id = ? AND recorded_at >= ? "
            "ORDER BY recorded_at DESC LIMIT ?",
            (guild_id, channel_id, signal_id, cutoff, TRAIL_DISPLAY_LIMIT),
        ).fetchall()
    return list(reversed(rows))


def get_hostile_signals(guild_id, channel_id):
    """Currently known hostile signals. Anything stale — per its OWN
    category's admin-configured retention_seconds (see
    CATEGORY_SETTING_DEFAULTS/get_category_settings; every category
    including "unidentified" has one now) — is deleted from hostile_signals
    (NOT its history — see below) before returning, so signals clear
    themselves off the map after the plugin stops reporting them (e.g. the
    game was closed), with no background job needed — the next map view
    does the pruning.

    Also applies this channel's admin category settings: a disabled category
    (e.g. "owned", off by default) is dropped from the result entirely — its
    rows stay in the table and keep accumulating history, they're just not
    returned here, so re-enabling the category shows current data
    immediately rather than waiting for the plugin to re-detect everything.

    hostile_signal_history is swept down to get_replay_retention_seconds()
    here (same lazy "next map view does it" pattern), independent of which
    signal_ids are currently stale/enabled — the replay scrubber needs a
    signal's history to keep existing after it goes stale by
    retention_seconds (that's the whole point of replaying "what was here
    earlier"), so history pruning is intentionally decoupled from the
    hostile_signals row's own lifecycle; record_hostile_signal_history
    already enforces the same floor on insert, but a signal that stops being
    reported entirely never inserts again, so this sweep is what actually
    catches it."""
    category_settings = get_category_settings(guild_id, channel_id)
    now = time.time()
    replay_cutoff = now - get_replay_retention_seconds()
    with db() as conn:
        rows = conn.execute(
            "SELECT signal_id, name, x, y, z, color, category, color_override, radius, is_static, threat, dps, updated_at "
            "FROM hostile_signals WHERE guild_id = ? AND channel_id = ?",
            (guild_id, channel_id),
        ).fetchall()

        stale_ids = []
        result = []
        for row in rows:
            # Falls back to the "enemy" default if a signal somehow carries an
            # unrecognized category — never crashes, just doesn't special-case it.
            setting = category_settings.get(row["category"], CATEGORY_SETTING_DEFAULTS["enemy"])
            if row["updated_at"] < now - setting["retention_seconds"]:
                stale_ids.append(row["signal_id"])
                continue
            if not setting["enabled"]:
                continue
            result.append({
                "signal_id": row["signal_id"],
                "name": row["name"],
                "x": row["x"],
                "y": row["y"],
                "z": row["z"],
                # A manually recolored signal (color_override) keeps its own
                # color; otherwise it follows its category's admin-set color.
                "color": row["color"] if row["color_override"] else setting["color"],
                "category": row["category"],
                "radius": row["radius"],
                "is_static": bool(row["is_static"]),
                "threat": row["threat"],
                "dps": row["dps"],
                # Every row has this (it's set on every upsert — see
                # upsert_hostile_signal above), unlike the trail-derived
                # last_seen_at hostile_points_and_extras_for also computes,
                # which is only present when there's recent position-history
                # data. This is what lets every signal show a "last heard"
                # label even when it hasn't moved enough to have a trail.
                "last_seen_at": row["updated_at"],
            })

        if stale_ids:
            placeholders = ",".join("?" * len(stale_ids))
            conn.execute(
                f"DELETE FROM hostile_signals WHERE guild_id = ? AND channel_id = ? "
                f"AND signal_id IN ({placeholders})",
                (guild_id, channel_id, *stale_ids),
            )
        conn.execute(
            "DELETE FROM hostile_signal_history WHERE guild_id = ? AND channel_id = ? AND recorded_at < ?",
            (guild_id, channel_id, replay_cutoff),
        )
    return result


def get_replay_bounds(guild_id, channel_id):
    """{"min_ts", "max_ts"} spanning every recorded position_history/
    hostile_signal_history sample still within get_replay_retention_seconds()
    for this channel — the range the web map's replay scrubber can slide
    across. None if there's no history at all yet (e.g. right after this
    feature was added, or a brand new channel)."""
    cutoff = time.time() - get_replay_retention_seconds()
    with db() as conn:
        p = conn.execute(
            "SELECT MIN(recorded_at) AS lo, MAX(recorded_at) AS hi FROM position_history "
            "WHERE guild_id = ? AND channel_id = ? AND recorded_at >= ?",
            (guild_id, channel_id, cutoff),
        ).fetchone()
        s = conn.execute(
            "SELECT MIN(recorded_at) AS lo, MAX(recorded_at) AS hi FROM hostile_signal_history "
            "WHERE guild_id = ? AND channel_id = ? AND recorded_at >= ?",
            (guild_id, channel_id, cutoff),
        ).fetchone()
    los = [r["lo"] for r in (p, s) if r["lo"] is not None]
    his = [r["hi"] for r in (p, s) if r["hi"] is not None]
    if not los:
        return None
    return {"min_ts": min(los), "max_ts": max(his)}


def get_replay_snapshot(guild_id, channel_id, ts, category_settings):
    """Reconstructs map state as of unix timestamp `ts`: every player/signal
    whose most recent sample at or before `ts` is still "fresh" there — i.e.
    within that entity's category's retention_seconds of ts, the same "gone
    from the map" rule get_hostile_signals/prune_stale_player_markers apply
    relative to now, just relative to ts instead — plus a trailing window of
    samples sized by that category's trail_seconds, for the trail line.

    One connection for the whole snapshot (unlike the live view's
    live_extras_for/hostile_points_and_extras_for, which hop a thread per
    entity — fine on a 3s live poll, too slow for an interactively dragged
    scrub slider).

    Returns (players, signals):
      players: [(player_name, latest_row, trail_rows), ...]
      signals: [(signal_id, name, category, color, trail_rows), ...]
    latest_row is trail_rows[-1] in both cases; kept as a separate element
    only so callers don't need to re-index."""
    # Bounded by the widest retention_seconds/trail_seconds any category
    # actually uses (capped at the replay window), NOT the full replay
    # window itself — a row older than every category's own retention
    # couldn't count as "still present" anyway (see the per-entity presence
    # checks below), so there's no correctness reason to scan it. This
    # matters a lot: playback issues a fetch every ~500ms (see the client's
    # REPLAY block), and the replay window can be 24h+ while a trail is
    # typically ~10 minutes — scanning the full window on every tick would
    # mean rereading a day of rows to draw a 10-minute line, twice a second.
    needed_lookback = max(max(s["retention_seconds"], s["trail_seconds"]) for s in category_settings.values())
    lo = ts - min(needed_lookback, get_replay_retention_seconds())
    with db() as conn:
        prows = conn.execute(
            "SELECT * FROM position_history WHERE guild_id = ? AND channel_id = ? "
            "AND recorded_at BETWEEN ? AND ? ORDER BY player_name, recorded_at",
            (guild_id, channel_id, lo, ts),
        ).fetchall()
        srows = conn.execute(
            "SELECT * FROM hostile_signal_history WHERE guild_id = ? AND channel_id = ? "
            "AND recorded_at BETWEEN ? AND ? ORDER BY signal_id, recorded_at",
            (guild_id, channel_id, lo, ts),
        ).fetchall()
        current_signals = {
            r["signal_id"]: r for r in conn.execute(
                "SELECT signal_id, name, category FROM hostile_signals WHERE guild_id = ? AND channel_id = ?",
                (guild_id, channel_id),
            )
        }

    by_player = {}
    for r in prows:
        by_player.setdefault(r["player_name"], []).append(r)

    player_setting = category_settings["player"]
    players = []
    if player_setting["enabled"]:
        trail_lo = ts - player_setting["trail_seconds"]
        presence_lo = ts - player_setting["retention_seconds"]
        for name, hist in by_player.items():
            last = hist[-1]
            if last["recorded_at"] < presence_lo:
                continue
            trail = [r for r in hist if r["recorded_at"] >= trail_lo][-TRAIL_DISPLAY_LIMIT:]
            # trail_seconds can be shorter than retention_seconds, so a
            # `last` sample that's still "present" can predate trail_lo and
            # get filtered out of trail entirely — always keep at least
            # `last` itself so trail is never empty for an entity presence
            # already confirmed exists (map_replay indexes trail[-1]).
            if not trail:
                trail = [last]
            players.append((name, last, trail))

    by_signal = {}
    for r in srows:
        by_signal.setdefault(r["signal_id"], []).append(r)

    signals = []
    for signal_id, hist in by_signal.items():
        last = hist[-1]
        # NULL name/category means this row predates that column (see
        # init_db's migration) — fall back to the signal's current
        # hostile_signals row, same as get_hostile_signals's own fallback
        # for an unrecognized category.
        fallback = current_signals.get(signal_id)
        category = last["category"] or (fallback["category"] if fallback else None) or "enemy"
        setting = category_settings.get(category, category_settings["enemy"])
        if not setting["enabled"]:
            continue
        if last["recorded_at"] < ts - setting["retention_seconds"]:
            continue
        name = last["name"] or (fallback["name"] if fallback else None) or f"Signal {signal_id}"
        trail_lo = ts - setting["trail_seconds"]
        trail = [r for r in hist if r["recorded_at"] >= trail_lo][-TRAIL_DISPLAY_LIMIT:]
        # Same "always keep last" guard as the player loop above — here it's
        # not just cosmetic: map_replay derives its own `last` as trail[-1]
        # for signals (unlike players, which get `last` as a separate
        # element), so an empty trail crashes it with an IndexError.
        if not trail:
            trail = [last]
        signals.append((signal_id, name, category, setting["color"], trail))

    return players, signals


def delete_hostile_signal(guild_id, channel_id, signal_id):
    """Manually dismiss a hostile signal (used by the web map's editor).
    If the plugin is still actively detecting it, it reappears on the next
    /sync/hostiles push — same as deleting a synced vault GPS point that's
    still in the player's local list. Returns True if a matching signal was
    found and removed.

    Only removes the live hostile_signals row — the signal's history in
    hostile_signal_history is left alone (same as get_hostile_signals's own
    automatic, retention-based pruning already does), so the replay
    scrubber can still reconstruct this signal at a timestamp from before
    it was dismissed instead of it being gone from replay too. History
    still ages out on its own via get_replay_retention_seconds, same as
    ever; this dismiss action just stops affecting it directly."""
    with db() as conn:
        cur = conn.execute(
            "DELETE FROM hostile_signals WHERE guild_id = ? AND channel_id = ? AND signal_id = ?",
            (guild_id, channel_id, signal_id),
        )
    return cur.rowcount > 0


def parse_index_string(index_str):
    """Parse an index string into a sorted list of unique indices.
    Supports: single (34), range (25-35), list (25, 27, 33), mixed (25-30, 32, 40, 42-50)."""
    indices = set()
    for part in index_str.split(","):
        part = part.strip()
        if not part:
            continue
        if "-" in part:
            bounds = part.split("-", 1)
            start, end = int(bounds[0].strip()), int(bounds[1].strip())
            if start > end:
                raise ValueError(f"Invalid range {start}-{end}: start must be <= end")
            indices.update(range(start, end + 1))
        else:
            indices.add(int(part))
    return sorted(indices)


def revise_vector(guild_id, channel_id, indices, name=None, color=None):
    vectors = load_vectors(guild_id, channel_id)
    valid_indices = {idx for idx, _, _ in vectors}
    if not all(i in valid_indices for i in indices):
        return False
    updates = []
    if name is not None:
        updates.append("name = ?")
    if color is not None:
        updates.append("color = ?")
    if not updates:
        return False
    set_clause = ", ".join(updates)
    index_set = set(indices)
    with db() as conn:
        for idx, row_id, _ in vectors:
            if idx in index_set:
                params = []
                if name is not None:
                    params.append(name)
                if color is not None:
                    params.append(color)
                params.append(row_id)
                conn.execute(f"UPDATE gps_points SET {set_clause} WHERE id = ?", params)
    return True


def remove_vectors_by_indices(guild_id, channel_id, indices):
    """Trash (see delete_gps_point) the GPS points at these indices — kept in
    the database until /empty_trash actually erases them."""
    vectors = load_vectors(guild_id, channel_id)
    valid_indices = {idx for idx, _, _ in vectors}
    if not all(i in valid_indices for i in indices):
        return False
    index_set = set(indices)
    ids_to_delete = [row_id for idx, row_id, _ in vectors if idx in index_set]
    with db() as conn:
        conn.executemany(
            "UPDATE gps_points SET deleted_at = ? WHERE id = ?",
            [(time.time(), rid) for rid in ids_to_delete],
        )
    return True


def get_bound_channels(guild_id):
    with db() as conn:
        rows = conn.execute(
            "SELECT channel_id FROM guild_bindings WHERE guild_id = ?",
            (guild_id,),
        ).fetchall()
    return {row["channel_id"] for row in rows}


def user_can_access_channel(user_id, guild_id, channel_id) -> bool:
    """Whether the given Discord user id can view (read messages in) the
    given channel, per the bot's own cached guild/member/role data. Used to
    gate the web map viewer — a user only sees maps for channels they could
    actually read in Discord."""
    if LOCAL_ACCESS:
        return True
    guild = bot.get_guild(guild_id)
    if guild is None:
        print(f"[access-debug] guild {guild_id} not in bot cache")
        return False
    channel = guild.get_channel(channel_id)
    if channel is None:
        print(f"[access-debug] channel {channel_id} not found in guild {guild_id} ({guild.name})")
        return False
    member = guild.get_member(int(user_id))
    if member is None:
        print(f"[access-debug] member {user_id} not found in guild {guild_id} ({guild.name})")
        return False
    result = channel.permissions_for(member).view_channel
    if not result:
        print(f"[access-debug] member {user_id} lacks view_channel on {channel_id} ({channel.name})")
    return result


def user_can_manage_channel(user_id, guild_id, channel_id) -> bool:
    """Whether the given Discord user can change signal-category settings for
    this channel — Manage Channel (for this specific channel) or Manage
    Guild (server-wide), not just view access."""
    if LOCAL_ACCESS:
        return True
    guild = bot.get_guild(guild_id)
    if guild is None:
        return False
    channel = guild.get_channel(channel_id)
    if channel is None:
        return False
    member = guild.get_member(int(user_id))
    if member is None:
        return False
    perms = channel.permissions_for(member)
    return perms.manage_channels or perms.manage_guild


def list_bound_channels_with_names(user_id=None):
    """Every bound (guild_id, channel_id), labeled 'GuildName/ChannelName' from
    the bot's cache (falls back to raw IDs for guilds/channels it can't see).
    When `user_id` is given, only channels that Discord user can actually read
    are included."""
    with db() as conn:
        rows = conn.execute(
            "SELECT guild_id, channel_id FROM guild_bindings ORDER BY guild_id, channel_id"
        ).fetchall()

    result = []
    for row in rows:
        if user_id is not None and not user_can_access_channel(user_id, row["guild_id"], row["channel_id"]):
            continue
        guild = bot.get_guild(row["guild_id"])
        channel = bot.get_channel(row["channel_id"])
        guild_name = guild.name if guild else ("Local" if LOCAL_ACCESS else str(row["guild_id"]))
        channel_name = channel.name if channel else ("this PC" if LOCAL_ACCESS else str(row["channel_id"]))
        result.append({
            "guild_id": row["guild_id"],
            "channel_id": row["channel_id"],
            "label": f"{guild_name}/{channel_name}",
        })
    return result


def set_bind_channel(guild_id, channel_id):
    with db() as conn:
        conn.execute(
            "INSERT OR IGNORE INTO guild_bindings (guild_id, channel_id) VALUES (?, ?)",
            (guild_id, channel_id),
        )


def create_sync_token(guild_id, channel_id, label=None):
    token = secrets.token_urlsafe(24)
    with db() as conn:
        conn.execute(
            "INSERT INTO sync_tokens (token, guild_id, channel_id, label) VALUES (?, ?, ?, ?)",
            (token, guild_id, channel_id, label),
        )
    return token


def revoke_sync_token(guild_id, channel_id, token_prefix):
    with db() as conn:
        cur = conn.execute(
            "DELETE FROM sync_tokens WHERE guild_id = ? AND channel_id = ? AND token LIKE ?",
            (guild_id, channel_id, f"{token_prefix}%"),
        )
    return cur.rowcount


def list_sync_tokens(guild_id, channel_id):
    with db() as conn:
        rows = conn.execute(
            "SELECT token, label, created_at FROM sync_tokens WHERE guild_id = ? AND channel_id = ? ORDER BY created_at",
            (guild_id, channel_id),
        ).fetchall()
    return rows


def resolve_sync_token(token):
    """Return (guild_id, channel_id) for a valid sync token, or None."""
    with db() as conn:
        row = conn.execute(
            "SELECT guild_id, channel_id FROM sync_tokens WHERE token = ?",
            (token,),
        ).fetchone()
    return (row["guild_id"], row["channel_id"]) if row else None


def get_sync_token_label(token):
    with db() as conn:
        row = conn.execute("SELECT label FROM sync_tokens WHERE token = ?", (token,)).fetchone()
    return row["label"] if row else None


def upsert_known_player(guild_id, channel_id, player_name, token, token_label):
    """Records/refreshes a player's durable "connected with a sync token"
    entry — see known_players' table comment. Called on every /sync/position
    push (see sync_api.sync_position)."""
    now = time.time()
    with db() as conn:
        cur = conn.execute(
            "UPDATE known_players SET last_seen_at = ?, token = ?, token_label = ? "
            "WHERE guild_id = ? AND channel_id = ? AND player_name = ?",
            (now, token, token_label, guild_id, channel_id, player_name),
        )
        if cur.rowcount == 0:
            conn.execute(
                "INSERT INTO known_players "
                "(guild_id, channel_id, player_name, token, token_label, first_seen_at, last_seen_at) "
                "VALUES (?, ?, ?, ?, ?, ?, ?)",
                (guild_id, channel_id, player_name, token, token_label, now, now),
            )


def get_known_players(guild_id, channel_id):
    """Every player who has ever synced into this channel, most-recently-seen
    first — for the web dashboard's Players tab."""
    with db() as conn:
        rows = conn.execute(
            "SELECT player_name, token_label, first_seen_at, last_seen_at FROM known_players "
            "WHERE guild_id = ? AND channel_id = ? ORDER BY last_seen_at DESC",
            (guild_id, channel_id),
        ).fetchall()
    return rows


async def reject_if_not_bound(interaction: discord.Interaction) -> bool:
    bound = get_bound_channels(interaction.guild.id)
    if not bound:
        await interaction.response.send_message(
            "No channel is bound for this server. An administrator must run `/bind` "
            "in the channel where this bot should operate before any commands can be used.",
            ephemeral=True,
        )
        return True
    if interaction.channel.id not in bound:
        await interaction.response.send_message(
            "This command can only be used in a bound channel.",
            ephemeral=True,
        )
        return True
    return False


@bot.event
async def on_command_error(ctx, error):
    if isinstance(error, commands.CommandNotFound):
        return


@bot.event
async def on_ready():
    init_db()
    print(f"We have logged in as {bot.user}")
    for guild in bot.guilds:
        print(f"Server Name: {guild.name}, Server ID: {guild.id}")
    cfg = bot_config.load()
    dev_guild_id = cfg.get("dev_guild_id")
    try:
        if cfg.get("dev_mode") and dev_guild_id:
            # Guild-scoped sync reflects in Discord's UI within seconds,
            # unlike a global sync (which Discord can take up to an hour to
            # propagate) — much faster iteration while testing commands.
            # copy_global_to mirrors the bot's normal (global) command set
            # onto this one guild rather than syncing an empty set.
            guild = discord.Object(id=dev_guild_id)
            bot.tree.copy_global_to(guild=guild)
            synced = await bot.tree.sync(guild=guild)
            print(f"[dev mode] Synced {len(synced)} command(s) to dev guild {dev_guild_id}")
        else:
            synced = await bot.tree.sync()
            print(f"Synced {len(synced)} command(s) globally")
    except Exception as e:
        print(f"Failed to sync commands: {e}")
    # See --mode in the __main__ block below: `--mode bot` runs the Discord
    # bot with the web/sync server left off entirely (RUN_WEB_SERVER is set
    # False before bot.run() is ever called in that case).
    if RUN_WEB_SERVER:
        await sync_api.start(sys.modules[__name__])


async def send_paginated(interaction: discord.Interaction, header: str, lines: list):
    LIMIT = 1900
    responded = False
    chunk = []
    chunk_len = len(header) + 8  # account for header + ```\n and \n```

    async def flush(current_chunk):
        nonlocal responded
        body = "```\n" + "\n".join(current_chunk) + "\n```"
        if not responded:
            await interaction.response.send_message(f"{header}\n{body}")
            responded = True
        else:
            await interaction.followup.send(body)

    for line in lines:
        cost = len(line) + 1
        if chunk and chunk_len + cost > LIMIT:
            await flush(chunk)
            chunk = []
            chunk_len = 8
        chunk.append(line)
        chunk_len += cost

    if chunk:
        await flush(chunk)


@bot.tree.command(name="search_gps", description="Search GPS points with various filters.")
async def search_gps(interaction: discord.Interaction, search_string: Optional[str] = None, reference_gps: Optional[str] = None, distance_km: Optional[float] = None):
    if await reject_if_not_bound(interaction):
        return

    vectors = load_vectors(interaction.guild.id, interaction.channel.id)

    if search_string is None and reference_gps is None and distance_km is None:
        if vectors:
            lines = [f"{index}: {vector}" for index, _, vector in vectors]
            await send_paginated(interaction, "**All GPS Points:**", lines)
        else:
            await interaction.response.send_message("No GPS points found.")
        return

    if reference_gps:
        try:
            reference_point = parse_gps_data(reference_gps)
        except Exception as e:
            await interaction.response.send_message(f"Invalid reference GPS point: {str(e)}")
            return

        if distance_km is not None:
            nearby = [(v, euclidean_distance(reference_point, v))
                      for _, _, v in vectors]
            nearby = [(v, d) for v, d in nearby if d <= distance_km * 1000]
            if nearby:
                lines = [f"{v.name}: {v} (Distance: {d/1000:.2f} Km)" for v, d in nearby]
                await send_paginated(interaction, f"**GPS Points within {distance_km} Km of {reference_point.name}:**", lines)
            else:
                await interaction.response.send_message(f"No GPS points found within {distance_km} Km of {reference_point.name}.")
            return

        results = [(idx, v, euclidean_distance(reference_point, v))
                   for idx, _, v in vectors
                   if search_string and search_string.lower() in v.name.lower()]
        results.sort(key=lambda x: x[2])
        if results:
            lines = [f"{idx}: {v}, Distance: {d/1000:.2f} Km" for idx, v, d in results]
            await send_paginated(interaction, f"**Closest Points to {reference_point.name} containing '{search_string}':**", lines)
        else:
            await interaction.response.send_message(f"No GPS points found containing '{search_string}' near {reference_point.name}.")
        return

    matches = [(idx, v) for idx, _, v in vectors
               if search_string.lower() in v.name.lower()]
    if matches:
        lines = [f"{idx}: {v}" for idx, v in matches]
        await send_paginated(interaction, f"**Results for '{search_string}':**", lines)
    else:
        await interaction.response.send_message(f"No GPS points found containing '{search_string}'.")


@bot.tree.command(name="add_gps", description="Add a GPS point to the storage.")
async def add_gps(interaction: discord.Interaction, gps_string: str):
    """Add a GPS point to the storage."""
    if await reject_if_not_bound(interaction):
        return

    gpslist = gps_string.split("GPS:")
    for gps in gpslist:
        if gps == '':
            continue
        try:
            vector = parse_gps_data(gps)
            duplicate = find_duplicate_coords(interaction.guild.id, interaction.channel.id, vector)
            if duplicate:
                await interaction.channel.send(f"Skipped `{vector.name}`: coordinates already exist as `{duplicate}`.")
                continue
            add_vector(interaction.guild.id, interaction.channel.id, vector)
            await interaction.channel.send("Identified Point: " + str(vector))
        except Exception as e:
            await interaction.channel.send(f"Error adding GPS point: {str(e)}\nPoint: {gps}")
    await interaction.response.send_message("Succesfully stashed all identified GPS Points.")


@bot.tree.command(name="bind", description="Bind the bot to respond only in this channel.")
@discord.app_commands.default_permissions(administrator=True)
async def bind(interaction: discord.Interaction):
    set_bind_channel(interaction.guild.id, interaction.channel.id)
    await interaction.response.send_message(f"Bot is now bound to this channel: {interaction.channel.name}")


@bot.tree.command(name="create_sync_token", description="Create a token for the Space Engineers plugin to sync GPS/position into this channel.")
@discord.app_commands.describe(label="Optional name to help you identify this token later (e.g. a player's name)")
async def create_sync_token_cmd(interaction: discord.Interaction, label: Optional[str] = None):
    if await reject_if_not_bound(interaction):
        return
    token = create_sync_token(interaction.guild.id, interaction.channel.id, label)
    endpoint = sync_api.public_endpoint_hint()
    await interaction.response.send_message(
        "Sync token created. Paste this into the plugin's config — it is shown only once:\n"
        f"```\nendpoint: {endpoint}\ntoken: {token}\n```",
        ephemeral=True,
    )


@bot.tree.command(name="revoke_sync_token", description="Revoke a sync token by its label or the start of its value.")
async def revoke_sync_token_cmd(interaction: discord.Interaction, token_prefix: str):
    if await reject_if_not_bound(interaction):
        return
    removed = revoke_sync_token(interaction.guild.id, interaction.channel.id, token_prefix)
    if removed:
        await interaction.response.send_message(f"Revoked {removed} sync token(s).", ephemeral=True)
    else:
        await interaction.response.send_message("No matching sync token found in this channel.", ephemeral=True)


@bot.tree.command(name="list_sync_tokens", description="List active sync tokens for this channel (labels only, not the full token).")
async def list_sync_tokens_cmd(interaction: discord.Interaction):
    if await reject_if_not_bound(interaction):
        return
    rows = list_sync_tokens(interaction.guild.id, interaction.channel.id)
    if not rows:
        await interaction.response.send_message("No sync tokens for this channel.", ephemeral=True)
        return
    lines = [f"{r['token'][:8]}… — {r['label'] or '(no label)'} — created {r['created_at']}" for r in rows]
    await send_paginated(interaction, "**Sync tokens for this channel:**", lines)


def argb_to_css_hex(color: str) -> str:
    """Space Engineers GPS color is #AARRGGBB. CSS uses #RRGGBB."""
    if not color:
        return "#ffffff"
    c = color.lstrip("#")
    if len(c) == 8:
        return "#" + c[2:]
    if len(c) == 6:
        return "#" + c
    return "#ffffff"


def normalize_argb_hex(color: str) -> str:
    """Normalize a stored GPS color to '#AARRGGBB' (8 hex digits), defaulting
    alpha to FF when only RRGGBB (6 digits) was stored. Used when handing
    colors back to the plugin, which expects a consistent 8-digit format."""
    if not color:
        return DEFAULT_GPS_COLOR
    c = color.lstrip("#")
    if len(c) == 6:
        return "#FF" + c.upper()
    if len(c) == 8:
        return "#" + c.upper()
    return DEFAULT_GPS_COLOR


def edit_gps_point(guild_id, channel_id, old_name, new_name, color):
    """Rename/recolor a vault GPS point (used by the web map's editor),
    including a live position marker's color — sets color_override so a
    manually-picked color sticks (see upsert_gps_point/
    apply_player_category_settings) instead of being overwritten by the next
    position sync or the "player" category's admin-set color.
    Returns True if a matching point was found and updated."""
    with db() as conn:
        cur = conn.execute(
            "UPDATE gps_points SET name = ?, color = ?, color_override = 1 "
            "WHERE guild_id = ? AND channel_id = ? AND name = ?",
            (new_name, normalize_argb_hex(color), guild_id, channel_id, old_name),
        )
    return cur.rowcount > 0


def delete_gps_point(guild_id, channel_id, name):
    """Trash a single vault GPS point by name (used by the web map's editor)
    — marks it deleted_at rather than actually removing the row, so it stays
    recoverable and can't be silently re-added by another player's stale
    local list still containing it (see upsert_and_trash_gps_points) until
    /empty_trash permanently erases every trashed point in this channel.
    Returns True if a matching, not-already-trashed point was found."""
    with db() as conn:
        cur = conn.execute(
            "UPDATE gps_points SET deleted_at = ? WHERE guild_id = ? AND channel_id = ? "
            "AND name = ? AND deleted_at IS NULL",
            (time.time(), guild_id, channel_id, name),
        )
    return cur.rowcount > 0


def empty_trash(guild_id, channel_id):
    """Permanently erases every trashed GPS point in this channel. This is
    the only thing that actually deletes a row from gps_points once a point
    has been removed — delete_gps_point, remove_vectors_by_indices and
    upsert_and_trash_gps_points's deletions all just mark deleted_at.
    Returns the number of points erased."""
    with db() as conn:
        cur = conn.execute(
            "DELETE FROM gps_points WHERE guild_id = ? AND channel_id = ? AND deleted_at IS NOT NULL",
            (guild_id, channel_id),
        )
    return cur.rowcount


def build_map_html(points, title: str, live_extra: dict = None, live_config: dict = None,
                    planets=None, asteroids=None, ores=None, shapes=None) -> str:
    """Render the map template. `live_extra` optionally maps a point's name to
    {"trail": [[x,y,z], ...], "velocity": {"vx","vy","vz","speed"}} (used for
    live player markers). `live_config` optionally enables client-side polling
    for real-time movement (only meaningful for the served /map/frame view,
    never for the standalone downloadable file). `planets`/`asteroids` are each
    an optional list of {"name","x","y","z","radius"}, rendered as large
    static spheres — asteroids use a bounding-sphere radius rather than
    planets' precise physical one, but render identically. `shapes` is an
    optional list of mesh shapes (see
    get_asteroid_shapes) — an asteroid's real shape, drawn instead of its
    plain bounding sphere when present."""
    live_extra = live_extra or {}
    payload = []
    for _, _, v in points:
        entry = {
            "name": v.name,
            "x": v.x,
            "y": v.y,
            "z": v.z,
            "color": argb_to_css_hex(v.color),
        }
        extra = live_extra.get(v.name)
        if extra:
            entry.update(extra)
        payload.append(entry)

    data_json = json.dumps(payload)
    safe_title = title.replace("<", "&lt;").replace(">", "&gt;")

    with open(MAP_TEMPLATE_PATH, "r", encoding="utf-8") as f:
        template = f.read()

    return (template
            .replace("{{POINTS_JSON}}", data_json)
            .replace("{{POINT_COUNT}}", str(len(payload)))
            .replace("{{TITLE}}", safe_title)
            .replace("{{LIVE_CONFIG_JSON}}", json.dumps(live_config))
            .replace("{{PLANETS_JSON}}", json.dumps(planets or []))
            .replace("{{ASTEROIDS_JSON}}", json.dumps(asteroids or []))
            .replace("{{ORES_JSON}}", json.dumps(ores or []))
            .replace("{{SHAPES_JSON}}", json.dumps(shapes or [])))


@bot.tree.command(name="map", description="Render an interactive 3D HTML map of all GPS points in this channel.")
async def map_gps(interaction: discord.Interaction):
    """Downloadable snapshot of the same map the web viewer (/map/frame)
    shows — same GPS points, hostile signals (with category colors/filter),
    live player trails/velocity/weapon-lock, and planets, built the exact
    same way via the module-level helpers in sync_api.py (see
    apply_player_category_settings/hostile_points_and_extras_for/
    live_extras_for — factored out of make_app() specifically so this command
    and the live web map share one code path instead of drifting apart).
    Deliberately built with no live_config: this is a static file with no
    server session behind it, so live polling/editing UI stays off — the
    client-side script already no-ops all of that when LIVE is unset."""
    if await reject_if_not_bound(interaction):
        return

    guild_id, channel_id = interaction.guild.id, interaction.channel.id
    db_module = sys.modules[__name__]

    points = load_vectors(guild_id, channel_id)
    points = await sync_api.apply_player_category_settings(points, db_module, guild_id, channel_id)
    hostile_points, hostile_extras = await sync_api.hostile_points_and_extras_for(db_module, guild_id, channel_id)
    all_points = points + [(0, None, v) for v in hostile_points]
    planet_rows = get_planets(guild_id, channel_id)
    asteroid_rows = get_asteroids(guild_id, channel_id)
    ores = get_ore_deposits(guild_id, channel_id)
    shapes = get_asteroid_shapes(guild_id, channel_id)
    if not all_points and not planet_rows and not asteroid_rows and not ores:
        await interaction.response.send_message("No GPS points in this channel to map.")
        return

    extras = await sync_api.live_extras_for(points, db_module, guild_id, channel_id)
    extras.update(hostile_extras)
    planets = [
        {"name": p["name"], "x": p["x"], "y": p["y"], "z": p["z"], "radius": p["radius"]}
        for p in planet_rows
    ]
    asteroids = [
        {"name": a["name"], "x": a["x"], "y": a["y"], "z": a["z"], "radius": a["radius"]}
        for a in asteroid_rows
    ]

    title = f"#{interaction.channel.name}"
    html = build_map_html(all_points, title, live_extra=extras, planets=planets, asteroids=asteroids, ores=ores,
                           shapes=shapes)
    buf = io.BytesIO(html.encode("utf-8"))
    file = discord.File(buf, filename=f"gps_map_{channel_id}.html")
    await interaction.response.send_message(
        f"Rendered {len(all_points)} point(s). Open the attached HTML in a browser.",
        file=file,
    )


@bot.tree.command(name="remove_gps_by_index_range", description="Remove GPS points by index. Supports: 34 | 25-35 | 25,27,33 | 25-30,32,42-50")
async def remove_gps_range(interaction: discord.Interaction, indices: str):
    if await reject_if_not_bound(interaction):
        return
    try:
        parsed = parse_index_string(indices)
    except ValueError as e:
        await interaction.response.send_message(f"Invalid index input: {e}", ephemeral=True)
        return
    if remove_vectors_by_indices(interaction.guild.id, interaction.channel.id, parsed):
        await interaction.response.send_message(f"Removed {len(parsed)} GPS point(s). Run /empty_trash to erase them for good.")
    else:
        await interaction.response.send_message("One or more indices were out of range. No points removed.", ephemeral=True)


@bot.tree.command(name="empty_trash", description="Permanently erase GPS points deleted in this channel, so they stop being recoverable.")
async def empty_trash_cmd(interaction: discord.Interaction):
    """Discord-facing wrapper for empty_trash(): deleting a GPS point (in-game,
    via /remove_gps_by_index_range, or via the web map's editor) only trashes
    it — this is what actually erases those rows for good."""
    if await reject_if_not_bound(interaction):
        return
    count = empty_trash(interaction.guild.id, interaction.channel.id)
    if count == 0:
        await interaction.response.send_message("Trash is already empty.")
    else:
        await interaction.response.send_message(f"Permanently erased {count} deleted GPS point(s).")


@bot.tree.command(name="revise", description="Revise the name and/or color of GPS points by index. Supports: 34 | 25-35 | 25,27,33")
async def revise_gps(interaction: discord.Interaction, indices: str, name: Optional[str] = None, color: Optional[str] = None):
    if await reject_if_not_bound(interaction):
        return
    if name is None and color is None:
        await interaction.response.send_message("Provide at least one of `name` or `color` to update.", ephemeral=True)
        return
    if name is not None and len(name) > 32:
        await interaction.response.send_message(f"Name exceeds the 32 character limit ({len(name)} characters).", ephemeral=True)
        return
    try:
        parsed = parse_index_string(indices)
    except ValueError as e:
        await interaction.response.send_message(f"Invalid index input: {e}", ephemeral=True)
        return
    if revise_vector(interaction.guild.id, interaction.channel.id, parsed, name=name, color=color):
        parts = []
        if name is not None:
            parts.append(f"name → `{name}`")
        if color is not None:
            parts.append(f"color → `{color}`")
        await interaction.response.send_message(f"Updated {len(parsed)} GPS point(s): {', '.join(parts)}.")
    else:
        await interaction.response.send_message("One or more indices were out of range. No points updated.", ephemeral=True)


# Guarded: this module gets imported (not just run directly) for one-off
# inspection/tooling (e.g. checking DB state from a script) — without this
# guard, that import alone starts a second live bot connection, since
# bot.run() blocks and none of the module-level code below was previously
# conditional on being the entry point.
async def _run_web_only():
    """Drives the sync/map web server with no Discord connection at all (see
    --mode web below). bot.run() isn't called — it owns/blocks its own event
    loop, and is what normally starts the web server too, from on_ready once
    login succeeds — so this does that same startup step directly on its own
    loop instead, then just waits forever (sync_api.start's aiohttp runner
    does its own listening once awaited; nothing further needs driving)."""
    await sync_api.start(sys.modules[__name__])
    await asyncio.Event().wait()


# Guarded: this module gets imported (not just run directly) for one-off
# inspection/tooling (e.g. checking DB state from a script) — without this
# guard, that import alone starts a second live bot connection, since
# bot.run() blocks and none of the module-level code below was previously
# conditional on being the entry point.
if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="GPS vault: Discord bot + sync/map web server.")
    parser.add_argument(
        "--mode", choices=("both", "bot", "web"), default="both",
        help="'both' (default): Discord bot and web server together in one process — "
             "unchanged from before this flag existed, including that the web server "
             "only actually starts once Discord login finishes (see on_ready). "
             "'bot': Discord bot only, web/sync server left off entirely. "
             "'web': sync/map web server only, no Discord connection at all — the web "
             "MAP's per-viewer permission checks (user_can_access_channel/"
             "user_can_manage_channel) and channel-name lookups all read from the bot's "
             "live guild/member cache, so with no bot connected they fail closed (every "
             "viewer denied) rather than failing open; the plugin's /sync/* endpoints "
             "(bearer-token authenticated, no Discord identity involved) are unaffected.",
    )
    args = parser.parse_args()

    init_db()
    migrate_legacy_files()

    if args.mode == "web":
        asyncio.run(_run_web_only())
    else:
        RUN_WEB_SERVER = args.mode == "both"

        # Discord token, sync server settings, and OAuth credentials all live in
        # one file now — bot_config.json (see bot_config.py).
        TOKEN = bot_config.load()["discord_token"]
        if not TOKEN:
            raise SystemExit("bot_config.json is missing discord_token — fill it in and restart.")

        bot.run(TOKEN)
