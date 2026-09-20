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

intents = discord.Intents.default()
bot = commands.Bot(command_prefix="/", intents=discord.Intents.all())
GPSBankInterface = 1059965274738151475

DB_PATH = "gps.db"
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


def init_db():
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
                updated_at REAL
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
            "WHERE guild_id = ? AND channel_id = ? ORDER BY id",
            (guild_id, channel_id),
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
            "AND color_override = 1 AND name LIKE ?",
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
    query (see apply_player_category_settings in sync_api.py)."""
    cutoff = time.time() - retention_seconds
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
    return stale_names


def find_duplicate_coords(guild_id, channel_id, vector):
    with db() as conn:
        row = conn.execute(
            "SELECT name FROM gps_points WHERE guild_id = ? AND channel_id = ? AND x = ? AND y = ? AND z = ?",
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
    otherwise every position sync would immediately clobber it back."""
    now = time.time()
    with db() as conn:
        cur = conn.execute(
            "UPDATE gps_points SET x = ?, y = ?, z = ?, updated_at = ?, "
            "color = CASE WHEN color_override = 1 THEN color ELSE ? END "
            "WHERE guild_id = ? AND channel_id = ? AND name = ?",
            (vector.x, vector.y, vector.z, now, vector.color, guild_id, channel_id, vector.name),
        )
        if cur.rowcount == 0:
            conn.execute(
                "INSERT INTO gps_points (guild_id, channel_id, name, x, y, z, color, updated_at) "
                "VALUES (?, ?, ?, ?, ?, ?, ?, ?)",
                (guild_id, channel_id, vector.name, vector.x, vector.y, vector.z, vector.color, now),
            )


def upsert_and_prune_gps_points(guild_id, channel_id, vectors):
    """Upsert every given point, then delete any non-live vault point in this
    channel that's missing from the list — so deleting a GPS in-game deletes
    it from the vault too. Live position markers (◆ prefix) are never touched
    here; they're managed separately by record_position_history/upsert via
    /sync/position.

    Caveat: with more than one player actively syncing the *same* channel,
    this treats each push as that player's full authoritative list, so a
    point one player still has locally can get re-created by their next push
    shortly after another player deletes it. Fine for the common case of one
    active syncer per channel; true multi-writer semantics would need
    per-point origin tracking, which isn't implemented."""
    live_prefix_pattern = sync_api.LIVE_POSITION_PREFIX + "%"
    with db() as conn:
        for v in vectors:
            cur = conn.execute(
                "UPDATE gps_points SET x = ?, y = ?, z = ?, color = ? "
                "WHERE guild_id = ? AND channel_id = ? AND name = ?",
                (v.x, v.y, v.z, v.color, guild_id, channel_id, v.name),
            )
            if cur.rowcount == 0:
                conn.execute(
                    "INSERT INTO gps_points (guild_id, channel_id, name, x, y, z, color) "
                    "VALUES (?, ?, ?, ?, ?, ?, ?)",
                    (guild_id, channel_id, v.name, v.x, v.y, v.z, v.color),
                )

        names = [v.name for v in vectors]
        if names:
            placeholders = ",".join("?" * len(names))
            cur = conn.execute(
                "DELETE FROM gps_points WHERE guild_id = ? AND channel_id = ? "
                f"AND name NOT LIKE ? AND name NOT IN ({placeholders})",
                (guild_id, channel_id, live_prefix_pattern, *names),
            )
        else:
            # No vectors at all (player has zero GPS points locally) — an
            # empty "NOT IN ()" is invalid SQL, and means "keep everything"
            # rather than "keep nothing" anyway, so prune unconditionally.
            cur = conn.execute(
                "DELETE FROM gps_points WHERE guild_id = ? AND channel_id = ? "
                "AND name NOT LIKE ?",
                (guild_id, channel_id, live_prefix_pattern),
            )
        deleted = cur.rowcount
    return deleted


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
                             lock_target_z=0.0, lock_distance=0.0, retention_seconds=POSITION_HISTORY_RETENTION_SECONDS):
    """Append a position sample for a live player and prune anything older
    than retention_seconds (the "player" category's configured trail_seconds
    — see CATEGORY_SETTING_DEFAULTS/get_category_settings), so the map can
    draw a recent movement trail. Acceleration is the plugin's real
    engine-computed value (whatever's actually controlled — grid physics
    when piloting, character physics when on foot), not estimated from
    consecutive samples here. locked/lock_* are the player's OWN weapon lock
    (MyTargetLockingComponent) at that moment — not whether anyone has
    locked onto the player, which the game doesn't track at all."""
    now = time.time()
    cutoff = now - retention_seconds
    with db() as conn:
        conn.execute(
            "INSERT INTO position_history "
            "(guild_id, channel_id, player_name, x, y, z, vx, vy, vz, ax, ay, az, "
            "locked, lock_target_name, lock_target_x, lock_target_y, lock_target_z, lock_distance, recorded_at) "
            "VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
            (guild_id, channel_id, player_name, x, y, z, vx, vy, vz, ax, ay, az,
             1 if locked else 0, lock_target_name, lock_target_x, lock_target_y, lock_target_z, lock_distance, now),
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
            "SELECT x, y, z, vx, vy, vz, ax, ay, az, "
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


def upsert_hostile_signal(guild_id, channel_id, signal_id, name, x, y, z, color=None, category=None):
    """Record/refresh a currently-detected hostile signal's latest known
    position. Keyed by signal_id (the game's EntityId), not name — pirate/NPC
    spawns commonly reuse identical display names. Leaves `color` (and its
    color_override flag) alone for a signal a viewer has manually recolored
    via edit_hostile_signal_color — otherwise every plugin re-detection would
    immediately clobber the override back to the category's default color."""
    now = time.time()
    with db() as conn:
        conn.execute(
            "INSERT INTO hostile_signals (guild_id, channel_id, signal_id, name, x, y, z, color, category, updated_at) "
            "VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?) "
            "ON CONFLICT (guild_id, channel_id, signal_id) DO UPDATE SET "
            "name = excluded.name, x = excluded.x, y = excluded.y, z = excluded.z, "
            "color = CASE WHEN hostile_signals.color_override = 1 THEN hostile_signals.color ELSE excluded.color END, "
            "category = excluded.category, updated_at = excluded.updated_at",
            (
                guild_id, channel_id, signal_id, name, x, y, z,
                color or DEFAULT_HOSTILE_SIGNAL_COLOR, category or "enemy", now,
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


def record_hostile_signal_history(guild_id, channel_id, signal_id, x, y, z, vx, vy, vz, ax=0.0, ay=0.0, az=0.0,
                                   retention_seconds=HOSTILE_SIGNAL_RETENTION_SECONDS):
    """Append a position sample for a hostile signal and prune anything older
    than retention_seconds (that signal's category's configured
    trail_seconds), mirroring record_position_history."""
    now = time.time()
    cutoff = now - retention_seconds
    with db() as conn:
        conn.execute(
            "INSERT INTO hostile_signal_history "
            "(guild_id, channel_id, signal_id, x, y, z, vx, vy, vz, ax, ay, az, recorded_at) "
            "VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
            (guild_id, channel_id, signal_id, x, y, z, vx, vy, vz, ax, ay, az, now),
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
            "SELECT x, y, z, vx, vy, vz, ax, ay, az, recorded_at FROM hostile_signal_history "
            "WHERE guild_id = ? AND channel_id = ? AND signal_id = ? AND recorded_at >= ? "
            "ORDER BY recorded_at DESC LIMIT ?",
            (guild_id, channel_id, signal_id, cutoff, TRAIL_DISPLAY_LIMIT),
        ).fetchall()
    return list(reversed(rows))


def get_hostile_signals(guild_id, channel_id):
    """Currently known hostile signals. Anything stale — per its OWN
    category's admin-configured retention_seconds (see
    CATEGORY_SETTING_DEFAULTS/get_category_settings; every category
    including "unidentified" has one now) — is deleted here (signal + its
    trail) before returning, so signals clear themselves off the map after
    the plugin stops reporting them (e.g. the game was closed), with no
    background job needed — the next map view does the pruning.

    Also applies this channel's admin category settings: a disabled category
    (e.g. "owned", off by default) is dropped from the result entirely — its
    rows stay in the table and keep accumulating history, they're just not
    returned here, so re-enabling the category shows current data
    immediately rather than waiting for the plugin to re-detect everything."""
    category_settings = get_category_settings(guild_id, channel_id)
    now = time.time()
    with db() as conn:
        rows = conn.execute(
            "SELECT signal_id, name, x, y, z, color, category, color_override, updated_at "
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
            })

        if stale_ids:
            placeholders = ",".join("?" * len(stale_ids))
            conn.execute(
                f"DELETE FROM hostile_signals WHERE guild_id = ? AND channel_id = ? "
                f"AND signal_id IN ({placeholders})",
                (guild_id, channel_id, *stale_ids),
            )
            conn.execute(
                f"DELETE FROM hostile_signal_history WHERE guild_id = ? AND channel_id = ? "
                f"AND signal_id IN ({placeholders})",
                (guild_id, channel_id, *stale_ids),
            )
    return result


def delete_hostile_signal(guild_id, channel_id, signal_id):
    """Manually dismiss a hostile signal (used by the web map's editor).
    If the plugin is still actively detecting it, it reappears on the next
    /sync/hostiles push — same as deleting a synced vault GPS point that's
    still in the player's local list. Returns True if a matching signal was
    found and removed."""
    with db() as conn:
        cur = conn.execute(
            "DELETE FROM hostile_signals WHERE guild_id = ? AND channel_id = ? AND signal_id = ?",
            (guild_id, channel_id, signal_id),
        )
        conn.execute(
            "DELETE FROM hostile_signal_history WHERE guild_id = ? AND channel_id = ? AND signal_id = ?",
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
    vectors = load_vectors(guild_id, channel_id)
    valid_indices = {idx for idx, _, _ in vectors}
    if not all(i in valid_indices for i in indices):
        return False
    index_set = set(indices)
    ids_to_delete = [row_id for idx, row_id, _ in vectors if idx in index_set]
    with db() as conn:
        conn.executemany(
            "DELETE FROM gps_points WHERE id = ?",
            [(rid,) for rid in ids_to_delete],
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
    guild = bot.get_guild(guild_id)
    if guild is None:
        return False
    channel = guild.get_channel(channel_id)
    if channel is None:
        return False
    member = guild.get_member(int(user_id))
    if member is None:
        return False
    return channel.permissions_for(member).view_channel


def user_can_manage_channel(user_id, guild_id, channel_id) -> bool:
    """Whether the given Discord user can change signal-category settings for
    this channel — Manage Channel (for this specific channel) or Manage
    Guild (server-wide), not just view access."""
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
        guild_name = guild.name if guild else str(row["guild_id"])
        channel_name = channel.name if channel else str(row["channel_id"])
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
    try:
        synced = await bot.tree.sync()
        print(f"Synced {len(synced)} command(s) globally")
    except Exception as e:
        print(f"Failed to sync commands: {e}")
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
@discord.app_commands.default_permissions(administrator=True)
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
@discord.app_commands.default_permissions(administrator=True)
async def revoke_sync_token_cmd(interaction: discord.Interaction, token_prefix: str):
    if await reject_if_not_bound(interaction):
        return
    removed = revoke_sync_token(interaction.guild.id, interaction.channel.id, token_prefix)
    if removed:
        await interaction.response.send_message(f"Revoked {removed} sync token(s).", ephemeral=True)
    else:
        await interaction.response.send_message("No matching sync token found in this channel.", ephemeral=True)


@bot.tree.command(name="list_sync_tokens", description="List active sync tokens for this channel (labels only, not the full token).")
@discord.app_commands.default_permissions(administrator=True)
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
    """Delete a single vault GPS point by name (used by the web map's
    editor). Returns True if a matching point was found and removed."""
    with db() as conn:
        cur = conn.execute(
            "DELETE FROM gps_points WHERE guild_id = ? AND channel_id = ? AND name = ?",
            (guild_id, channel_id, name),
        )
    return cur.rowcount > 0


def build_map_html(points, title: str, live_extra: dict = None, live_config: dict = None, planets=None) -> str:
    """Render the map template. `live_extra` optionally maps a point's name to
    {"trail": [[x,y,z], ...], "velocity": {"vx","vy","vz","speed"}} (used for
    live player markers). `live_config` optionally enables client-side polling
    for real-time movement (only meaningful for the served /map/frame view,
    never for the standalone downloadable file). `planets` is an optional list
    of {"name","x","y","z","radius"}, rendered as large static spheres."""
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
            .replace("{{PLANETS_JSON}}", json.dumps(planets or [])))


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
    if not all_points and not planet_rows:
        await interaction.response.send_message("No GPS points in this channel to map.")
        return

    extras = await sync_api.live_extras_for(points, db_module, guild_id, channel_id)
    extras.update(hostile_extras)
    planets = [
        {"name": p["name"], "x": p["x"], "y": p["y"], "z": p["z"], "radius": p["radius"]}
        for p in planet_rows
    ]

    title = f"#{interaction.channel.name}"
    html = build_map_html(all_points, title, live_extra=extras, planets=planets)
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
        await interaction.response.send_message(f"Removed {len(parsed)} GPS point(s).")
    else:
        await interaction.response.send_message("One or more indices were out of range. No points removed.", ephemeral=True)


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
if __name__ == "__main__":
    init_db()
    migrate_legacy_files()

    # Discord token, sync server settings, and OAuth credentials all live in
    # one file now — bot_config.json (see bot_config.py).
    TOKEN = bot_config.load()["discord_token"]
    if not TOKEN:
        raise SystemExit("bot_config.json is missing discord_token — fill it in and restart.")

    bot.run(TOKEN)