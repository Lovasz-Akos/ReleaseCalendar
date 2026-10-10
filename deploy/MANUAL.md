# Manual deployment

Three artifacts, five commands. Assumes a systemd Linux host; the build below is
self-contained, so **no .NET runtime is needed on the server**.

Set these once per shell and the commands below paste as-is:

```bash
SERVER=192.168.1.50      # your server's address
APPUSER=youruser         # the account the service runs as
```

## 0. Check the architecture first

```bash
uname -m          # x86_64 → use linux-x64 (the prebuilt tarball)
                  # aarch64 → rebuild with RID=linux-arm64
```

To rebuild for a different architecture:

```powershell
dotnet publish src/ReleaseCalendar.Web -c Release -r linux-arm64 --self-contained true `
  -p:DebugType=none -o artifacts/publish-linux-arm64
tar -czf artifacts/releasecalendar-linux-arm64.tar.gz -C artifacts/publish-linux-arm64 .
```

## 1. Copy the artifacts over

```bash
scp artifacts/releasecalendar-linux-x64.tar.gz $APPUSER@$SERVER:~/
scp artifacts/releasecalendar.service          $APPUSER@$SERVER:~/
```

## 2. Install (on the server, as root)

```bash
sudo install -d -o $APPUSER -g $APPUSER -m 755 /opt/releasecalendar
sudo tar -xzf ~/releasecalendar-linux-x64.tar.gz -C /opt/releasecalendar
sudo chown -R $APPUSER:$APPUSER /opt/releasecalendar
sudo chmod +x /opt/releasecalendar/ReleaseCalendar.Web

# Data directory — deliberately outside /opt, see "Why" below.
sudo install -d -o $APPUSER -g $APPUSER -m 750 /var/lib/releasecalendar
```

## 3. Three bits of config that have to be right

**a. Database path** — outside the directory that gets replaced on upgrade:

```bash
sudo tee /opt/releasecalendar/appsettings.Production.json >/dev/null <<'JSON'
{
  "Logging": { "LogLevel": { "Default": "Information", "Microsoft.AspNetCore": "Warning" } },
  "Calendar": { "DatabasePath": "/var/lib/releasecalendar/calendar.db" }
}
JSON
sudo chown $APPUSER:$APPUSER /opt/releasecalendar/appsettings.Production.json
```

**b. API key** — as an environment file, not in `appsettings.json`:

```bash
sudo tee /etc/releasecalendar.env >/dev/null <<'ENV'
Calendar__ApiKey=<your key>
ENV
sudo chown root:$APPUSER /etc/releasecalendar.env
sudo chmod 640 /etc/releasecalendar.env
```

The double underscore is how .NET maps an env var onto `Calendar:ApiKey`.

**c. Bind address** — already set in the unit file as
`ASPNETCORE_URLS=http://0.0.0.0:5080`. Without `0.0.0.0` it listens on localhost only
and nothing on the LAN can reach it.

## 4. Start it

```bash
sudo cp ~/releasecalendar.service /etc/systemd/system/releasecalendar.service
sudo systemctl daemon-reload
sudo systemctl enable --now releasecalendar
systemctl status releasecalendar
journalctl -u releasecalendar -f        # watch the first sync
```

Within ~10 seconds the log should read:

```
Sync done in ####ms — 157 wishlisted, 612 owned, 769 apps refreshed, 0 date change(s), 10 request(s)
```

Then: <http://$SERVER:5080>

## Upgrading later

```bash
sudo systemctl stop releasecalendar
sudo rm -rf /opt/releasecalendar && sudo install -d -o $APPUSER -g $APPUSER /opt/releasecalendar
sudo tar -xzf ~/releasecalendar-linux-x64.tar.gz -C /opt/releasecalendar
# re-apply appsettings.Production.json (step 3a), then:
sudo systemctl start releasecalendar
```

`/var/lib/releasecalendar` and `/etc/releasecalendar.env` are untouched, so the delay
history and the key survive. `deploy/deploy.sh` automates all of this over SSH if you
ever want one-command redeploys.

## Why the data directory is separate

The SQLite cache can be rebuilt from Steam in three requests, but `DateChange` — the
delay history behind the Delays page — cannot. It only accumulates going forward. If the
database sat in `/opt/releasecalendar`, every upgrade would silently reset it.

## Gotchas

| Symptom | Cause |
|---|---|
| `status=203/EXEC` | `ReleaseCalendar.Web` isn't executable → `chmod +x` |
| Starts, then `Permission denied` on the DB | `/var/lib/releasecalendar` not owned by `$APPUSER`, or missing from `ReadWritePaths` |
| Reachable on the server but not from the LAN | listening on localhost — check `ASPNETCORE_URLS`, then the firewall (`sudo ufw allow 5080/tcp`) |
| Library page shows 0 owned | API key not loaded — `sudo systemctl show releasecalendar -p Environment` won't show it (it's an `EnvironmentFile`); check the file is mode 640 and group `$APPUSER` |
| Wishlist syncs, owned doesn't | profile's *Game details* privacy isn't Public |
