# Docker / Dockge deployment

The app is configured entirely through environment variables, so there is nothing to
edit inside the image. Three routes, in order of least ongoing effort.

Routes B and C need a target host; set these once per shell so their commands paste
as-is:

```bash
SERVER=192.168.1.50      # your server's address
APPUSER=youruser         # the account the service runs as
```

## Route A — pull the published image (recommended)

`.github/workflows/build.yml` runs the test suite on every push to `main` and, only if it
passes, builds and pushes `ghcr.io/lovasz-akos/releasecalendar:latest`. The server never
builds anything, so this is genuinely just compose text in Dockge.

### One-time: let the server pull from GHCR

While the repo is private the package is private too. Two options:

**Authenticate the server** (keeps the image private):

```bash
# github.com/settings/tokens -> classic token, scope: read:packages
echo <PAT> | docker login ghcr.io -u <github-user> --password-stdin
```

**Or make just the package public** (repo stays private): on the package page →
Package settings → Change visibility → Public. The image contains no secrets — all
configuration arrives as environment variables — so this is a reasonable trade for
never dealing with registry auth again. It does publish the compiled app.

### Then

Paste the contents of `compose.yaml` into a new Dockge stack, fill in the `.env`:

```
STEAM_ACCOUNT=your-vanity-name
STEAM_API_KEY=xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx
TZ=Europe/Budapest
```

and Deploy.

### Updating

```bash
docker compose pull && docker compose up -d
```

Or the **Update** button in Dockge. Image tags available:

| Tag | Moves | Use for |
|---|---|---|
| `latest` | every green push to `main` | normal use |
| `sha-abc1234` | never | pinning, or rolling back |
| `1.2.3`, `1.2` | git tags `v*` | if you start tagging releases |

To roll back, point the stack at a `sha-` tag and redeploy. The named volume is
untouched by an image change, so the database and delay history survive.

## Route B — build on the server

Only needed if you'd rather not use a registry. Clone the repo into the stack directory
and uncomment the `build:` block in `compose.yaml`:

```bash
sudo mkdir -p /opt/stacks/releasecalendar
sudo chown "$USER" /opt/stacks/releasecalendar
git clone https://github.com/Lovasz-Akos/ReleaseCalendar /opt/stacks/releasecalendar
```

Dockge picks the stack up; upgrades are `git pull` then **Rebuild**. First build pulls the
SDK image (~1 GB) and takes a couple of minutes; later builds reuse the layer cache.

## Route C — no build, no registry

Publish framework-dependent output locally and bind-mount it into the stock runtime
image. Pure compose, no Dockerfile, no SDK on the server — but you re-copy the output
on every upgrade.

```powershell
dotnet publish src/ReleaseCalendar.Web -c Release -o artifacts/app --nologo
tar -czf artifacts/app.tar.gz -C artifacts/app .
scp artifacts/app.tar.gz $APPUSER@$SERVER:~/
```

```bash
sudo install -d -o 1654 -g 1654 /opt/releasecalendar-app
sudo tar -xzf ~/app.tar.gz -C /opt/releasecalendar-app
```

Compose text for Dockge:

```yaml
services:
  releasecalendar:
    image: mcr.microsoft.com/dotnet/aspnet:10.0
    container_name: releasecalendar
    restart: unless-stopped
    working_dir: /app
    command: ["dotnet", "ReleaseCalendar.Web.dll"]
    user: "1654:1654"
    ports:
      - "5080:8080"
    environment:
      ASPNETCORE_URLS: "http://+:8080"
      Calendar__Account: "${STEAM_ACCOUNT:?set STEAM_ACCOUNT in the stack .env}"
      Calendar__ApiKey: "${STEAM_API_KEY:?set STEAM_API_KEY in the stack .env}"
      Calendar__DatabasePath: "/data/calendar.db"
      TZ: "${TZ:-UTC}"
    volumes:
      - /opt/releasecalendar-app:/app:ro
      - releasecalendar-data:/data

volumes:
  releasecalendar-data:
```

## Carrying over an existing database

Only needed if you already ran the systemd install and want the delay history. The
cache itself rebuilds from Steam in three requests, so skip this otherwise.

```bash
# after the stack has started once, so the volume exists
docker compose stop releasecalendar
docker run --rm \
  -v releasecalendar_releasecalendar-data:/data \
  -v /var/lib/releasecalendar:/old:ro \
  alpine sh -c 'cp /old/calendar.db* /data/ && chown -R 1654:1654 /data'
docker compose start releasecalendar
```

The volume is named `<stack>_releasecalendar-data`; confirm with `docker volume ls`.

## Reverting the systemd install

```bash
sudo systemctl disable --now releasecalendar
sudo rm /etc/systemd/system/releasecalendar.service
sudo systemctl daemon-reload
sudo rm -rf /opt/releasecalendar
# /var/lib/releasecalendar and /etc/releasecalendar.env left in place on purpose
```

## Verifying

```bash
docker logs -f releasecalendar
```

Within ~10 seconds:

```
Sync done in ####ms — 142 wishlisted, 418 owned, 560 apps refreshed, 0 date change(s), 8 request(s)
```

Then <http://$SERVER:5080>.

## Gotchas

| Symptom | Cause |
|---|---|
| `Failed to determine the https port for redirect` | fixed in d6+: the app now only redirects when it actually serves HTTPS. A container is "Production" while serving plain HTTP, which used to trip the redirect middleware on every request. TLS belongs to a reverse proxy, not this app. |
| `The key {...} was not found in the key ring` | fixed in d6+: Data Protection keys now live in `/data/dataprotection-keys` on the volume. Previously they were written inside the container and lost on every image update, so browsers holding an antiforgery cookie failed to validate. Harmless but noisy; a reload issued a fresh token. |
| `unable to open database file` / `SQLITE_READONLY` | the `/data` mount isn't writable by uid 1654. Named volumes inherit ownership from the image; a **bind** mount needs `chown 1654:1654` on the host directory |
| `STEAM_API_KEY` variable is not set | the stack `.env` is missing it. Compose fails fast by design rather than starting with the owned-games half silently dead |
| Container runs, nothing on the LAN | the port isn't published, or the host firewall blocks 5080 (`sudo ufw allow 5080/tcp`) |
| Library shows 0 owned | key present but the profile's *Game details* privacy isn't Public |
| Delay history reset after an upgrade | the database wasn't on the named volume — check `Calendar__DatabasePath` is `/data/calendar.db` |

## Notes

- No healthcheck is defined: the `aspnet` images ship without `curl` or `wget`, and a
  healthcheck that can't run is worse than none. Add one with a dedicated probe if you
  want Dockge to show health.
- There is **no authentication** on the app. Anyone on the LAN can open it and fetch
  `/feed/<steamid>.ics`. Fine behind a home router; put it behind a reverse proxy with
  basic auth if it ever leaves the LAN.
- `Calendar__ApiKey` reaches the container as an environment variable, which means
  `docker inspect` shows it. That is normal for compose stacks, but it is why the key
  belongs in the stack `.env` rather than in the compose text you might paste around.
