"""HTTP sync API for the Space Engineers client plugin.

Runs alongside the Discord bot (same asyncio event loop) so the plugin,
running on a different machine, can push GPS waypoints and live player
position into this project's gps.db vault.
"""
import asyncio
import json
import os
from html import escape

from aiohttp import web

import web_auth

CONFIG_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "sync_config.json")
DEFAULT_CONFIG = {"host": "0.0.0.0", "port": 8765, "public_host": None}

LIVE_POSITION_PREFIX = "◆ "  # "◆ " — marks live-position points apart from manual waypoints
HOSTILE_SIGNAL_PREFIX = "⚠ "  # "⚠ " — marks hostile-signal points; never a vault waypoint


def load_config():
    if os.path.exists(CONFIG_PATH):
        with open(CONFIG_PATH, "r", encoding="utf-8") as f:
            cfg = json.load(f)
        return {**DEFAULT_CONFIG, **cfg}
    return dict(DEFAULT_CONFIG)


def public_endpoint_hint():
    cfg = load_config()
    host = cfg.get("public_host") or cfg.get("host")
    if host in ("0.0.0.0", "::"):
        host = "<your-server-address>"
    return f"http://{host}:{cfg['port']}"


def _bad_request(message):
    return web.json_response({"error": message}, status=400)


def _unauthorized():
    return web.json_response({"error": "invalid or missing token"}, status=401)


PREDICTION_HORIZON_SECONDS = 600.0  # 10 minutes — "where will they be at this rate"
PREDICTION_STEPS = 30
PREDICTION_ACCEL_RAMP_SECONDS = 20.0  # see _predict_path
PREDICTION_MAX_ACCEL = 50.0  # m/s^2 — generous for SE ship/jetpack accelerations
PREDICTION_MIN_SPEED = 0.5  # m/s — below this, extrapolating is meaningless noise
PREDICTION_STOP_SPEED_THRESHOLD = 0.5  # m/s — "effectively stopped", for capping a decelerating path
FTL_VELOCITY_THRESHOLD = 0.05  # m/s — physics velocity below this is treated as "not reported"


def _stopping_time(vx, vy, vz, ax, ay, az):
    """If the current acceleration is actually decelerating the grid toward a
    stop, return how many seconds until that happens; otherwise None.

    Exact for straight retro-thrust (acceleration anti-parallel to velocity):
    finds the time of minimum speed along v(t) = v + a*t by solving
    d/dt |v(t)|² = 0, which gives t = -(v·a)/|a|². A turn/strafe maneuver
    (acceleration with a large sideways component) also has a well-defined
    "minimum speed" instant, but that minimum won't be near zero — so it's
    only treated as a stop when the speed actually reached there is small."""
    a_sq = ax * ax + ay * ay + az * az
    if a_sq < 1e-9:
        return None

    v_dot_a = vx * ax + vy * ay + vz * az
    if v_dot_a >= 0:
        return None  # not decelerating

    t_min = -v_dot_a / a_sq
    if t_min <= 0:
        return None

    speed_at_min = (
        (vx + ax * t_min) ** 2 + (vy + ay * t_min) ** 2 + (vz + az * t_min) ** 2
    ) ** 0.5
    if speed_at_min > PREDICTION_STOP_SPEED_THRESHOLD:
        return None  # decelerating, but this won't actually bring it to a stop

    return t_min


def _effective_motion(prev_row, last_row):
    """Prefer the plugin's real, engine-reported velocity/acceleration
    (whatever's actually controlled — grid physics when piloting, character
    physics on foot). Some FTL/warp mods move a ship in ways the physics
    engine never reports as velocity at all (e.g. teleport-style jumps) — when
    the reported velocity is ~0, fall back to a velocity derived from the
    change in position between the last two samples, so the map still
    reflects real movement. That fallback can't reliably estimate acceleration
    from only two position samples, so it assumes constant velocity (no
    acceleration) rather than amplifying noise into a wild curve.
    Returns (vx, vy, vz, ax, ay, az)."""
    physics_speed = (last_row["vx"] ** 2 + last_row["vy"] ** 2 + last_row["vz"] ** 2) ** 0.5
    if physics_speed >= FTL_VELOCITY_THRESHOLD:
        return (last_row["vx"], last_row["vy"], last_row["vz"],
                last_row["ax"], last_row["ay"], last_row["az"])

    dt = last_row["recorded_at"] - prev_row["recorded_at"]
    if dt < 1.0:
        return (0.0, 0.0, 0.0, 0.0, 0.0, 0.0)

    return (
        (last_row["x"] - prev_row["x"]) / dt,
        (last_row["y"] - prev_row["y"]) / dt,
        (last_row["z"] - prev_row["z"]) / dt,
        0.0, 0.0, 0.0,
    )


def _predict_path(px, py, pz, vx, vy, vz, ax, ay, az):
    """Extrapolate a path out to PREDICTION_HORIZON_SECONDS (10 minutes) via
    basic kinematics: pos(t) = pos + v*t + 0.5*a*t². Returns [] when there's
    essentially no velocity to extrapolate from — a real "not moving" state
    would otherwise still produce a technically-valid but zero-length,
    invisible line.

    Two things cap how far out this path is allowed to run:
    - It never predicts further than the grid could actually reach in 10
      minutes at the current velocity/acceleration (PREDICTION_HORIZON_SECONDS).
    - If the current acceleration is decelerating the grid, it never predicts
      past the point where that would bring it to a stop — continuing the
      formula beyond that would have it nonsensically reverse and start
      moving backward, which isn't what "decelerating" means.

    Short of that stop (if any), acceleration only compounds for the first
    PREDICTION_ACCEL_RAMP_SECONDS; beyond that the path continues at whatever
    velocity the ramp reached. Sustaining today's acceleration for the entire
    remaining horizon would be physically absurd (nothing keeps thrusting
    indefinitely — a ship hits cruise speed or the player lets off), so this
    reads as "coasts from here" rather than an ever-steepening curve."""
    speed = (vx ** 2 + vy ** 2 + vz ** 2) ** 0.5
    if speed < PREDICTION_MIN_SPEED:
        return []

    accel_mag = (ax ** 2 + ay ** 2 + az ** 2) ** 0.5
    if accel_mag > PREDICTION_MAX_ACCEL:
        scale = PREDICTION_MAX_ACCEL / accel_mag
        ax, ay, az = ax * scale, ay * scale, az * scale

    horizon = PREDICTION_HORIZON_SECONDS
    stop_time = _stopping_time(vx, vy, vz, ax, ay, az)
    if stop_time is not None:
        # A stop is a naturally self-limiting event — the deceleration applies
        # for its whole real duration, not the generic ramp-then-coast cutoff
        # below (that cutoff exists only because *unbounded* acceleration
        # sustained for the full 10-minute horizon would be unrealistic).
        horizon = min(horizon, stop_time)
        ramp = horizon
    else:
        ramp = min(PREDICTION_ACCEL_RAMP_SECONDS, horizon)
    vx_ramp = vx + ax * ramp
    vy_ramp = vy + ay * ramp
    vz_ramp = vz + az * ramp
    px_ramp = px + vx * ramp + 0.5 * ax * ramp * ramp
    py_ramp = py + vy * ramp + 0.5 * ay * ramp * ramp
    pz_ramp = pz + vz * ramp + 0.5 * az * ramp * ramp

    path = []
    for i in range(1, PREDICTION_STEPS + 1):
        t = horizon * i / PREDICTION_STEPS
        if t <= ramp:
            path.append([
                px + vx * t + 0.5 * ax * t * t,
                py + vy * t + 0.5 * ay * t * t,
                pz + vz * t + 0.5 * az * t * t,
            ])
        else:
            t2 = t - ramp
            path.append([
                px_ramp + vx_ramp * t2,
                py_ramp + vy_ramp * t2,
                pz_ramp + vz_ramp * t2,
            ])
    return path


async def _authenticate(request, db_module):
    auth = request.headers.get("Authorization", "")
    token = auth[len("Bearer "):] if auth.startswith("Bearer ") else None
    if not token:
        return None
    return await asyncio.to_thread(db_module.resolve_sync_token, token)


CHANNEL_OVERRIDE_HEADER = "X-Channel-Id"


async def _resolve_channel(request, db_module, guild_id, default_channel_id):
    """A token authorizes its whole guild, not just the channel it was
    created in — the plugin can target any *other* channel the bot is bound
    to in that same guild via the X-Channel-Id header (its "Channel ID"
    setting). Returns (channel_id, error_response); error_response is None
    on success."""
    override = request.headers.get(CHANNEL_OVERRIDE_HEADER)
    if not override:
        return default_channel_id, None

    try:
        override_id = int(override)
    except ValueError:
        return None, _bad_request(f"invalid {CHANNEL_OVERRIDE_HEADER} header")

    bound = await asyncio.to_thread(db_module.get_bound_channels, guild_id)
    if override_id not in bound:
        return None, _bad_request(
            f"{CHANNEL_OVERRIDE_HEADER} is not a channel this bot is bound to in your guild"
        )
    return override_id, None


def _require_login(request: web.Request) -> str:
    """Return the logged-in Discord user id, or raise a redirect to /login
    (which resumes at the current URL after a successful login)."""
    user_id = web_auth.get_logged_in_user_id(request)
    if user_id is None:
        next_url = f"{request.path}?{request.query_string}" if request.query_string else request.path
        raise web.HTTPFound(f"/login?next={next_url}")
    return user_id


def make_app(db_module):
    """Build the aiohttp app. `db_module` is VectorHandler, passed in to avoid
    a circular import (VectorHandler imports this module to start the server)."""

    async def login(request: web.Request) -> web.StreamResponse:
        cfg = web_auth.load_oauth_config()
        if not web_auth.oauth_configured(cfg):
            return web.Response(
                text="<p style='font-family:sans-serif;padding:2em'>"
                     "Login isn't configured yet. An admin needs to fill in "
                     "<code>oauth_config.json</code> (client_id / client_secret / "
                     "redirect_uri, from the bot's application in the Discord "
                     "Developer Portal) and restart the bot.</p>",
                content_type="text/html",
                status=503,
            )
        next_path = request.query.get("next", "/map")
        state = web_auth.make_oauth_state(next_path)
        raise web.HTTPFound(web_auth.build_authorize_url(cfg, state))

    async def oauth_callback(request: web.Request) -> web.StreamResponse:
        cfg = web_auth.load_oauth_config()
        code = request.query.get("code")
        state = request.query.get("state")
        next_path = web_auth.read_oauth_state(state) or "/map"
        if not code:
            return web.Response(text="Missing 'code' from Discord.", status=400)

        try:
            user_id, username = await web_auth.exchange_code_for_user(cfg, code)
        except Exception as e:
            return web.Response(text=f"Discord login failed: {e}", status=502)

        response = web.HTTPFound(next_path)
        response.set_cookie(
            web_auth.SESSION_COOKIE_NAME,
            web_auth.make_session_cookie(user_id),
            max_age=web_auth.SESSION_MAX_AGE_SECONDS,
            httponly=True,
            samesite="Lax",
        )
        raise response

    async def logout(request: web.Request) -> web.StreamResponse:
        response = web.HTTPFound("/map")
        response.del_cookie(web_auth.SESSION_COOKIE_NAME)
        raise response

    async def sync_gps(request: web.Request) -> web.StreamResponse:
        scope = await _authenticate(request, db_module)
        if scope is None:
            return _unauthorized()
        guild_id, channel_id = scope
        channel_id, err = await _resolve_channel(request, db_module, guild_id, channel_id)
        if err:
            return err

        try:
            body = await request.json()
        except json.JSONDecodeError:
            return _bad_request("invalid JSON body")

        points = body.get("points")
        if not isinstance(points, list) or not points:
            return _bad_request("'points' must be a non-empty list")

        vectors = []
        errors = []
        for p in points:
            try:
                name = str(p["name"])[:32]
                x, y, z = float(p["x"]), float(p["y"]), float(p["z"])
                color = p.get("color") or db_module.DEFAULT_GPS_COLOR
            except (KeyError, TypeError, ValueError) as e:
                errors.append(str(e))
                continue
            vectors.append(db_module.Vector3D(name, x, y, z, color))

        deleted = 0
        if vectors:
            deleted = await asyncio.to_thread(
                db_module.upsert_and_prune_gps_points, guild_id, channel_id, vectors
            )

        return web.json_response({"upserted": len(vectors), "deleted": deleted, "errors": errors})

    async def sync_planets(request: web.Request) -> web.StreamResponse:
        scope = await _authenticate(request, db_module)
        if scope is None:
            return _unauthorized()
        guild_id, channel_id = scope
        channel_id, err = await _resolve_channel(request, db_module, guild_id, channel_id)
        if err:
            return err

        try:
            body = await request.json()
        except json.JSONDecodeError:
            return _bad_request("invalid JSON body")

        planets = body.get("planets")
        if not isinstance(planets, list) or not planets:
            return _bad_request("'planets' must be a non-empty list")

        parsed = []
        errors = []
        for p in planets:
            try:
                name = str(p["name"])[:64]
                x, y, z = float(p["x"]), float(p["y"]), float(p["z"])
                radius = float(p["radius"])
            except (KeyError, TypeError, ValueError) as e:
                errors.append(str(e))
                continue
            parsed.append({"name": name, "x": x, "y": y, "z": z, "radius": radius})

        if parsed:
            await asyncio.to_thread(db_module.upsert_planets, guild_id, channel_id, parsed)

        return web.json_response({"upserted": len(parsed), "errors": errors})

    async def sync_hostiles(request: web.Request) -> web.StreamResponse:
        """Push direction: the plugin reports whatever hostile signals are
        currently visible to the player's HUD (antenna/sensor detections),
        every tick. Unlike sync_gps, an empty/partial list never deletes
        anything — a signal only disappears once get_hostile_signals prunes
        it for going HOSTILE_SIGNAL_RETENTION_SECONDS without being
        re-reported, same as the game closing (this request) leaving whatever
        was last seen to expire on its own."""
        scope = await _authenticate(request, db_module)
        if scope is None:
            return _unauthorized()
        guild_id, channel_id = scope
        channel_id, err = await _resolve_channel(request, db_module, guild_id, channel_id)
        if err:
            return err

        try:
            body = await request.json()
        except json.JSONDecodeError:
            return _bad_request("invalid JSON body")

        signals = body.get("signals")
        if not isinstance(signals, list):
            return _bad_request("'signals' must be a list")

        parsed = []
        errors = []
        for s in signals:
            try:
                signal_id = int(s["signal_id"])
                name = str(s["name"])[:32]
                x, y, z = float(s["x"]), float(s["y"]), float(s["z"])
                vx = float(s.get("vx") or 0.0)
                vy = float(s.get("vy") or 0.0)
                vz = float(s.get("vz") or 0.0)
                ax = float(s.get("ax") or 0.0)
                ay = float(s.get("ay") or 0.0)
                az = float(s.get("az") or 0.0)
            except (KeyError, TypeError, ValueError) as e:
                errors.append(str(e))
                continue
            parsed.append((signal_id, name, x, y, z, vx, vy, vz, ax, ay, az))

        for signal_id, name, x, y, z, vx, vy, vz, ax, ay, az in parsed:
            await asyncio.to_thread(db_module.upsert_hostile_signal, guild_id, channel_id, signal_id, name, x, y, z)
            await asyncio.to_thread(
                db_module.record_hostile_signal_history, guild_id, channel_id, signal_id, x, y, z, vx, vy, vz, ax, ay, az
            )

        return web.json_response({"upserted": len(parsed), "errors": errors})

    async def fetch_gps(request: web.Request) -> web.StreamResponse:
        """Pull direction: the plugin polls this to learn about GPS points
        added/edited in Discord (or by any other player) so it can push them
        into the local player's in-game GPS list. Live position markers are
        excluded — those are ephemeral per-player state, not vault waypoints."""
        scope = await _authenticate(request, db_module)
        if scope is None:
            return _unauthorized()
        guild_id, channel_id = scope
        channel_id, err = await _resolve_channel(request, db_module, guild_id, channel_id)
        if err:
            return err

        points = await asyncio.to_thread(db_module.load_vectors, guild_id, channel_id)
        payload = [
            {
                "name": v.name,
                "x": v.x,
                "y": v.y,
                "z": v.z,
                "color": db_module.normalize_argb_hex(v.color),
            }
            for _, _, v in points
            if not v.name.startswith(LIVE_POSITION_PREFIX)
        ]
        return web.json_response({"points": payload})

    async def fetch_players(request: web.Request) -> web.StreamResponse:
        """Other players' current live positions in this channel (reported
        within the last 5 minutes), for the plugin's /vault players ("/vp")
        command. Excludes the requesting player's own name via ?exclude=."""
        scope = await _authenticate(request, db_module)
        if scope is None:
            return _unauthorized()
        guild_id, channel_id = scope
        channel_id, err = await _resolve_channel(request, db_module, guild_id, channel_id)
        if err:
            return err

        exclude_name = request.query.get("exclude")
        rows = await asyncio.to_thread(
            db_module.get_active_live_players, guild_id, channel_id, exclude_name
        )
        payload = [{"name": r["player_name"], "x": r["x"], "y": r["y"], "z": r["z"]} for r in rows]
        return web.json_response({"players": payload})

    async def sync_position(request: web.Request) -> web.StreamResponse:
        scope = await _authenticate(request, db_module)
        if scope is None:
            return _unauthorized()
        guild_id, channel_id = scope
        channel_id, err = await _resolve_channel(request, db_module, guild_id, channel_id)
        if err:
            return err

        try:
            body = await request.json()
            player_name = str(body["player_name"])[:32 - len(LIVE_POSITION_PREFIX)]
            x, y, z = float(body["x"]), float(body["y"]), float(body["z"])
            color = body.get("color") or db_module.DEFAULT_GPS_COLOR
            vx = float(body.get("vx") or 0.0)
            vy = float(body.get("vy") or 0.0)
            vz = float(body.get("vz") or 0.0)
            ax = float(body.get("ax") or 0.0)
            ay = float(body.get("ay") or 0.0)
            az = float(body.get("az") or 0.0)
        except (json.JSONDecodeError, KeyError, TypeError, ValueError) as e:
            return _bad_request(f"invalid body: {e}")

        name = f"{LIVE_POSITION_PREFIX}{player_name}"
        vector = db_module.Vector3D(name, x, y, z, color)
        await asyncio.to_thread(db_module.upsert_gps_point, guild_id, channel_id, vector)
        await asyncio.to_thread(
            db_module.record_position_history, guild_id, channel_id, player_name, x, y, z, vx, vy, vz, ax, ay, az
        )
        return web.json_response({"ok": True})

    def _trail_extras_from_rows(rows):
        """Build {"trail", "velocity", "prediction", "last_seen_at"} from a
        list of position-history-shaped rows (x, y, z, vx, vy, vz, ax, ay, az,
        recorded_at), oldest first. Shared by live players and hostile
        signals — both are tracked via the same trail/predict/dead-reckon
        mechanism, just backed by different history tables. "prediction" is a
        short dashed-line forecast, extrapolated from last-observed velocity
        and acceleration (basic kinematics: pos + v*t + 0.5*a*t²).
        "last_seen_at" is the unix timestamp of the most recent sample, so the
        client can show/tick up "how long since we last heard from this"."""
        trail = [[r["x"], r["y"], r["z"]] for r in rows]
        velocity = None
        prediction = []
        last_seen_at = None
        if rows:
            last = rows[-1]
            if len(rows) >= 2:
                vx, vy, vz, ax, ay, az = _effective_motion(rows[-2], last)
                derived = (vx, vy, vz) != (last["vx"], last["vy"], last["vz"])
            else:
                vx, vy, vz, ax, ay, az = last["vx"], last["vy"], last["vz"], last["ax"], last["ay"], last["az"]
                derived = False
            speed = (vx ** 2 + vy ** 2 + vz ** 2) ** 0.5
            # ax/ay/az are included so the client can dead-reckon this
            # marker's on-screen position between poll ticks instead of only
            # updating it every few seconds.
            velocity = {
                "vx": vx, "vy": vy, "vz": vz,
                "ax": ax, "ay": ay, "az": az,
                "speed": speed, "derived": derived,
            }
            last_seen_at = last["recorded_at"]
            prediction = _predict_path(last["x"], last["y"], last["z"], vx, vy, vz, ax, ay, az)
        return {
            "trail": trail,
            "velocity": velocity,
            "prediction": prediction,
            "last_seen_at": last_seen_at,
        }

    async def live_extras_for(points, guild_id, channel_id):
        """Map each live-position point's name to its trail extras (see
        _trail_extras_from_rows), sourced from position_history."""
        extras = {}
        for _, _, v in points:
            if not v.name.startswith(LIVE_POSITION_PREFIX):
                continue
            player_name = v.name[len(LIVE_POSITION_PREFIX):]
            rows = await asyncio.to_thread(db_module.get_position_trail, guild_id, channel_id, player_name)
            extras[v.name] = _trail_extras_from_rows(rows)
        return extras

    async def hostile_points_and_extras_for(guild_id, channel_id):
        """Currently-known hostile signals as (points, extras) in the same
        shape live_extras_for produces — points get merged into the vault
        point list, extras keyed by the same prefixed name so the client
        renders/tracks them exactly like a live player marker."""
        rows = await asyncio.to_thread(db_module.get_hostile_signals, guild_id, channel_id)
        points = []
        extras = {}
        for r in rows:
            name = f"{HOSTILE_SIGNAL_PREFIX}{r['name']}"
            points.append(db_module.Vector3D(name, r["x"], r["y"], r["z"], r["color"]))
            hist = await asyncio.to_thread(
                db_module.get_hostile_signal_trail, guild_id, channel_id, r["signal_id"]
            )
            extras[name] = _trail_extras_from_rows(hist)
        return points, extras

    async def map_live(request: web.Request) -> web.StreamResponse:
        user_id = _require_login(request)
        try:
            guild_id = int(request.query["guild_id"])
            channel_id = int(request.query["channel_id"])
        except (KeyError, ValueError):
            return _bad_request("missing or invalid guild_id/channel_id")

        if not await asyncio.to_thread(db_module.user_can_access_channel, user_id, guild_id, channel_id):
            return web.json_response({"error": "forbidden"}, status=403)

        points = await asyncio.to_thread(db_module.load_vectors, guild_id, channel_id)
        hostile_points, hostile_extras = await hostile_points_and_extras_for(guild_id, channel_id)
        all_points = [v for _, _, v in points] + hostile_points
        payload_points = [
            {
                "name": v.name,
                "x": v.x,
                "y": v.y,
                "z": v.z,
                "color": db_module.argb_to_css_hex(v.color),
            }
            for v in all_points
        ]
        extras = await live_extras_for(points, guild_id, channel_id)
        extras.update(hostile_extras)
        planet_rows = await asyncio.to_thread(db_module.get_planets, guild_id, channel_id)
        payload_planets = [
            {"name": p["name"], "x": p["x"], "y": p["y"], "z": p["z"], "radius": p["radius"]}
            for p in planet_rows
        ]
        return web.json_response({"points": payload_points, "extras": extras, "planets": payload_planets})

    async def map_frame(request: web.Request) -> web.StreamResponse:
        user_id = _require_login(request)
        try:
            guild_id = int(request.query["guild_id"])
            channel_id = int(request.query["channel_id"])
        except (KeyError, ValueError):
            return web.Response(text="Missing or invalid guild_id/channel_id", status=400)

        if not await asyncio.to_thread(db_module.user_can_access_channel, user_id, guild_id, channel_id):
            return web.Response(text="You don't have access to this channel.", status=403)

        points = await asyncio.to_thread(db_module.load_vectors, guild_id, channel_id)
        planet_rows = await asyncio.to_thread(db_module.get_planets, guild_id, channel_id)
        hostile_points, hostile_extras = await hostile_points_and_extras_for(guild_id, channel_id)
        all_points = points + [(0, None, v) for v in hostile_points]
        if not all_points and not planet_rows:
            return web.Response(text=_EMPTY_MAP_HTML, content_type="text/html")

        channel = db_module.bot.get_channel(channel_id)
        title = f"#{channel.name}" if channel else str(channel_id)
        extras = await live_extras_for(points, guild_id, channel_id)
        extras.update(hostile_extras)
        planets = [
            {"name": p["name"], "x": p["x"], "y": p["y"], "z": p["z"], "radius": p["radius"]}
            for p in planet_rows
        ]
        # guild_id/channel_id are Discord snowflakes (64-bit) — larger than
        # JS's safe integer range (2^53). Embedding them as bare numeric
        # literals in the page's `const LIVE = {...}` gets silently rounded
        # by the JS engine, so every /map/live poll queries the wrong
        # (rounded) id, gets an empty point list back, and the client's
        # reconciliation logic then hides every point it can't find in that
        # empty response. Stringifying avoids any numeric parsing entirely.
        live_config = {"guildId": str(guild_id), "channelId": str(channel_id), "pollMs": 3000}
        html = await asyncio.to_thread(
            db_module.build_map_html, all_points, title,
            live_extra=extras, live_config=live_config, planets=planets,
        )
        return web.Response(text=html, content_type="text/html")

    async def edit_point(request: web.Request) -> web.StreamResponse:
        """Rename/recolor a vault GPS point from the web map's editor. Uses
        the same login+channel-view gate as viewing the map, matching how
        lenient the equivalent Discord commands (/add_gps, /revise) already
        are — no extra permission beyond being able to see the channel."""
        user_id = _require_login(request)

        try:
            body = await request.json()
            guild_id = int(body["guild_id"])
            channel_id = int(body["channel_id"])
            old_name = str(body["old_name"])
            new_name = str(body["new_name"]).strip()[:32]
            color = str(body["color"])
        except (json.JSONDecodeError, KeyError, TypeError, ValueError):
            return _bad_request("invalid request body")

        if not new_name:
            return _bad_request("name can't be empty")
        if old_name.startswith(LIVE_POSITION_PREFIX) or new_name.startswith(LIVE_POSITION_PREFIX):
            return _bad_request("live position markers can't be edited")

        if not await asyncio.to_thread(db_module.user_can_access_channel, user_id, guild_id, channel_id):
            return web.json_response({"error": "forbidden"}, status=403)

        found = await asyncio.to_thread(
            db_module.edit_gps_point, guild_id, channel_id, old_name, new_name, color
        )
        if not found:
            return _bad_request("point not found")
        return web.json_response({"ok": True})

    async def map_page(request: web.Request) -> web.StreamResponse:
        user_id = _require_login(request)
        channels = await asyncio.to_thread(db_module.list_bound_channels_with_names, user_id)
        if not channels:
            return web.Response(
                text="<p style='font-family:sans-serif;padding:2em'>"
                     "No channels you have access to are bound yet — run <code>/bind</code> "
                     "in a Discord channel you can read first.</p>",
                content_type="text/html",
            )

        try:
            sel_guild = int(request.query.get("guild_id", ""))
            sel_channel = int(request.query.get("channel_id", ""))
            if not any(c["guild_id"] == sel_guild and c["channel_id"] == sel_channel for c in channels):
                raise ValueError
        except ValueError:
            sel_guild, sel_channel = channels[0]["guild_id"], channels[0]["channel_id"]

        options = "\n".join(
            '<option value="{g}:{c}"{sel}>{label}</option>'.format(
                g=chan["guild_id"],
                c=chan["channel_id"],
                sel=" selected" if chan["guild_id"] == sel_guild and chan["channel_id"] == sel_channel else "",
                label=escape(chan["label"]),
            )
            for chan in channels
        )

        html = (_MAP_PAGE_TEMPLATE
                .replace("__OPTIONS__", options)
                .replace("__GUILD__", str(sel_guild))
                .replace("__CHANNEL__", str(sel_channel)))
        return web.Response(text=html, content_type="text/html")

    app = web.Application()
    app.add_routes([
        web.post("/sync/gps", sync_gps),
        web.get("/sync/gps", fetch_gps),
        web.get("/sync/players", fetch_players),
        web.post("/sync/planets", sync_planets),
        web.post("/sync/position", sync_position),
        web.post("/sync/hostiles", sync_hostiles),
        web.get("/login", login),
        web.get("/oauth/callback", oauth_callback),
        web.get("/logout", logout),
        web.get("/map", map_page),
        web.get("/map/frame", map_frame),
        web.get("/map/live", map_live),
        web.post("/map/edit_point", edit_point),
    ])
    return app


_EMPTY_MAP_HTML = (
    "<html><body style=\"background:#0b0d10;color:#9aa4b2;font-family:system-ui,sans-serif;"
    "display:flex;align-items:center;justify-content:center;height:100vh;margin:0\">"
    "No GPS points in this channel yet.</body></html>"
)

_MAP_PAGE_TEMPLATE = """<!doctype html>
<html>
<head>
<meta charset="utf-8">
<title>GPS Sync — Live Map</title>
<style>
  html, body { margin: 0; height: 100%; background: #0b0d10; font-family: system-ui, sans-serif; }
  #bar {
    display: flex; align-items: center; gap: 10px; padding: 10px 14px;
    background: #14171c; border-bottom: 1px solid #2a2f37; box-sizing: border-box;
  }
  #bar label { color: #9aa4b2; font-size: 13px; }
  #bar select {
    background: #1c2027; color: #e6e9ef; border: 1px solid #333a45;
    border-radius: 6px; padding: 6px 10px; font-size: 14px;
  }
  #bar a { color: #9aa4b2; font-size: 13px; margin-left: auto; text-decoration: none; }
  #bar a:hover { color: #e6e9ef; }
  iframe { border: 0; width: 100%; height: calc(100% - 49px); display: block; }
</style>
</head>
<body>
  <div id="bar">
    <label for="channelSelect">Channel</label>
    <select id="channelSelect">
__OPTIONS__
    </select>
    <a href="/logout">Log out</a>
  </div>
  <iframe id="mapFrame" src="/map/frame?guild_id=__GUILD__&channel_id=__CHANNEL__"></iframe>
  <script>
    var sel = document.getElementById('channelSelect');
    var frame = document.getElementById('mapFrame');
    sel.addEventListener('change', function () {
      var parts = sel.value.split(':');
      var g = parts[0], c = parts[1];
      frame.src = '/map/frame?guild_id=' + g + '&channel_id=' + c;
      history.replaceState(null, '', '/map?guild_id=' + g + '&channel_id=' + c);
    });
  </script>
</body>
</html>
"""


async def start(db_module):
    cfg = load_config()
    app = make_app(db_module)
    runner = web.AppRunner(app)
    await runner.setup()
    site = web.TCPSite(runner, cfg["host"], cfg["port"])
    await site.start()
    print(f"[sync_api] listening on {cfg['host']}:{cfg['port']}")
    return runner
