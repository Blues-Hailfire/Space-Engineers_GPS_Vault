"""One-shot installer for the GPS Vault bot. Run it with the Python you want
the bot to use (3.8+), as your normal user — not with sudo; it asks for sudo
itself when it needs it:

    python3 setup.py                 # venv + dependencies + bot_config.json
    python3 setup.py --service       # ...and install/enable a systemd service (Linux)
    python3 setup.py --uninstall-service

Steps:
  1. Creates .venv/ next to this file (if missing) and installs
     requirements.txt into it.
  2. Creates bot_config.json with placeholders if it doesn't exist yet (see
     bot_config.py) and reports what still needs filling in.
  3. Linux only: installs a systemd unit that runs VectorHandler.py from that
     venv, restarts it on failure, and starts it at boot. Asked interactively
     unless --service/--no-service is given. Safe to re-run; it rewrites the
     unit and restarts the service with the current settings.

Stdlib only, since it runs before the venv exists.
"""
import argparse
import getpass
import os
import shutil
import subprocess
import sys
import tempfile
import venv

ROOT = os.path.dirname(os.path.abspath(__file__))
VENV_DIR = os.path.join(ROOT, ".venv")
REQUIREMENTS = os.path.join(ROOT, "requirements.txt")
ENTRY_POINT = os.path.join(ROOT, "VectorHandler.py")
UNIT_DIR = "/etc/systemd/system"
MIN_PYTHON = (3, 8)


def venv_python():
    if os.name == "nt":
        return os.path.join(VENV_DIR, "Scripts", "python.exe")
    return os.path.join(VENV_DIR, "bin", "python")


def run(cmd, sudo=False, check=True):
    if sudo and os.name != "nt" and os.geteuid() != 0:
        if not shutil.which("sudo"):
            raise SystemExit("This step needs root and sudo isn't available - re-run as root.")
        cmd = ["sudo"] + cmd
    print("  $ " + " ".join(cmd), flush=True)
    return subprocess.run(cmd, check=check)


def setup_venv():
    print("[1/3] Python environment")
    if not os.path.exists(venv_python()):
        print(f"  creating virtual environment in {VENV_DIR}")
        try:
            venv.EnvBuilder(with_pip=True).create(VENV_DIR)
        except subprocess.CalledProcessError:
            # Debian/Ubuntu ship venv without ensurepip unless python3-venv is installed.
            shutil.rmtree(VENV_DIR, ignore_errors=True)
            raise SystemExit(
                "Creating the virtual environment failed. On Debian/Ubuntu install the venv "
                "package first (e.g. `sudo apt install python3-venv`) and re-run."
            )
    else:
        print(f"  using existing virtual environment in {VENV_DIR}")
    run([venv_python(), "-m", "pip", "install", "--upgrade", "pip", "--quiet"])
    run([venv_python(), "-m", "pip", "install", "-r", REQUIREMENTS, "--quiet"])


def check_config(mode):
    """Creates bot_config.json if needed. Returns True when the bot has what it
    needs to start in `mode`."""
    print("[2/3] Configuration")
    sys.path.insert(0, ROOT)
    import bot_config

    existed = os.path.exists(bot_config.CONFIG_PATH)
    cfg = bot_config.load()
    if not existed:
        print(f"  created {bot_config.CONFIG_PATH} with placeholder values")

    ready = True
    if mode != "web" and not cfg.get("discord_token"):
        print("  ! discord_token is not set - the bot won't start until it is.")
        ready = False
    if not cfg.get("public_host"):
        print("  - public_host is not set (needed for /create_sync_token to print a usable endpoint).")
    if not cfg.get("oauth_client_id"):
        print("  - Discord OAuth isn't configured (needed for logging into the web map; see README).")
    if ready:
        print("  bot_config.json looks ready.")
    return ready


def service_user():
    # Under sudo, run the service as the user who invoked it, not root.
    return os.environ.get("SUDO_USER") or getpass.getuser()


def unit_text(user, mode):
    return f"""[Unit]
Description=GPS Vault - Space Engineers GPS Manager bot
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User={user}
WorkingDirectory={ROOT}
ExecStart="{venv_python()}" "{ENTRY_POINT}" --mode {mode}
Environment=PYTHONUNBUFFERED=1
Restart=on-failure
RestartSec=10

[Install]
WantedBy=multi-user.target
"""


def install_service(name, mode, start):
    print(f"[3/3] systemd service '{name}'")
    if not shutil.which("systemctl"):
        raise SystemExit("systemctl not found - this system doesn't appear to use systemd.")

    user = service_user()
    if user == "root":
        print("  ! the service will run as root; run setup.py as a normal user to avoid that.")

    unit_path = f"{UNIT_DIR}/{name}.service"
    with tempfile.NamedTemporaryFile("w", suffix=".service", delete=False, encoding="utf-8") as f:
        f.write(unit_text(user, mode))
        tmp = f.name
    try:
        run(["install", "-m", "644", tmp, unit_path], sudo=True)
    finally:
        os.unlink(tmp)

    run(["systemctl", "daemon-reload"], sudo=True)
    run(["systemctl", "enable", name], sudo=True)
    if start:
        run(["systemctl", "restart", name], sudo=True)
        print(f"  started. Logs: journalctl -u {name} -f")
    else:
        print(f"  enabled but not started - fill in bot_config.json, then: sudo systemctl start {name}")


def uninstall_service(name):
    print(f"Removing systemd service '{name}'")
    unit_path = f"{UNIT_DIR}/{name}.service"
    run(["systemctl", "disable", "--now", name], sudo=True, check=False)
    run(["rm", "-f", unit_path], sudo=True)
    run(["systemctl", "daemon-reload"], sudo=True)


def want_service(args):
    if args.service or args.no_service:
        return args.service
    if not sys.stdin.isatty():
        return False
    answer = input("Install GPS Vault as a systemd service (starts at boot)? [Y/n] ").strip().lower()
    return answer in ("", "y", "yes")


def main():
    parser = argparse.ArgumentParser(description="Set up the GPS Vault bot.")
    svc = parser.add_mutually_exclusive_group()
    svc.add_argument("--service", action="store_true", help="Install/update the systemd service (Linux).")
    svc.add_argument("--no-service", action="store_true", help="Skip the systemd service prompt.")
    svc.add_argument("--uninstall-service", action="store_true", help="Stop and remove the systemd service.")
    parser.add_argument("--service-name", default="gps-vault", help="systemd unit name (default: gps-vault).")
    parser.add_argument("--mode", choices=("both", "bot", "web"), default="both",
                        help="VectorHandler.py --mode for the service (default: both).")
    args = parser.parse_args()

    if sys.version_info < MIN_PYTHON:
        raise SystemExit(f"Python {MIN_PYTHON[0]}.{MIN_PYTHON[1]}+ is required.")

    is_linux = sys.platform.startswith("linux")
    if (args.service or args.uninstall_service) and not is_linux:
        raise SystemExit("systemd service options are only supported on Linux.")
    if args.uninstall_service:
        uninstall_service(args.service_name)
        return

    setup_venv()
    ready = check_config(args.mode)

    if not is_linux:
        print("[3/3] Done. Start the bot with:")
        print(f'  "{venv_python()}" VectorHandler.py')
        return

    if want_service(args):
        install_service(args.service_name, args.mode, start=ready)
    else:
        print("[3/3] Skipped service install. Start the bot with:")
        print(f"  {venv_python()} VectorHandler.py")


if __name__ == "__main__":
    main()
