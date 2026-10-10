#!/usr/bin/env bash
#
# Publish a release build and install it as a systemd service on a remote host.
# Idempotent: safe to re-run for upgrades. The SQLite database and the API key
# are never touched by a redeploy.
#
# Usage:
#   HOST=192.168.1.50 DEPLOY_USER=youruser deploy/deploy.sh
#
# Options (environment):
#   HOST      target host                       (required)
#   DEPLOY_USER  ssh user on the target            (required)
#   SSH_KEY   private key for the deploy        (default: ~/.ssh/releasecal_deploy)
#   RID       dotnet runtime identifier         (default: linux-x64)
#   PORT      port to listen on                 (default: 5080)
#   API_KEY   Steam Web API key; written to /etc/releasecalendar.env only when set
#   SUDO_PW   sudo password, piped to `sudo -S` (required for the first install)
#
set -euo pipefail

HOST="${HOST:?set HOST}"
# Deliberately not named USER: the shell already defines that, so a
# default would silently resolve to the *local* username.
DEPLOY_USER="${DEPLOY_USER:?set DEPLOY_USER (the ssh user on the target)}"
SSH_KEY="${SSH_KEY:-$HOME/.ssh/releasecal_deploy}"
RID="${RID:-linux-x64}"
PORT="${PORT:-5080}"
API_KEY="${API_KEY:-}"
SUDO_PW="${SUDO_PW:-}"

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$REPO_ROOT/src/ReleaseCalendar.Web"
STAGE="$REPO_ROOT/artifacts/publish-$RID"
TARBALL="$REPO_ROOT/artifacts/releasecalendar-$RID.tar.gz"

SSH=(ssh -i "$SSH_KEY" -o StrictHostKeyChecking=accept-new -o ConnectTimeout=10 "$DEPLOY_USER@$HOST")

say() { printf '\n\033[1m==> %s\033[0m\n' "$*"; }

# Run a command as root on the target. Uses passwordless sudo when available,
# otherwise feeds SUDO_PW to `sudo -S`.
remote_root() {
  local script="$1"
  if "${SSH[@]}" 'sudo -n true' >/dev/null 2>&1; then
    "${SSH[@]}" "sudo bash -s" <<< "$script"
  else
    [ -n "$SUDO_PW" ] || { echo "need SUDO_PW (no passwordless sudo on $HOST)" >&2; exit 1; }
    "${SSH[@]}" "sudo -S -p '' bash -s" <<< "$SUDO_PW
$script"
  fi
}

# ---------------------------------------------------------------------------

say "Publishing $RID release build"
rm -rf "$STAGE"
dotnet publish "$PROJECT" \
  -c Release \
  -r "$RID" \
  --self-contained true \
  -p:PublishTrimmed=false \
  -p:DebugType=none \
  -o "$STAGE" \
  --nologo

# A self-contained build needs no .NET on the server, which is one less thing to
# keep in step across upgrades.
[ -f "$STAGE/ReleaseCalendar.Web" ] || { echo "publish produced no executable" >&2; exit 1; }

# Never ship a local dev database or secrets.
rm -f "$STAGE/calendar.db" "$STAGE/calendar.db-shm" "$STAGE/calendar.db-wal"
rm -f "$STAGE/appsettings.Development.json"

say "Packing"
mkdir -p "$REPO_ROOT/artifacts"
tar -czf "$TARBALL" -C "$STAGE" .
echo "$(du -h "$TARBALL" | cut -f1) -> $TARBALL"

say "Uploading"
"${SSH[@]}" 'rm -rf ~/releasecalendar-stage && mkdir -p ~/releasecalendar-stage'
scp -i "$SSH_KEY" -o StrictHostKeyChecking=accept-new -q \
    "$TARBALL" "$DEPLOY_USER@$HOST:~/releasecalendar-stage/app.tar.gz"

say "Installing"
UNIT="$(sed -e "s/__USER__/$DEPLOY_USER/g" -e "s/__PORT__/$PORT/g" "$REPO_ROOT/deploy/releasecalendar.service")"

remote_root "set -euo pipefail

systemctl stop releasecalendar 2>/dev/null || true

# Binaries are replaced wholesale; data and secrets are left alone.
rm -rf /opt/releasecalendar
install -d -o $DEPLOY_USER -g $DEPLOY_USER -m 755 /opt/releasecalendar
tar -xzf /home/$DEPLOY_USER/releasecalendar-stage/app.tar.gz -C /opt/releasecalendar
chown -R $DEPLOY_USER:$DEPLOY_USER /opt/releasecalendar
chmod +x /opt/releasecalendar/ReleaseCalendar.Web

install -d -o $DEPLOY_USER -g $DEPLOY_USER -m 750 /var/lib/releasecalendar

# Point the app at the data directory and the chosen port.
cat > /opt/releasecalendar/appsettings.Production.json <<'JSON'
{
  \"Logging\": { \"LogLevel\": { \"Default\": \"Information\", \"Microsoft.AspNetCore\": \"Warning\" } },
  \"Calendar\": { \"DatabasePath\": \"/var/lib/releasecalendar/calendar.db\" }
}
JSON
chown $DEPLOY_USER:$DEPLOY_USER /opt/releasecalendar/appsettings.Production.json

# Create the env file on first install; preserve an existing key on upgrades.
if [ ! -f /etc/releasecalendar.env ]; then
  touch /etc/releasecalendar.env
fi
chown root:$DEPLOY_USER /etc/releasecalendar.env
chmod 640 /etc/releasecalendar.env

cat > /etc/systemd/system/releasecalendar.service <<'UNITEOF'
$UNIT
UNITEOF

systemctl daemon-reload
systemctl enable releasecalendar >/dev/null
rm -rf /home/$DEPLOY_USER/releasecalendar-stage
"

if [ -n "$API_KEY" ]; then
  say "Writing API key"
  remote_root "set -euo pipefail
umask 027
cat > /etc/releasecalendar.env <<'ENVEOF'
Calendar__ApiKey=$API_KEY
ENVEOF
chown root:$DEPLOY_USER /etc/releasecalendar.env
chmod 640 /etc/releasecalendar.env
"
fi

say "Starting"
remote_root "systemctl restart releasecalendar"
sleep 6

remote_root "systemctl is-active releasecalendar && systemctl --no-pager -l status releasecalendar | head -12"

say "Done — http://$HOST:$PORT"
