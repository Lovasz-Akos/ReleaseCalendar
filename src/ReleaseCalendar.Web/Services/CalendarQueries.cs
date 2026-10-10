using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReleaseCalendar.Web.Data;
using ReleaseCalendar.Web.Steam;

namespace ReleaseCalendar.Web.Services;

/// <summary>One thing that happens on a date: a release, or a game leaving Early Access.</summary>
public sealed record CalendarEvent(
    int AppId,
    string Name,
    EventKind Kind,
    DateOnly? Date,
    DatePrecision Precision,
    string? CustomMessage,
    string? CapsuleUrl,
    bool Owned,
    bool Wishlisted,
    bool IsComingSoon = false,
    bool Resolved = true)
{
    /// <summary>Where this sits on a grid: the start of the period it names, not Valve's placeholder.</summary>
    public DateOnly? GridDate => ReleaseMapper.PeriodStart(Date, Precision);

    public bool IsDated => Date is not null
        && Precision is DatePrecision.Exact or DatePrecision.Quarter or DatePrecision.Year;

    public string StoreUrl => $"https://store.steampowered.com/app/{AppId}/";

    public string When => Kind == EventKind.EarlyAccessExit && Date is { } d
        ? d.ToString("d MMM yyyy")
        : ReleaseMapper.Describe(Date, Precision, CustomMessage);

    public string SourceLabel => (Owned, Wishlisted) switch
    {
        (true, true) => "owned · wishlist",
        (true, false) => "owned",
        _ => "wishlist",
    };
}

public sealed record UpNextGroup(string Title, string Note, DatePrecision Tier, IReadOnlyList<CalendarEvent> Events);

public sealed record MonthView(
    int Year,
    int Month,
    IReadOnlyDictionary<DateOnly, List<CalendarEvent>> ByDay,
    IReadOnlyList<CalendarEvent> Approximate,
    IReadOnlyList<CalendarEvent> Undated,
    int TotalEvents);

public sealed record LibraryView(
    int OwnedCount,
    int WishlistCount,
    int EarlyAccessCount,
    int UnreleasedOwnedCount,
    int OutNowStillWishlisted,
    int NoDateOnRecordCount,
    IReadOnlyList<(int Year, int Count)> ByYear,
    IReadOnlyList<CalendarEvent> EarlyAccessExits);

/// <summary>Games you already paid for that haven't come out yet.</summary>
public sealed record OwnedUnreleasedView(
    IReadOnlyList<UpNextGroup> Groups,
    IReadOnlyList<CalendarEvent> EarlyAccessExits,
    IReadOnlyList<CalendarEvent> NoStoreData,
    int PendingCount,
    int OwnedCount);

public sealed record DelayRow(
    int AppId, string Name, DateTime SeenUtc,
    DateOnly? OldDate, DatePrecision OldPrecision,
    DateOnly? NewDate, DatePrecision NewPrecision, int? DaysMoved)
{
    public string OldText => ReleaseMapper.Describe(OldDate, OldPrecision, null);
    public string NewText => ReleaseMapper.Describe(NewDate, NewPrecision, null);
    public bool IsDelay => DaysMoved > 0;
    public bool IsPullIn => DaysMoved < 0;
}

public sealed class CalendarQueries(
    IDbContextFactory<CalendarDb> dbFactory,
    IOptions<CalendarOptions> options,
    SteamAccount account,
    TimeProvider clock)
{
    public DateOnly Today => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

    private long SteamId => account.SteamId;

    /// <summary>
    /// Every event for the configured account. A couple of hundred rows at most, so it is
    /// pulled into memory once and sliced in C# rather than translated into several queries.
    /// </summary>
    public async Task<List<CalendarEvent>> GetEventsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var rows = await db.Entries
            .Where(e => e.SteamId == SteamId)
            .GroupBy(e => e.AppId)
            .Select(g => new
            {
                AppId = g.Key,
                Owned = g.Any(e => e.Source == EntrySource.Owned),
                Wishlisted = g.Any(e => e.Source == EntrySource.Wishlist),
            })
            .Join(db.Apps, x => x.AppId, a => a.AppId, (x, a) => new { x.Owned, x.Wishlisted, App = a })
            .AsNoTracking()
            .ToListAsync(ct);

        var events = new List<CalendarEvent>(rows.Count + 16);

        foreach (var r in rows)
        {
            var a = r.App;

            events.Add(new CalendarEvent(
                a.AppId, a.Name, EventKind.Release, a.ReleaseDate, a.Precision,
                a.CustomMessage, a.CapsuleUrl, r.Owned, r.Wishlisted, a.IsComingSoon, a.Resolved));

            // A dated 1.0 launch is a second, separate event for the same app.
            if (a.EarlyAccessExitDate is { } ea)
                events.Add(new CalendarEvent(
                    a.AppId, a.Name, EventKind.EarlyAccessExit, ea, DatePrecision.Exact,
                    null, a.CapsuleUrl, r.Owned, r.Wishlisted, false, a.Resolved));
        }

        return events;
    }

    public async Task<List<UpNextGroup>> UpNextAsync(CancellationToken ct = default)
    {
        var today = Today;
        var all = await GetEventsAsync(ct);
        var groups = new List<UpNextGroup>();

        // Already out but never bought — the most actionable list in the app.
        var cutoff = today.AddDays(-options.Value.RecentlyReleasedDays);
        var outNow = all
            .Where(e => e.Kind == EventKind.Release && e.Wishlisted && !e.Owned
                     && e.Precision == DatePrecision.Exact
                     && e.Date is { } d && d <= today && d >= cutoff)
            .OrderByDescending(e => e.Date)
            .ToList();

        if (outNow.Count > 0)
            groups.Add(new("Out now — still on your wishlist", $"{outNow.Count} released in the last {options.Value.RecentlyReleasedDays} days", DatePrecision.Exact, outNow));

        // Everything still ahead, by how precisely it's dated.
        var future = all.Where(e => e.GridDate is { } g && g >= today || e.Date is { } d && d >= today).ToList();
        var endOfMonth = new DateOnly(today.Year, today.Month, DateTime.DaysInMonth(today.Year, today.Month));

        var thisMonth = future
            .Where(e => e.Precision == DatePrecision.Exact && e.Date is { } d && d >= today && d <= endOfMonth)
            .OrderBy(e => e.Date)
            .ToList();

        if (thisMonth.Count > 0)
            groups.Add(new("This month", Pluralise(thisMonth, "dated event"), DatePrecision.Exact, thisMonth));

        var dated = future
            .Where(e => e.Precision == DatePrecision.Exact && e.Date is { } d && d > endOfMonth)
            .OrderBy(e => e.Date)
            .ToList();

        if (dated.Count > 0)
            groups.Add(new("Dated, further out", Pluralise(dated, "release"), DatePrecision.Exact, dated));

        var quarter = future
            .Where(e => e.Precision == DatePrecision.Quarter)
            .OrderBy(e => e.Date)
            .ToList();

        if (quarter.Count > 0)
            groups.Add(new("Quarter known", "rendered as quarters, not days", DatePrecision.Quarter, quarter));

        var year = future
            .Where(e => e.Precision == DatePrecision.Year)
            .OrderBy(e => e.Date)
            .ToList();

        if (year.Count > 0)
            groups.Add(new("Year known", Pluralise(year, "release"), DatePrecision.Year, year));

        var undated = all
            .Where(e => e.Kind == EventKind.Release
                     && e.Precision is DatePrecision.ComingSoon or DatePrecision.Tba)
            .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        if (undated.Count > 0)
        {
            var tba = undated.Count(e => e.Precision == DatePrecision.Tba);
            groups.Add(new("No date yet", $"{tba} TBA + {undated.Count - tba} “coming soon”", DatePrecision.Tba, undated));
        }

        // Out already, but Steam won't say when. Only worth showing for things you don't
        // own — otherwise it's just library trivia.
        var noDateOnRecord = all
            .Where(e => e.Kind == EventKind.Release && e.Precision == DatePrecision.Unknown
                     && e.Wishlisted && !e.Owned)
            .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        if (noDateOnRecord.Count > 0)
            groups.Add(new("Out, but Steam gives no date", "delisted or region-locked", DatePrecision.Tba, noDateOnRecord));

        return groups;
    }

    /// <summary>
    /// The owned library's forward-looking slice: pre-orders, unreleased purchases, and
    /// Early Access games with a dated 1.0. Delisted apps the store won't describe are
    /// kept separate rather than counted as upcoming.
    /// </summary>
    public async Task<OwnedUnreleasedView> OwnedUnreleasedAsync(CancellationToken ct = default)
    {
        var today = Today;
        var all = await GetEventsAsync(ct);
        var owned = all.Where(e => e.Owned).ToList();

        var pending = owned
            .Where(e => e.Kind == EventKind.Release && e.IsComingSoon && e.Resolved)
            .ToList();

        var groups = new List<UpNextGroup>();

        void Add(string title, string note, DatePrecision tier, IEnumerable<CalendarEvent> items)
        {
            var list = items.ToList();
            if (list.Count > 0) groups.Add(new UpNextGroup(title, note, tier, list));
        }

        Add("Dated", "day-accurate", DatePrecision.Exact,
            pending.Where(e => e.Precision == DatePrecision.Exact).OrderBy(e => e.Date));

        Add("Quarter known", "rendered as quarters, not days", DatePrecision.Quarter,
            pending.Where(e => e.Precision == DatePrecision.Quarter).OrderBy(e => e.Date));

        Add("Year known", "year only", DatePrecision.Year,
            pending.Where(e => e.Precision == DatePrecision.Year).OrderBy(e => e.Date));

        Add("No date yet", "paid for, no date announced", DatePrecision.Tba,
            pending.Where(e => e.Precision is DatePrecision.ComingSoon or DatePrecision.Tba)
                   .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase));

        // Owned games still in Early Access with a 1.0 date ahead of them.
        var eaExits = owned
            .Where(e => e.Kind == EventKind.EarlyAccessExit && e.Date is { } d && d >= today)
            .OrderBy(e => e.Date)
            .ToList();

        // Delisted and region-locked apps. GetItems does return these, but with no name
        // and no release block at all — so Precision, not Resolved, is the real signal.
        var noStoreData = owned
            .Where(e => e.Kind == EventKind.Release
                     && (e.Precision == DatePrecision.Unknown || !e.Resolved))
            .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        return new OwnedUnreleasedView(
            groups, eaExits, noStoreData, pending.Count, owned.Count(e => e.Kind == EventKind.Release));
    }

    public async Task<MonthView> MonthAsync(int year, int month, CancellationToken ct = default)
    {
        var all = await GetEventsAsync(ct);
        var first = new DateOnly(year, month, 1);
        var last = first.AddMonths(1).AddDays(-1);
        var quarter = ReleaseMapper.Quarter(first);

        // Only day-accurate things go on the grid. A quarter-accurate game pinned to a
        // specific square would be a lie, so those live in the strip underneath.
        var byDay = all
            .Where(e => e.Precision == DatePrecision.Exact
                     && e.Date is { } d && d >= first && d <= last)
            .GroupBy(e => e.Date!.Value)
            .ToDictionary(g => g.Key, g => g.OrderBy(e => e.Kind).ThenBy(e => e.Name).ToList());

        // Fuzzy dates whose period covers the month being shown.
        var approximate = all
            .Where(e => e.Kind == EventKind.Release && e.Date is { } d
                     && ((e.Precision == DatePrecision.Quarter && d.Year == year && ReleaseMapper.Quarter(d) == quarter)
                      || (e.Precision == DatePrecision.Year && d.Year == year)))
            .OrderBy(e => e.Precision)
            .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        // No date at all — these could land in any month, so they are always listed.
        var undated = all
            .Where(e => e.Kind == EventKind.Release
                     && e.Precision is DatePrecision.ComingSoon or DatePrecision.Tba)
            .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        return new MonthView(year, month, byDay, approximate, undated, byDay.Sum(d => d.Value.Count));
    }

    public async Task<LibraryView> LibraryAsync(CancellationToken ct = default)
    {
        var today = Today;
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var rows = await db.Entries
            .Where(e => e.SteamId == SteamId)
            .GroupBy(e => e.AppId)
            .Select(g => new
            {
                AppId = g.Key,
                Owned = g.Any(e => e.Source == EntrySource.Owned),
                Wishlisted = g.Any(e => e.Source == EntrySource.Wishlist),
            })
            .Join(db.Apps, x => x.AppId, a => a.AppId, (x, a) => new { x.Owned, x.Wishlisted, App = a })
            .AsNoTracking()
            .ToListAsync(ct);

        var owned = rows.Where(r => r.Owned).ToList();

        var byYear = owned
            .Where(r => r.App.ReleaseDate is not null && !r.App.IsComingSoon)
            .GroupBy(r => r.App.ReleaseDate!.Value.Year)
            .Select(g => (Year: g.Key, Count: g.Count()))
            .OrderBy(x => x.Year)
            .ToList();

        var eaExits = rows
            .Where(r => r.App.EarlyAccessExitDate is { } ea && ea >= today)
            .OrderBy(r => r.App.EarlyAccessExitDate)
            .Select(r => new CalendarEvent(
                r.App.AppId, r.App.Name, EventKind.EarlyAccessExit, r.App.EarlyAccessExitDate,
                DatePrecision.Exact, null, r.App.CapsuleUrl, r.Owned, r.Wishlisted, false, r.App.Resolved))
            .ToList();

        var cutoff = today.AddDays(-options.Value.RecentlyReleasedDays);

        return new LibraryView(
            OwnedCount: owned.Count,
            WishlistCount: rows.Count(r => r.Wishlisted),
            EarlyAccessCount: rows.Count(r => r.Owned && r.App.IsEarlyAccess),
            UnreleasedOwnedCount: rows.Count(r => r.Owned && r.App.IsComingSoon),
            OutNowStillWishlisted: rows.Count(r => r.Wishlisted && !r.Owned && !r.App.IsComingSoon
                                                && r.App.ReleaseDate is { } d && d <= today && d >= cutoff),
            NoDateOnRecordCount: rows.Count(r => r.Owned && r.App.Precision == DatePrecision.Unknown),
            ByYear: byYear,
            EarlyAccessExits: eaExits);
    }

    public async Task<List<DelayRow>> DelaysAsync(int take = 100, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        return await db.DateChanges
            .OrderByDescending(c => c.SeenUtc)
            .Take(take)
            .Join(db.Apps, c => c.AppId, a => a.AppId, (c, a) => new DelayRow(
                c.AppId, a.Name, c.SeenUtc, c.OldDate, c.OldPrecision, c.NewDate, c.NewPrecision,
                c.OldDate != null && c.NewDate != null ? c.NewDate!.Value.DayNumber - c.OldDate!.Value.DayNumber : null))
            .AsNoTracking()
            .ToListAsync(ct);
    }

    public async Task<SyncRun?> LastSyncAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.SyncRuns
            .OrderByDescending(r => r.StartedUtc)
            .AsNoTracking()
            .FirstOrDefaultAsync(ct);
    }

    private static string Pluralise<T>(IReadOnlyCollection<T> items, string noun) =>
        $"{items.Count} {noun}{(items.Count == 1 ? "" : "s")}";
}
