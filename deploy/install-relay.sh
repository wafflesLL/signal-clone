#!/usr/bin/env bash
# Installs the E2EE relay as a systemd service on Ubuntu 24.04.
#
#   sudo bash install-relay.sh relay.tgz
#
# relay.tgz is a framework-dependent linux-x64 publish of src/E2ee.Server:
#   dotnet publish src/E2ee.Server -c Release -r linux-x64 --self-contained false -o out
#   COPYFILE_DISABLE=1 tar -C out --no-xattrs --exclude='*.pdb' -czf relay.tgz .   # (macOS: skip Finder metadata)
#
# The relay listens only on 127.0.0.1:8000; put an HTTPS front (Caddy or
# Tailscale Funnel) in front of it. Its database and attachment blobs live in
# /var/lib/e2ee-relay, which survives restarts and upgrades. Safe to re-run to upgrade.
set -euo pipefail

PKG="${1:?usage: sudo bash install-relay.sh relay.tgz}"
[[ $EUID -eq 0 ]] || { echo "Run with sudo." >&2; exit 1; }

echo "==> Installing the ASP.NET Core 8 runtime"
apt-get update -q
DEBIAN_FRONTEND=noninteractive apt-get install -y -q aspnetcore-runtime-8.0

echo "==> Creating the e2ee-relay user and directories"
id e2ee-relay >/dev/null 2>&1 || useradd --system --home /var/lib/e2ee-relay --shell /usr/sbin/nologin e2ee-relay
install -d -o e2ee-relay -g e2ee-relay -m 700 /var/lib/e2ee-relay
install -d -m 755 /opt/e2ee-relay

echo "==> Unpacking the relay into /opt/e2ee-relay"
staging=$(mktemp -d)
tar -C "$staging" -xzf "$PKG"
# A running binary can't be overwritten ("Text file busy"); stop the old relay first.
systemctl stop e2ee-relay 2>/dev/null || true
cp -a "$staging"/. /opt/e2ee-relay/
rm -r "$staging"
chown -R root:root /opt/e2ee-relay
chmod 755 /opt/e2ee-relay/E2ee.Server

echo "==> Writing the systemd service"
cat > /etc/systemd/system/e2ee-relay.service <<'UNIT'
[Unit]
Description=E2EE messenger relay
After=network-online.target
Wants=network-online.target

[Service]
User=e2ee-relay
Group=e2ee-relay
WorkingDirectory=/var/lib/e2ee-relay
Environment=E2EE_DB=/var/lib/e2ee-relay/relay.sqlite3
Environment=DOTNET_CLI_TELEMETRY_OPTOUT=1
Environment=ASPNETCORE_ENVIRONMENT=Production
ExecStart=/opt/e2ee-relay/E2ee.Server --urls http://127.0.0.1:8000
Restart=always
RestartSec=3

# Hardening: the relay only needs its own data directory.
NoNewPrivileges=true
ProtectSystem=strict
ProtectHome=true
PrivateTmp=true
PrivateDevices=true
ReadWritePaths=/var/lib/e2ee-relay
ProtectKernelTunables=true
ProtectKernelModules=true
ProtectControlGroups=true
RestrictSUIDSGID=true
LockPersonality=true

[Install]
WantedBy=multi-user.target
UNIT

systemctl daemon-reload
systemctl enable e2ee-relay >/dev/null
systemctl restart e2ee-relay

echo "==> SSH: keys only (no password logins)"
cat > /etc/ssh/sshd_config.d/00-keys-only.conf <<'SSH'
PasswordAuthentication no
KbdInteractiveAuthentication no
SSH
systemctl reload ssh

sleep 3
if systemctl is-active --quiet e2ee-relay; then
  echo "==> Relay is running on 127.0.0.1:8000 (data: /var/lib/e2ee-relay)"
else
  echo "Relay failed to start:" >&2
  journalctl -u e2ee-relay -n 30 --no-pager >&2
  exit 1
fi
