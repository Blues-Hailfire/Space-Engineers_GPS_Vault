"""Single consolidated config/secrets file for the Discord bot, the sync
webserver, and the web map's Discord OAuth login — one file to edit instead
of three (DiscordToken.txt, sync_config.json, oauth_config.json, now folded
into bot_config.json). Holds real secrets (bot token, OAuth client secret),
so bot_config.json is gitignored; this module is not.

Auto-creates bot_config.json with null placeholders on first run, same
bootstrap UX the old oauth_config.json had.
"""
import json
import os

CONFIG_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "bot_config.json")

DEFAULTS = {
    "discord_token": None,
    "host": "0.0.0.0",
    "port": 4040,
    "public_host": None,
    "cert_file": None,
    "key_file": None,
    "https_port": None,
    "oauth_client_id": None,
    "oauth_client_secret": None,
    "oauth_redirect_uri": None,
}


def load():
    """The full config dict, merged over DEFAULTS so a partially-filled or
    older file still works. Auto-creates bot_config.json with placeholder
    values if it doesn't exist yet."""
    if os.path.exists(CONFIG_PATH):
        with open(CONFIG_PATH, "r", encoding="utf-8") as f:
            cfg = json.load(f)
        return {**DEFAULTS, **cfg}
    with open(CONFIG_PATH, "w", encoding="utf-8") as f:
        json.dump(DEFAULTS, f, indent=2)
    return dict(DEFAULTS)
