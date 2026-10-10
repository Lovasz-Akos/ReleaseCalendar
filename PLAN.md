# Release Calendar — implementation plan

Steam wishlist + library release calendar. Blazor Server (interactive server) + EF Core + SQLite,
one ASP.NET Core process. Design brief and mockups:
https://claude.ai/code/artifact/a7895b7a-bac5-43ad-a44f-90b99cd39e2a

**Account under test:** set `Calendar:SteamId` via user-secrets or the stack `.env`.
Figures throughout refer to a 157-item public wishlist and a 612-game library.

---

## A — Foundations

- [x] **A1** Solution + `src/ReleaseCalendar.Web` (net10.0 Blazor Server) + `tests/ReleaseCalendar.Tests` (xUnit)
- [x] **A2** Steam DTOs + `SteamClient`: `GetWishlist`, `GetItems` (chunked 100), `GetOwnedGames`
- [x] **A3** `ReleaseMapper`: `coming_soon_display` → `DatePrecision`, EA-exit dates, placeholder-timestamp guard
- [x] **A4** EF model (`App`, `Entry`, `DateChange`, `SyncRun`) + SQLite + migration

## B — Sync

- [x] **B1** `SyncService`: wishlist → entries, appids → `GetItems` batches → apps, record date changes
- [x] **B2** Tiered staleness (imminent 6h / upcoming 24h / released 30d) so a sync is a few requests
- [x] **B3** `SyncWorker` BackgroundService on an interval + manual "Sync now" from the UI

## C — UI

- [x] **C1** Layout, nav, dark app theme ported from the mockup
- [x] **C2** Up Next — precision tiers + EA exits + "out now, still wishlisted"
- [x] **C3** Month grid + undated strip for the visible period
- [x] **C4** Library view, Delays view, Settings (steamID, API key, sync now)
- [x] **C5** Unreleased — owned-but-not-out, pending 1.0s, and delisted apps kept separate

## D — Feed

- [x] **D1** `GET /feed/{steamId}.ics` — all-day VEVENTs, fuzzy dates as period-start events

## E — Verification

- [x] **E1** Unit tests green against captured fixtures (`tests/fixtures/`)
- [x] **E2** Real end-to-end sync against the live API, numbers reconciled against known counts
- [x] **E3** Owned-games half verified live: 612 owned, 8 batched requests
- [ ] **E4** Manual test by the user (click through, confirm against their own Steam account)

---

## Known API facts (verified 2026-10-10)

| Fact | Detail |
|---|---|
| `GetWishlist` | keyless, `?steamid=`, returns `{appid, priority, date_added}`; `{"response":{}}` when private/empty |
| `GetItems` | keyless, 100 appids/call via `input_json`, returns `release{}` with unix timestamps |
| `GetOwnedGames` | **needs** Web API key + profile *Game details: Public* |
| No CORS | no `Access-Control-Allow-Origin` on any endpoint; `OPTIONS` → 405. Server-side only. |
| Precision enum | `date_full` · `date_quarter` · `date_year` · `text_comingsoon` · `text_tba` |
| Placeholder dates | `date_quarter` → last day of quarter, `date_year` → Dec 31. Never render as exact. |
| EA exits | `release_from_early_access_date` — a separate event; only future ones matter |
| `appdetails` | 1 app/call, ~200 per 5 min, ignores `filters=`. Fallback only (DLC lists). |
| Delisted apps | `GetItems` returns them with **no name and no `release` block** — not omitted. `Precision == Unknown` is the signal, not a missing item. 22 of 612 owned games. |
| Query logging | ASP.NET redacts HttpClient query strings (`?*`), so the API key never reaches the logs. |

## Fixtures

- `tests/fixtures/wishlist.json` — real 157-item wishlist response
- `tests/fixtures/getitems.json` — 9 apps covering every precision variant, an EA exit, and released games
