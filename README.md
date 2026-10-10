# Release Calendar

Your Steam wishlist and library as a calendar — plus an iCalendar feed you can subscribe to,
so releases land in whatever calendar you already use.

One ASP.NET Core process: Blazor Server for the UI, EF Core + SQLite for the cache, a
background worker that talks to Steam. Nothing in the browser touches Steam directly —
none of Valve's endpoints send CORS headers, and the API key stays server-side.

```
dotnet run --project src/ReleaseCalendar.Web
```

Then open <http://localhost:5000>. The first sync runs a few seconds after startup and takes
about two seconds.

## Configuration

`src/ReleaseCalendar.Web/appsettings.json`, section `Calendar`:

| Key | Meaning |
|---|---|
| `SteamId` | Your steamID64 — **not committed**; set it per-environment (see below). Find it with `https://steamcommunity.com/id/<vanity>/?xml=1` |
| `ApiKey` | Only needed for owned games — **set it with user-secrets, not here** |
| `SyncIntervalHours` | Background sync cadence (default 6) |
| `CountryCode` / `Language` | Store region for names and dates (default `US` / `english`) |
| `RecentlyReleasedDays` | How long a release stays in "out now, still wishlisted" (default 180) |

The wishlist half needs **no API key** — just a public wishlist.

Neither your steamID nor the key is committed: `appsettings.json` ships `"SteamId": 0`,
which the app reports as "no steamID configured" rather than failing obscurely. Set both
locally with user-secrets:

```
dotnet user-secrets --project src/ReleaseCalendar.Web set "Calendar:SteamId" "<your steamID64>"
```

In Docker they come from the stack `.env` as `STEAM_ID` and `STEAM_API_KEY`; under systemd,
from `/etc/releasecalendar.env`.

For owned games:

```
dotnet user-secrets --project src/ReleaseCalendar.Web set "Calendar:ApiKey" "<key>"
```

Get a key at <https://steamcommunity.com/dev/apikey> (any domain value works; the account
needs $5+ of lifetime spend), and set your profile's **Game details** privacy to Public —
that's a separate setting from the profile and from the wishlist.

## The calendar feed

```
http://localhost:5000/feed/<steamID64>.ics
```

Subscribe to it in Google Calendar, Outlook or iOS. Day-accurate releases are `CONFIRMED`
all-day events; quarter- and year-only games are `TENTATIVE` and land at the start of their
period — or on today, if that period is already underway. Undated games aren't in the feed —
they join it the moment Steam dates them.

Two filters keep a subscription useful rather than a data dump: nothing older than
`RecentlyReleasedDays`, and a past release only appears if you still haven't bought it. An
owned game that shipped three months ago is nothing to act on; an unbought wishlist item that
shipped last week is.

## Why date precision is modelled explicitly

Steam's store API returns a release timestamp *and* a `coming_soon_display` field saying how
precise that timestamp actually is. The timestamp for a fuzzy date is a placeholder — the last
day of the quarter, or 31 December of the year. Trust it blindly and the calendar claims a
dozen games launch on New Year's Eve.

| `coming_soon_display` | Stored as | Rendered as |
|---|---|---|
| `date_full` | `Exact` | the day |
| `date_quarter` | `Quarter` | `Q2 2027`, placed at the quarter's start |
| `date_year` | `Year` | `2027`, placed at the year's start |
| `text_comingsoon` | `ComingSoon` | the store's own message |
| `text_tba` | `Tba` | "To be announced" |
| *(absent, not coming soon)* | `Exact` | already out |
| *(no `release` block at all)* | `Unknown` | "no date" — see below |

Unrecognised values degrade to `ComingSoon` rather than throwing, so a future Steam change
can't take the app down.

`Unknown` is the one that matters for a library. Delisted and region-locked apps — GTA III,
F1 2019, *Battle vs Chess* — come back from `GetItems` with no `release` block, and often no
name either. Mapping that to "coming soon" files them next to genuinely unannounced games:
in this library, 22 of 612 owned games were masquerading as upcoming titles. They are
counted and listed on their own, never in a forward-looking tier.

`release_from_early_access_date` is a second, separate event: a game leaving Early Access.
No "release date" field surfaces it.

## Layout

```
src/ReleaseCalendar.Web/
  Steam/        SteamClient + DTOs + ReleaseMapper (the precision logic)
  Data/         EF entities and DbContext
  Services/     SyncService, SyncWorker, CalendarQueries, IcsFeed
  Components/   Blazor pages: Up Next, Month, Unreleased, Library, Delays, Settings
tests/
  fixtures/     real captured Steam responses
  ReleaseCalendar.Tests/
```

## Sync behaviour

Wishlist membership is one request per sync. Store metadata is only refetched when stale:

- 6 hours for anything releasing (or leaving Early Access) within 30 days
- 24 hours for other upcoming games, including undated ones
- 30 days for games already out

A steady-state sync is **one** HTTP request. A cold start is three. `GetItems` takes 100
appids per call, so even a full refresh of 157 games is two requests.

Every date change is written to `DateChange` — that history can't be backfilled, so it starts
accumulating from the first sync. The Delays page reads it.

## Tests

```
dotnet test
```

87 tests. The Steam parsing tests run against real captured responses in `tests/fixtures/`
covering every precision variant, an Early Access exit, and released games. Sync and query
tests run against real SQLite in-memory with a stubbed Steam transport and a controllable clock.

## Verified Steam API facts

Checked live on 2026-10-10:

| Endpoint | Key | Notes |
|---|---|---|
| `IWishlistService/GetWishlist/v1` | none | `{appid, priority, date_added}`. Private/empty → `{"response":{}}` with HTTP 200 |
| `IStoreBrowseService/GetItems/v1` | none | 100 appids per call, release data as unix timestamps |
| `IPlayerService/GetOwnedGames/v1` | **yes** | needs *Game details: Public* |
| `store/api/appdetails` | none | 1 app/call, ~200 per 5 min, ignores `filters=`. Fallback only |
| `steamcommunity.com/.../games?xml=1` | — | dead: HTTP 302 to `/login` |

No endpoint sends `Access-Control-Allow-Origin`; `OPTIONS` returns 405.
