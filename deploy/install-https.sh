#!/usr/bin/env bash
# Puts Caddy in front of the relay for automatic HTTPS, and keeps a DuckDNS
# name pointed at this network's public IP.
#
#   sudo bash install-https.sh signal-clone.duckdns.org
#
# The router forwards external port 8443 to this machine's port 8443, where
# Caddy listens. Because ports 80/443 aren't reachable from the internet, Caddy proves it owns
# the name with a DNS challenge through DuckDNS, so it needs a Caddy build with
# the caddy-dns/duckdns plugin (downloaded from caddyserver.com, Caddy's
# official build service) and your DuckDNS token. The script asks for the
# token without echoing it, and stores it root-only in /etc/duckdns/env.
#
# Run install-relay.sh first. Safe to re-run.
set -euo pipefail

DOMAIN="${1:?usage: sudo bash install-https.sh yourname.duckdns.org}"
[[ $EUID -eq 0 ]] || { echo "Run with sudo." >&2; exit 1; }
[[ "$DOMAIN" =~ ^[a-z0-9-]+\.duckdns\.org$ ]] || { echo "Expected a name like yourname.duckdns.org" >&2; exit 1; }
SUB="${DOMAIN%.duckdns.org}"

install -d -m 700 /etc/duckdns
if [[ -z "${DUCKDNS_TOKEN:-}" && -f /etc/duckdns/env ]]; then
  DUCKDNS_TOKEN=$(sed -n 's/^DUCKDNS_TOKEN=//p' /etc/duckdns/env)
fi
if [[ -z "${DUCKDNS_TOKEN:-}" ]]; then
  read -rsp "DuckDNS token (from duckdns.org, not shown): " DUCKDNS_TOKEN; echo
fi
[[ "$DUCKDNS_TOKEN" =~ ^[0-9a-f-]{36}$ ]] || { echo "That doesn't look like a DuckDNS token." >&2; exit 1; }
printf 'DUCKDNS_DOMAIN=%s\nDUCKDNS_TOKEN=%s\n' "$SUB" "$DUCKDNS_TOKEN" > /etc/duckdns/env
chmod 600 /etc/duckdns/env

echo "==> Pointing $DOMAIN at this network's public IP"
cat > /etc/systemd/system/duckdns.service <<'UNIT'
[Unit]
Description=Update DuckDNS with this network's public IP
After=network-online.target
Wants=network-online.target

[Service]
Type=oneshot
EnvironmentFile=/etc/duckdns/env
DynamicUser=true
ExecStart=/bin/sh -c 'r=$(curl -fsS --max-time 30 "https://www.duckdns.org/update?domains=$DUCKDNS_DOMAIN&token=$DUCKDNS_TOKEN&ip="); echo "duckdns: $r"; [ "$r" = OK ]'
UNIT
cat > /etc/systemd/system/duckdns.timer <<'UNIT'
[Unit]
Description=Update DuckDNS every 5 minutes

[Timer]
OnBootSec=1min
OnUnitActiveSec=5min

[Install]
WantedBy=timers.target
UNIT
systemctl daemon-reload
systemctl enable --now duckdns.timer >/dev/null
systemctl start duckdns.service || { echo "DuckDNS rejected the update; check the token." >&2; exit 1; }

echo "==> Installing Caddy (Ubuntu package, for its service and user)"
apt-get update -q
DEBIAN_FRONTEND=noninteractive apt-get install -y -q caddy curl

echo "==> Replacing the binary with Caddy + DuckDNS plugin (slow link: a few minutes)"
tmp=$(mktemp)
curl -fL --retry 3 --max-time 1200 -o "$tmp" \
  "https://caddyserver.com/api/download?os=linux&arch=amd64&p=github.com/caddy-dns/duckdns"
chmod 755 "$tmp"
"$tmp" list-modules | grep -q '^dns.providers.duckdns$' || { echo "Downloaded Caddy lacks the DuckDNS module." >&2; exit 1; }
# Keep apt from overwriting the custom binary on upgrades (Caddy's documented approach).
if ! dpkg-divert --list /usr/bin/caddy | grep -q caddy.default; then
  dpkg-divert --divert /usr/bin/caddy.default --rename /usr/bin/caddy
fi
mv "$tmp" /usr/bin/caddy
chown root:root /usr/bin/caddy

echo "==> Configuring Caddy for $DOMAIN"
install -d /etc/systemd/system/caddy.service.d
cat > /etc/systemd/system/caddy.service.d/duckdns.conf <<'UNIT'
[Service]
EnvironmentFile=/etc/duckdns/env
UNIT
cat > /etc/caddy/Caddyfile <<CADDY
{
	# Nothing reaches port 80 from outside; don't bother redirecting it.
	auto_https disable_redirects
	# The router forwards external 8443 straight to 8443 here.
	https_port 8443
}

$DOMAIN:8443 {
	tls {
		dns duckdns {env.DUCKDNS_TOKEN}
		resolvers 1.1.1.1 8.8.8.8
	}
	# Attachments can be up to 100 MB (plus encryption overhead).
	request_body {
		max_size 101MB
	}
	reverse_proxy 127.0.0.1:8000
}
CADDY
/usr/bin/caddy validate --config /etc/caddy/Caddyfile --adapter caddyfile >/dev/null
systemctl daemon-reload
systemctl enable caddy >/dev/null
systemctl restart caddy

echo "==> Waiting for the HTTPS certificate (DNS challenge, up to ~3 minutes)"
for _ in $(seq 1 45); do
  # Any HTTP status over a verified TLS connection means the certificate is in place.
  code=$(curl -s -o /dev/null -w '%{http_code}' --max-time 5 --resolve "$DOMAIN:8443:127.0.0.1" "https://$DOMAIN:8443/v1/users/nobody" || true)
  if [[ "$code" != 000 ]]; then
    echo "==> $DOMAIN has a certificate and is serving the relay"
    exit 0
  fi
  sleep 4
done
echo "No certificate yet. Caddy's log:" >&2
journalctl -u caddy -n 30 --no-pager >&2
exit 1
