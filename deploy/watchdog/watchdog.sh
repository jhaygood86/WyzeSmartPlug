#!/usr/bin/env bash
# haygoodsrv watchdog: if Cockpit is down AND SSH to the host fails, power-cycle it
# through the Wyze plug API. Runs from a systemd timer; config in /etc/haygoodsrv-watchdog.env.
set -u

CONF="${WATCHDOG_CONF:-/etc/haygoodsrv-watchdog.env}"
# shellcheck disable=SC1090
[ -r "$CONF" ] && . "$CONF"

TARGET_HOST="${TARGET_HOST:-haygoodsrv}"
COCKPIT_URL="${COCKPIT_URL:-https://${TARGET_HOST}:9090}"
SSH_USER="${SSH_USER:-root}"
SSH_KEY="${SSH_KEY:-/opt/haygoodsrv-watchdog/id_ed25519}"
KNOWN_HOSTS="${KNOWN_HOSTS:-/opt/haygoodsrv-watchdog/known_hosts}"
REMOTE_HEALTH="${REMOTE_HEALTH:-/opt/haygoodsrv-watchdog/remote-health.sh}"
PLUG_API_URL="${PLUG_API_URL:-http://127.0.0.1:5000}"
PLUG_MAC="${PLUG_MAC:?PLUG_MAC is required}"
PLUG_API_KEY="${PLUG_API_KEY:-}"
DRY_RUN="${DRY_RUN:-1}"
OFF_SECONDS="${OFF_SECONDS:-10}"
COOLDOWN_MINUTES="${COOLDOWN_MINUTES:-30}"
STATE_DIR="${STATE_DIR:-/var/lib/haygoodsrv-watchdog}"

mkdir -p "$STATE_DIR"
exec 9>"$STATE_DIR/lock"
flock -n 9 || { echo "another watchdog run is in progress"; exit 0; }

log() { echo "$*"; }

api() { # api METHOD PATH
  local args=(-sS -m 30 -X "$1" "${PLUG_API_URL}$2")
  [ -n "$PLUG_API_KEY" ] && args+=(-H "X-Api-Key: ${PLUG_API_KEY}")
  curl "${args[@]}"
}

# Check A: Cockpit answers over HTTPS (any HTTP response counts; TLS is not validated).
cockpit_code=$(curl -k -sS -m 10 -o /dev/null -w '%{http_code}' "$COCKPIT_URL" 2>/dev/null || true)
if [ -n "$cockpit_code" ] && [ "$cockpit_code" != "000" ]; then cockpit_ok=1; else cockpit_ok=0; fi

# Check B: SSH in and run basic health checks. Failing to connect/run is a hard failure;
# problems the health script reports (exit 1) are logged as warnings only.
SSH_OPTS=(-i "$SSH_KEY" -o BatchMode=yes -o LogLevel=ERROR -o ConnectTimeout=10 -o ServerAliveInterval=5 -o ServerAliveCountMax=2
          -o StrictHostKeyChecking=accept-new -o UserKnownHostsFile="$KNOWN_HOSTS")
ssh_out=$(timeout 60 ssh "${SSH_OPTS[@]}" "${SSH_USER}@${TARGET_HOST}" 'bash -s' < "$REMOTE_HEALTH" 2>&1)
ssh_rc=$?
if [ "$ssh_rc" -eq 0 ] || [ "$ssh_rc" -eq 1 ]; then ssh_ok=1; else ssh_ok=0; fi
[ "$ssh_rc" -eq 1 ] && log "WARN health checks reported problems on ${TARGET_HOST}: ${ssh_out}"
[ "$ssh_rc" -eq 0 ] && log "OK ssh health: ${ssh_out}"

log "cockpit=${cockpit_ok} (http ${cockpit_code:-none}) ssh=${ssh_ok} (rc ${ssh_rc})"

if [ "$cockpit_ok" -eq 1 ] || [ "$ssh_ok" -eq 1 ]; then
  [ "$cockpit_ok" -eq 0 ] && log "WARN Cockpit down but SSH works; not power-cycling"
  [ "$ssh_ok" -eq 0 ] && log "WARN SSH down but Cockpit works; not power-cycling: ${ssh_out}"
  exit 0
fi

log "ALERT ${TARGET_HOST} is unreachable via both Cockpit and SSH: ${ssh_out}"

stamp="$STATE_DIR/last-cycle"
if [ -f "$stamp" ]; then
  age_min=$(( ( $(date +%s) - $(stat -c %Y "$stamp") ) / 60 ))
  if [ "$age_min" -lt "$COOLDOWN_MINUTES" ]; then
    log "In cooldown (power-cycled ${age_min}m ago, cooldown ${COOLDOWN_MINUTES}m); not power-cycling again"
    exit 0
  fi
fi

if [ "$DRY_RUN" = "1" ]; then
  log "DRY_RUN: would power-cycle plug ${PLUG_MAC}"
  exit 0
fi

log "Power-cycling plug ${PLUG_MAC}"
touch "$stamp"
off=$(api POST "/plugs/${PLUG_MAC}/off") || { log "ERROR plug off failed: ${off:-no response}"; exit 2; }
log "off: ${off}"
sleep "$OFF_SECONDS"
on=""
for attempt in 1 2 3; do # turning it back on is the critical part, so retry
  if on=$(api POST "/plugs/${PLUG_MAC}/on") && echo "$on" | grep -q '"isOn":true'; then break; fi
  log "WARN plug on attempt ${attempt} failed: ${on:-no response}"; on=""; sleep 5
done
[ -n "$on" ] || { log "ERROR could not turn plug back on!"; exit 2; }
log "on: ${on}"
log "Power-cycle complete"
