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
| `Account` | Who to track: a vanity name, a steamID64, or a pasted profile URL. **Not committed** — set it per-environment (see below). |
| `SteamId` | An explicit steamID64 that skips the vanity lookup. Optional; `Account` is usually easier. |
| `ApiKey` | Only needed for owned games — **set it with user-secrets, not here** |
| `SyncIntervalHours` | Background sync cadence (default 6) |
| `CountryCode` / `Language` | Store region for names, dates **and prices** (default `US` / `english`). Set it to your own region or the Sales page quotes the wrong currency. |
| `RecentlyReleasedDays` | How long a release stays in "out now, still wishlisted" (default 180) |

The wishlist half needs **no API key** — just a public wishlist.

Neither your account nor the key is committed: `appsettings.json` ships them empty, and the
app reports "no account configured" rather than failing obscurely. Set them locally with
user-secrets:

```
dotnet user-secrets --project src/ReleaseCalendar.Web set "Calendar:Account" "your-vanity-name"
```

A vanity name is resolved to a steamID64 once at startup, keylessly, via the profile's own
XML endpoint — the same trick that finds your id in the first place. Resolution failure is
logged and retried by the background sync rather than stopping the app, and the Settings page
shows what the name resolved to. Note a vanity name is mutable: set `Calendar:SteamId` to the
number instead if you want it pinned.

In Docker these come from the stack `.env` as `STEAM_ACCOUNT` and `STEAM_API_KEY`; under
systemd, from `/etc/releasecalendar.env`.

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

`Unknown` is the one that matters for a library. Delisted and region-locked apps come back
from `GetItems` with no `release` block, and often no name either. Mapping that to "coming
soon" files them next to genuinely unannounced games — and a library of several hundred
games will have dozens of them, so the Unreleased page would be mostly noise. They are
counted and listed separately, never in a forward-looking tier.

`release_from_early_access_date` is a second, separate event: a game leaving Early Access.
No "release date" field surfaces it.

## Sales and price history

`GetItems` returns prices in the same response as release dates, so the Sales page costs
**no extra requests** — it is the same two calls per sync. It lists wishlist items
discounted right now, with the ones that weren't discounted at the previous sync flagged
separately: that is what "new sale" means here, and it is a comparison against stored
state rather than a guess.

A discount already running at the very first sync is **not** reported as new. Otherwise a
cold start flags every sale on the wishlist at once, which is noise rather than news — the
same reason the first sighting of a release date isn't recorded as a delay.

**Steam publishes no historical prices.** Nothing can be backfilled; SteamDB's graphs exist
because it has polled every app for years. This app records a price point whenever a price
actually changes, so the history is sparse, exact, and grows forward from your first sync.
That means:

- the per-row graph appears only once a game's price has changed at least once
- "lowest seen" means lowest since tracking began, which the page says explicitly
- the series is a **step** chart: a price holds until it changes, so interpolating between
  points would draw prices that were never charged

Prices are shown using Steam's own formatted strings rather than rebuilt from cents —
`19,99€` and `4 299 Ft` don't survive a naive currency format.

## Review scores

`include_reviews` rides along in the same `GetItems` call as prices — again no extra
requests. Shown only where a buy decision is live: the "out now, still on your wishlist"
tier and the Sales page.

It needs no per-tier switch because it is self-limiting: an unreleased game has no reviews,
so nothing renders. That matters because Steam returns a **structurally valid** summary for
unreleased games reading `0% of 0 reviews`, labelled "No user reviews". The review *count*
decides whether there's a score — never the percentage — or every upcoming game would
display a damning 0%.

Two cues, both with the number present so neither relies on colour:

- **weak** (<70% positive) is tinted, because "don't bother" is the actionable signal; a
  good score needs no decoration
- **thin** (<50 reviews) gets a dashed edge, so "100% of 20" doesn't read as more solid
  than "97% of 215,621"

The tooltip always carries the percentage, the sample size and Steam's own wording.

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

## License

GPL-3.0-or-later. See [LICENSE](LICENSE).

Copyright (C) 2026 Lovász Ákos

This program is free software: you can redistribute it and/or modify it under the terms
of the GNU General Public License as published by the Free Software Foundation, either
version 3 of the License, or (at your option) any later version. It is distributed in the
hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty
of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.

Copyleft, not just attribution: anyone distributing a modified version must also release
their source under the GPL.
