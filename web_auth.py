"""Discord OAuth2 login for the web map viewer.

Only used to establish *who* is viewing the page (their Discord user id) —
scope is kept to 'identify' alone. Per-channel access is then computed
server-side from the bot's own guild/member/channel cache (VectorHandler.bot),
never from Discord API scopes, so no extra OAuth permissions are needed.
"""
import base64
import hashlib
import hmac
import json
import os
import secrets
import time
from urllib.parse import urlencode

import aiohttp
from aiohttp import web

OAUTH_CONFIG_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "oauth_config.json")
SESSION_SECRET_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "session_secret.key")

SESSION_COOKIE_NAME = "gps_session"
SESSION_MAX_AGE_SECONDS = 30 * 24 * 3600  # 30 days
OAUTH_STATE_MAX_AGE_SECONDS = 600  # 10 minutes to complete the Discord login

DISCORD_AUTHORIZE_URL = "https://discord.com/api/oauth2/authorize"
DISCORD_TOKEN_URL = "https://discord.com/api/oauth2/token"
DISCORD_USER_URL = "https://discord.com/api/users/@me"


def load_oauth_config():
    if os.path.exists(OAUTH_CONFIG_PATH):
        with open(OAUTH_CONFIG_PATH, "r", encoding="utf-8") as f:
            return json.load(f)
    cfg = {"client_id": None, "client_secret": None, "redirect_uri": None}
    with open(OAUTH_CONFIG_PATH, "w", encoding="utf-8") as f:
        json.dump(cfg, f, indent=2)
    return cfg


def oauth_configured(cfg) -> bool:
    return bool(cfg.get("client_id") and cfg.get("client_secret") and cfg.get("redirect_uri"))


def get_session_secret() -> bytes:
    if os.path.exists(SESSION_SECRET_PATH):
        with open(SESSION_SECRET_PATH, "rb") as f:
            return f.read()
    key = secrets.token_bytes(32)
    with open(SESSION_SECRET_PATH, "wb") as f:
        f.write(key)
    return key


def _sign(payload: str) -> str:
    sig = hmac.new(get_session_secret(), payload.encode(), hashlib.sha256).hexdigest()
    return f"{payload}:{sig}"


def _unsign(token: str):
    """Verify a value produced by _sign() and return its payload, or None if
    missing/tampered. The payload may itself contain ':' (it was the only
    thing added after signing), so split off just the trailing signature."""
    if not token:
        return None
    payload, _, sig = token.rpartition(":")
    if not payload:
        return None
    expected = hmac.new(get_session_secret(), payload.encode(), hashlib.sha256).hexdigest()
    if not hmac.compare_digest(sig, expected):
        return None
    return payload


def make_session_cookie(user_id: str) -> str:
    expiry = int(time.time()) + SESSION_MAX_AGE_SECONDS
    return _sign(f"{user_id}:{expiry}")


def read_session_user_id(cookie_value: str):
    """Return the Discord user id from a valid, unexpired session cookie, or None."""
    payload = _unsign(cookie_value)
    if payload is None:
        return None
    user_id, _, expiry_str = payload.rpartition(":")
    if not user_id:
        return None
    try:
        if int(expiry_str) < time.time():
            return None
    except ValueError:
        return None
    return user_id


def get_logged_in_user_id(request: web.Request):
    return read_session_user_id(request.cookies.get(SESSION_COOKIE_NAME))


def make_oauth_state(next_path: str) -> str:
    """Signed, expiring token carrying the post-login redirect target, passed
    through Discord as the OAuth 'state' param (also doubles as CSRF
    protection for the callback)."""
    expiry = int(time.time()) + OAUTH_STATE_MAX_AGE_SECONDS
    encoded_next = base64.urlsafe_b64encode(next_path.encode()).decode()
    return _sign(f"{encoded_next}:{expiry}")


def read_oauth_state(state: str):
    """Return the original next_path from a valid state token, or None."""
    payload = _unsign(state)
    if payload is None:
        return None
    encoded_next, _, expiry_str = payload.rpartition(":")
    if not encoded_next:
        return None
    try:
        if int(expiry_str) < time.time():
            return None
        return base64.urlsafe_b64decode(encoded_next.encode()).decode()
    except Exception:
        return None


def build_authorize_url(cfg, state: str) -> str:
    params = {
        "client_id": cfg["client_id"],
        "redirect_uri": cfg["redirect_uri"],
        "response_type": "code",
        "scope": "identify",
        "state": state,
    }
    return f"{DISCORD_AUTHORIZE_URL}?{urlencode(params)}"


async def exchange_code_for_user(cfg, code: str):
    """Exchange an OAuth code for the authenticated Discord user's id.
    Returns (user_id, username); raises on failure."""
    data = {
        "client_id": cfg["client_id"],
        "client_secret": cfg["client_secret"],
        "grant_type": "authorization_code",
        "code": code,
        "redirect_uri": cfg["redirect_uri"],
    }
    async with aiohttp.ClientSession() as session:
        async with session.post(DISCORD_TOKEN_URL, data=data) as resp:
            resp.raise_for_status()
            token_data = await resp.json()

        access_token = token_data["access_token"]
        headers = {"Authorization": f"Bearer {access_token}"}
        async with session.get(DISCORD_USER_URL, headers=headers) as resp:
            resp.raise_for_status()
            user_data = await resp.json()

    return str(user_data["id"]), user_data.get("username")
