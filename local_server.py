"""Standalone local copy of the GPS Vault web server, for the Datalink
plugin's "Host Locally" toggle. Built into GpsVaultLocalServer.exe by
.github/workflows/local-server.yml and downloaded/launched by the plugin
(Pulsar_Plugin/LocalServerHost.cs) — not meant to be run by hand, though
nothing stops you:

    set GPSVAULT_LOCAL_TOKEN=some-secret
    python local_server.py --data-dir .\\local-data --port 4040

No Discord connection at all. Runs sync_api in LOCAL_MODE: listens on
127.0.0.1 only, authenticates the plugin with the single token passed via
the GPSVAULT_LOCAL_TOKEN environment variable (not argv, so it doesn't show
up in the process list), and serves the map without a Discord login. One
synthetic "Local" guild/channel holds everything.

Every file the server writes (VaultDatabase.db, bot_config.json, session_secret.key,
guild_data/) goes under --data-dir: when frozen by PyInstaller the modules'
own __file__-relative paths point into a throwaway temp extraction folder.
"""
import argparse
import asyncio
import os
import sys

LOCAL_GUILD_ID = 1
LOCAL_CHANNEL_ID = 1


def main():
    parser = argparse.ArgumentParser(description="GPS Vault local web server (Datalink 'Host Locally').")
    parser.add_argument("--data-dir", required=True, help="Folder for the database and other persistent files.")
    parser.add_argument("--port", type=int, default=4040)
    args = parser.parse_args()

    token = os.environ.get("GPSVAULT_LOCAL_TOKEN", "").strip()
    if not token:
        raise SystemExit("GPSVAULT_LOCAL_TOKEN is not set — refusing to start an unauthenticated server.")

    data_dir = os.path.abspath(args.data_dir)
    os.makedirs(data_dir, exist_ok=True)
    os.chdir(data_dir)

    # Redirect every persistent path before anything reads it.
    import bot_config
    import web_auth
    bot_config.CONFIG_PATH = os.path.join(data_dir, "bot_config.json")
    web_auth.SESSION_SECRET_PATH = os.path.join(data_dir, "session_secret.key")

    import VectorHandler
    import sync_api
    VectorHandler.DB_PATH = os.path.join(data_dir, "VaultDatabase.db")
    VectorHandler.LOCAL_ACCESS = True

    VectorHandler.init_db()
    VectorHandler.set_bind_channel(LOCAL_GUILD_ID, LOCAL_CHANNEL_ID)
    sync_api.enable_local_mode(token, LOCAL_GUILD_ID, LOCAL_CHANNEL_ID, args.port)

    print(f"[local_server] data dir: {data_dir}", flush=True)
    asyncio.run(VectorHandler._run_web_only())


if __name__ == "__main__":
    sys.exit(main())
