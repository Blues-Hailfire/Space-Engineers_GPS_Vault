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
                color      TEXT    NOT NULL DEFAULT '#FFFFFFFF'
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
                recorded_at REAL    NOT NULL
            )
        """)
        # Add acceleration columns if upgrading from a pre-acceleration schema.
        existing_ph_cols = {row["name"] for row in conn.execute("PRAGMA table_info(position_history)")}
        for col in ("ax", "ay", "az"):
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
                updated_at REAL    NOT NULL,
                PRIMARY KEY (guild_id, channel_id, signal_id)
            )
        """)
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
    must not accumulate duplicate rows."""
    with db() as conn:
        cur = conn.execute(
            "UPDATE gps_points SET x = ?, y = ?, z = ?, color = ? "
            "WHERE guild_id = ? AND channel_id = ? AND name = ?",
            (vector.x, vector.y, vector.z, vector.color, guild_id, channel_id, vector.name),
        )
        if cur.rowcount == 0:
            conn.execute(
                "INSERT INTO gps_points (guild_id, channel_id, name, x, y, z, color) "
                "VALUES (?, ?, ?, ?, ?, ?, ?)",
                (guild_id, channel_id, vector.name, vector.x, vector.y, vector.z, vector.color),
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
        placeholders = ",".join("?" * len(names))
        cur = conn.execute(
            "DELETE FROM gps_points WHERE guild_id = ? AND channel_id = ? "
            f"AND name NOT LIKE ? AND name NOT IN ({placeholders})",
            (guild_id, channel_id, live_prefix_pattern, *names),
        )
        deleted = cur.rowcount
    return deleted


POSITION_HISTORY_RETENTION_SECONDS = 6 * 3600


def record_position_history(guild_id, channel_id, player_name, x, y, z, vx, vy, vz, ax=0.0, ay=0.0, az=0.0):
    """Append a position sample for a live player and prune anything older
    than the retention window, so the map can draw a recent movement trail.
    Acceleration is the plugin's real engine-computed value (whatever's
    actually controlled — grid physics when piloting, character physics when
    on foot), not estimated from consecutive samples here."""
    now = time.time()
    cutoff = now - POSITION_HISTORY_RETENTION_SECONDS
    with db() as conn:
        conn.execute(
            "INSERT INTO position_history "
            "(guild_id, channel_id, player_name, x, y, z, vx, vy, vz, ax, ay, az, recorded_at) "
            "VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
            (guild_id, channel_id, player_name, x, y, z, vx, vy, vz, ax, ay, az, now),
        )
        conn.execute(
            "DELETE FROM position_history "
            "WHERE guild_id = ? AND channel_id = ? AND player_name = ? AND recorded_at < ?",
            (guild_id, channel_id, player_name, cutoff),
        )


def get_position_trail(guild_id, channel_id, player_name):
    """Position samples for a player within the retention window, oldest first."""
    cutoff = time.time() - POSITION_HISTORY_RETENTION_SECONDS
    with db() as conn:
        rows = conn.execute(
            "SELECT x, y, z, vx, vy, vz, ax, ay, az, recorded_at FROM position_history "
            "WHERE guild_id = ? AND channel_id = ? AND player_name = ? AND recorded_at >= ? "
            "ORDER BY recorded_at",
            (guild_id, channel_id, player_name, cutoff),
        ).fetchall()
    return rows


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

# Same window as POSITION_HISTORY_RETENTION_SECONDS, but doubling as the
# lifetime of the signal itself (not just its trail) — a hostile contact the
# plugin hasn't re-reported in 6 hours is dropped from the map entirely by
# get_hostile_signals below, rather than lingering like a player marker does.
HOSTILE_SIGNAL_RETENTION_SECONDS = 6 * 3600


def upsert_hostile_signal(guild_id, channel_id, signal_id, name, x, y, z, color=None):
    """Record/refresh a currently-detected hostile signal's latest known
    position. Keyed by signal_id (the game's EntityId), not name — pirate/NPC
    spawns commonly reuse identical display names."""
    now = time.time()
    with db() as conn:
        conn.execute(
            "INSERT INTO hostile_signals (guild_id, channel_id, signal_id, name, x, y, z, color, updated_at) "
            "VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?) "
            "ON CONFLICT (guild_id, channel_id, signal_id) DO UPDATE SET "
            "name = excluded.name, x = excluded.x, y = excluded.y, z = excluded.z, "
            "color = excluded.color, updated_at = excluded.updated_at",
            (guild_id, channel_id, signal_id, name, x, y, z, color or DEFAULT_HOSTILE_SIGNAL_COLOR, now),
        )


def record_hostile_signal_history(guild_id, channel_id, signal_id, x, y, z, vx, vy, vz, ax=0.0, ay=0.0, az=0.0):
    """Append a position sample for a hostile signal and prune anything older
    than the retention window, mirroring record_position_history."""
    now = time.time()
    cutoff = now - HOSTILE_SIGNAL_RETENTION_SECONDS
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


def get_hostile_signal_trail(guild_id, channel_id, signal_id):
    """Position samples for a hostile signal within the retention window,
    oldest first — mirrors get_position_trail."""
    cutoff = time.time() - HOSTILE_SIGNAL_RETENTION_SECONDS
    with db() as conn:
        rows = conn.execute(
            "SELECT x, y, z, vx, vy, vz, ax, ay, az, recorded_at FROM hostile_signal_history "
            "WHERE guild_id = ? AND channel_id = ? AND signal_id = ? AND recorded_at >= ? "
            "ORDER BY recorded_at",
            (guild_id, channel_id, signal_id, cutoff),
        ).fetchall()
    return rows


def get_hostile_signals(guild_id, channel_id):
    """Currently known hostile signals. Anything not refreshed within
    HOSTILE_SIGNAL_RETENTION_SECONDS is deleted here (signal + its trail)
    before reading, so signals clear themselves off the map ~6h after the
    plugin stops reporting them (e.g. the game was closed) with no
    background job needed — the next map view does the pruning."""
    cutoff = time.time() - HOSTILE_SIGNAL_RETENTION_SECONDS
    with db() as conn:
        conn.execute(
            "DELETE FROM hostile_signals WHERE guild_id = ? AND channel_id = ? AND updated_at < ?",
            (guild_id, channel_id, cutoff),
        )
        conn.execute(
            "DELETE FROM hostile_signal_history WHERE guild_id = ? AND channel_id = ? AND recorded_at < ?",
            (guild_id, channel_id, cutoff),
        )
        rows = conn.execute(
            "SELECT signal_id, name, x, y, z, color FROM hostile_signals "
            "WHERE guild_id = ? AND channel_id = ?",
            (guild_id, channel_id),
        ).fetchall()
    return rows


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
    """Rename/recolor a vault GPS point (used by the web map's editor).
    Returns True if a matching point was found and updated."""
    with db() as conn:
        cur = conn.execute(
            "UPDATE gps_points SET name = ?, color = ? "
            "WHERE guild_id = ? AND channel_id = ? AND name = ?",
            (new_name, normalize_argb_hex(color), guild_id, channel_id, old_name),
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
    if await reject_if_not_bound(interaction):
        return
    points = load_vectors(interaction.guild.id, interaction.channel.id)
    if not points:
        await interaction.response.send_message("No GPS points in this channel to map.")
        return

    title = f"#{interaction.channel.name}"
    html = build_map_html(points, title)
    buf = io.BytesIO(html.encode("utf-8"))
    file = discord.File(buf, filename=f"gps_map_{interaction.channel.id}.html")
    await interaction.response.send_message(
        f"Rendered {len(points)} point(s). Open the attached HTML in a browser.",
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


init_db()
migrate_legacy_files()

# Load Discord Token
with open("DiscordToken.txt", "r") as file:
    TOKEN = file.read().strip()

bot.run(TOKEN)