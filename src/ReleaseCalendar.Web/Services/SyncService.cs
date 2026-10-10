using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReleaseCalendar.Web.Data;
using ReleaseCalendar.Web.Steam;

namespace ReleaseCalendar.Web.Services;

/// <summary>
/// Pulls the wishlist and (when a key is configured) the owned library, then refreshes
/// store metadata for anything new or stale. Runs are serialised — a manual "Sync now"
/// while the background worker is mid-run waits rather than doubling up.
/// </summary>
public sealed class SyncService(
    IDbContextFactory<CalendarDb> dbFactory,
    IServiceScopeFactory scopeFactory,
    IOptions<CalendarOptions> options,
    SteamAccount account,
    TimeProvider clock,
    ILogger<SyncService> log)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Raised after every run so open pages can refresh themselves.</summary>
    public event Func<Task>? SyncCompleted;

    public bool IsSyncing { get; private set; }

    public async Task<SyncRun> SyncAsync(bool force = false, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        IsSyncing = true;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var steam = scope.ServiceProvider.GetRequiredService<SteamClient>();

            // A vanity name needs one network call to become an id. Retried here as well
            // as at startup, so a Steam outage at boot doesn't leave the app inert.
            await scope.ServiceProvider.GetRequiredService<SteamIdResolver>().EnsureResolvedAsync(ct);

            return await RunAsync(steam, force, ct);
        }
        finally
        {
            IsSyncing = false;
            _gate.Release();
            if (SyncCompleted is { } handler)
            {
                try { await handler.Invoke(); }
                catch (Exception ex) { log.LogDebug(ex, "A SyncCompleted subscriber threw."); }
            }
        }
    }

    private async Task<SyncRun> RunAsync(SteamClient steam, bool force, CancellationToken ct)
    {
        var opts = options.Value;
        var now = clock.GetUtcNow().UtcDateTime;
        var today = DateOnly.FromDateTime(now);

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var run = new SyncRun { StartedUtc = now };
        db.SyncRuns.Add(run);
        await db.SaveChangesAsync(ct);

        var requestsBefore = steam.RequestCount;

        try
        {
            if (!account.IsConfigured)
                throw new InvalidOperationException(
                    "No Steam account configured. Set Calendar:Account to a steamID64, a vanity "
                  + "name, or a profile URL — see the Settings page.");

            // App rows must exist before entries can reference them.
            var ensured = (await db.Apps.Select(a => a.AppId).ToListAsync(ct)).ToHashSet();

            // ---- wishlist: keyless, one request ----------------------------------
            var wishlist = await steam.GetWishlistAsync(account.SteamId, ct);
            run.WishlistItems = wishlist.Count;

            if (wishlist.Count > 0)
            {
                await EnsureAppRowsAsync(db, wishlist.Select(w => w.AppId), ensured, ct);
                Reconcile(db, account.SteamId, EntrySource.Wishlist, await ExistingAsync(db, account.SteamId, EntrySource.Wishlist, ct),
                    wishlist.Select(w => new EntryInput(
                        w.AppId,
                        w.Priority,
                        DateTimeOffset.FromUnixTimeSeconds(w.DateAdded).UtcDateTime,
                        null)));
            }

            // ---- owned library: needs a Web API key ------------------------------
            var ownedNames = new Dictionary<int, string>();

            if (opts.HasApiKey)
            {
                var owned = await steam.GetOwnedGamesAsync(account.SteamId, opts.ApiKey!, ct);
                if (owned is not null)
                {
                    foreach (var g in owned)
                        if (g.Name is { Length: > 0 } n)
                            ownedNames[g.AppId] = n;

                    run.OwnedItems = owned.Count;
                    await EnsureAppRowsAsync(db, owned.Select(g => g.AppId), ensured, ct);
                    Reconcile(db, account.SteamId, EntrySource.Owned, await ExistingAsync(db, account.SteamId, EntrySource.Owned, ct),
                        owned.Select(g => new EntryInput(g.AppId, null, null, g.PlaytimeForever)));
                }
            }

            await db.SaveChangesAsync(ct);

            // ---- refresh store metadata for new + stale apps ----------------------
            var tracked = await db.Entries
                .Where(e => e.SteamId == account.SteamId)
                .Select(e => e.AppId)
                .Distinct()
                .ToListAsync(ct);

            var apps = await db.Apps.Where(a => tracked.Contains(a.AppId)).ToDictionaryAsync(a => a.AppId, ct);

            var toFetch = tracked
                .Where(id => force
                          || !apps.TryGetValue(id, out var a)
                          || a.FetchedUtc == default
                          || now - a.FetchedUtc >= RefreshInterval(a, today))
                .ToList();

            if (toFetch.Count > 0)
            {
                log.LogInformation("Refreshing {Count} apps in {Batches} GetItems request(s).",
                    toFetch.Count, (toFetch.Count + SteamClient.MaxIdsPerRequest - 1) / SteamClient.MaxIdsPerRequest);

                var items = await steam.GetItemsAsync(toFetch, opts.Language, opts.CountryCode, ct);
                var requested = toFetch.ToHashSet();
                var returned = new HashSet<int>();

                foreach (var item in items)
                {
                    var id = item.ResolvedAppId;

                    // Ignore anything we didn't ask for — a stray item must never be
                    // inserted blindly, or it collides with a row that already exists.
                    if (id == 0 || !requested.Contains(id)) continue;
                    returned.Add(id);

                    if (!apps.TryGetValue(id, out var app))
                    {
                        // Checks the change tracker first, then the table.
                        app = await db.Apps.FindAsync([id], ct);

                        if (app is null)
                        {
                            app = new SteamApp { AppId = id };
                            db.Apps.Add(app);
                        }

                        apps[id] = app;
                    }

                    var mapped = ReleaseMapper.Map(item.Release);

                    // Only a *change* counts — the first sighting of an app isn't a delay.
                    if (app.FetchedUtc != default
                        && (app.ReleaseDate != mapped.Date || app.Precision != mapped.Precision))
                    {
                        db.DateChanges.Add(new DateChange
                        {
                            AppId = id,
                            SeenUtc = now,
                            OldDate = app.ReleaseDate,
                            OldPrecision = app.Precision,
                            NewDate = mapped.Date,
                            NewPrecision = mapped.Precision,
                        });
                        run.DateChanges++;
                    }

                    if (item.Name is { Length: > 0 } name) app.Name = name;
                    app.Resolved = true;
                    app.StoreUrlPath = item.StoreUrlPath ?? app.StoreUrlPath;
                    app.IsFree = item.IsFree;
                    app.CapsuleUrl = ReleaseMapper.CapsuleUrl(item.Assets) ?? app.CapsuleUrl;
                    app.ReleaseDate = mapped.Date;
                    app.Precision = mapped.Precision;
                    app.IsComingSoon = mapped.IsComingSoon;
                    app.IsEarlyAccess = item.Release?.IsEarlyAccess ?? false;
                    app.CustomMessage = mapped.CustomMessage;
                    app.EarlyAccessExitDate = mapped.EarlyAccessExitDate;
                    app.FetchedUtc = now;
                }

                run.AppsRefreshed = returned.Count;

                // Items Steam declined to return (some DLC aren't store-browse items).
                // Stamp them anyway so they back off instead of retrying every sync.
                var missing = toFetch.Where(id => !returned.Contains(id)).ToList();
                if (missing.Count > 0)
                {
                    log.LogWarning("GetItems returned no data for {Count} appid(s) — delisted or "
                                 + "region-locked: {Ids}", missing.Count, string.Join(", ", missing.Take(15)));

                    foreach (var id in missing)
                    {
                        if (!apps.TryGetValue(id, out var app)) continue;

                        // Back off so these don't retry every sync forever. Data from a
                        // previous successful fetch is left alone — a transient miss must
                        // not wipe a game we already know about.
                        app.FetchedUtc = now;

                        if (!app.Resolved)
                        {
                            app.Precision = DatePrecision.Unknown;
                            app.IsComingSoon = false;
                        }
                    }
                }
            }

            // The store won't name everything it owns up to: delisted apps, betas, and
            // pre-release DLC such as a Call of Duty edition all come back nameless. But
            // GetOwnedGames already told us, so prefer that over showing "App 4783780".
            foreach (var (id, name) in ownedNames)
            {
                if (!apps.TryGetValue(id, out var app)) continue;
                if (app.Name.Length != 0 && app.Name != $"App {id}") continue;

                app.Name = name;
            }

            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            run.Error = $"{ex.GetType().Name}: {ex.Message}";
            log.LogError(ex, "Sync failed.");
        }

        run.FinishedUtc = clock.GetUtcNow().UtcDateTime;
        run.StoreRequests = steam.RequestCount - requestsBefore;
        await db.SaveChangesAsync(ct);

        log.LogInformation(
            "Sync done in {Ms}ms — {Wishlist} wishlisted, {Owned} owned, {Refreshed} apps refreshed, "
          + "{Changes} date change(s), {Requests} request(s).",
            (int)(run.FinishedUtc - run.StartedUtc)!.Value.TotalMilliseconds,
            run.WishlistItems, run.OwnedItems, run.AppsRefreshed, run.DateChanges, run.StoreRequests);

        return run;
    }

    private readonly record struct EntryInput(int AppId, int? Priority, DateTime? AddedUtc, int? PlaytimeMinutes);

    private static async Task<Dictionary<int, Entry>> ExistingAsync(
        CalendarDb db, long steamId, EntrySource source, CancellationToken ct) =>
        await db.Entries.Where(e => e.SteamId == steamId && e.Source == source).ToDictionaryAsync(e => e.AppId, ct);

    /// <summary>
    /// Adds, updates and removes entries so the table mirrors Steam exactly — buying a
    /// wishlisted game removes it upstream, and it should disappear here too.
    /// </summary>
    private static void Reconcile(
        CalendarDb db, long steamId, EntrySource source,
        Dictionary<int, Entry> existing, IEnumerable<EntryInput> incoming)
    {
        var seen = new HashSet<int>();

        foreach (var input in incoming)
        {
            if (!seen.Add(input.AppId)) continue;

            if (existing.TryGetValue(input.AppId, out var entry))
            {
                entry.Priority = input.Priority;
                entry.AddedUtc = input.AddedUtc ?? entry.AddedUtc;
                entry.PlaytimeMinutes = input.PlaytimeMinutes ?? entry.PlaytimeMinutes;
            }
            else
            {
                db.Entries.Add(new Entry
                {
                    SteamId = steamId,
                    AppId = input.AppId,
                    Source = source,
                    Priority = input.Priority,
                    AddedUtc = input.AddedUtc,
                    PlaytimeMinutes = input.PlaytimeMinutes,
                });
            }
        }

        foreach (var stale in existing.Values.Where(e => !seen.Contains(e.AppId)))
            db.Entries.Remove(stale);
    }

    private static async Task EnsureAppRowsAsync(
        CalendarDb db, IEnumerable<int> appIds, HashSet<int> ensured, CancellationToken ct)
    {
        foreach (var id in appIds.Distinct())
        {
            if (!ensured.Add(id)) continue;

            db.Apps.Add(new SteamApp
            {
                AppId = id,
                Name = $"App {id}",

                // Nothing is known yet, which is not the same as "coming soon" — an
                // unresolvable app must never drift into the upcoming tiers.
                Precision = DatePrecision.Unknown,
                IsComingSoon = false,
                Resolved = false,
                FetchedUtc = default, // never fetched — always counts as stale
            });
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// How long an app's metadata stays fresh. Anything landing within a month is checked
    /// often because that's when dates move; a game that is already out is near-static.
    /// </summary>
    internal static TimeSpan RefreshInterval(SteamApp app, DateOnly today)
    {
        var pending = PendingDate(app, today);

        if (pending is { } d && d.DayNumber - today.DayNumber <= 30)
            return TimeSpan.FromHours(6);

        if (!app.IsComingSoon && pending is null)
            return TimeSpan.FromDays(30);

        return TimeSpan.FromHours(24);
    }

    /// <summary>The next future date this app is waiting on, if any.</summary>
    private static DateOnly? PendingDate(SteamApp app, DateOnly today)
    {
        DateOnly? best = null;

        if (app.IsComingSoon && app.ReleaseDate is { } r && r >= today) best = r;
        if (app.EarlyAccessExitDate is { } ea && ea >= today && (best is null || ea < best)) best = ea;

        return best;
    }
}
