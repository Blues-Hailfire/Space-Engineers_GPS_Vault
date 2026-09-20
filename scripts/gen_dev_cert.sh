#!/usr/bin/env bash
# Generates a self-signed TLS cert for LAN/dev testing of sync_api.py.
#
# Not for production: browsers/HttpClient won't trust it unless you import
# certs/dev.crt into the client machine's trust store yourself. Once a real
# domain exists, replace certs/dev.{crt,key} with a CA-issued cert (e.g. via
# Let's Encrypt / certbot, or a reverse proxy like Caddy that gets one
# automatically) and point sync_config.json's cert_file/key_file at those
# instead — no code changes needed either way.
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.."
mkdir -p certs

# Include the LAN IP if given, e.g.: ./scripts/gen_dev_cert.sh 192.168.1.50
SAN="DNS:localhost,IP:127.0.0.1"
if [ "${1:-}" != "" ]; then
  SAN="${SAN},IP:${1}"
fi

# MSYS_NO_PATHCONV: Git Bash on Windows otherwise mangles "/CN=..." into a path.
MSYS_NO_PATHCONV=1 openssl req -x509 -newkey rsa:2048 -sha256 -days 825 -nodes \
  -keyout certs/dev.key -out certs/dev.crt \
  -subj "/CN=gps-sync-dev" \
  -addext "subjectAltName=${SAN}"

echo
echo "Wrote certs/dev.crt and certs/dev.key."
echo "Set sync_config.json: \"cert_file\": \"certs/dev.crt\", \"key_file\": \"certs/dev.key\""
echo "Then, on each client machine, import certs/dev.crt into the trusted root store"
echo "(Windows: certutil -addstore -f Root certs/dev.crt) so the plugin's HttpClient"
echo "accepts it — same trust model as production, just a locally-issued cert."
