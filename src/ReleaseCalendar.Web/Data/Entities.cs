namespace ReleaseCalendar.Web.Data;

/// <summary>
/// How much Valve actually knows about a release date. Comes straight from the
/// store's <c>coming_soon_display</c> field — never inferred from the timestamp,
/// because fuzzy dates still carry a real-looking placeholder timestamp.
/// </summary>
public enum DatePrecision
{
    /// <summary>A real calendar day. Safe to put on a grid.</summary>
    Exact = 0,
    /// <summary>Quarter known. Timestamp is the last day of that quarter.</summary>
    Quarter = 1,
    /// <summary>Year known. Timestamp is 31 December of that year.</summary>
    Year = 2,
    /// <summary>No date, store says "Coming soon".</summary>
    ComingSoon = 3,
    /// <summary>No date, nothing announced.</summary>
    Tba = 4,

    /// <summary>
    /// Already out, but Steam returned no timestamp — delisted apps, betas and
    /// region-locked items do this. Distinct from <see cref="Tba"/>, which means
    /// "announced, not dated": these are not upcoming, so they stay out of the
    /// forward-looking tiers while still being counted rather than vanishing.
    /// </summary>
    Unknown = 5,
}

public enum EntrySource
{
    Wishlist = 0,
    Owned = 1,
}

public enum EventKind
{
    Release = 0,
    EarlyAccessExit = 1,
}

/// <summary>
/// A Steam store item. Named SteamApp rather than App so it doesn't collide with
/// the Blazor root component.
/// </summary>
public class SteamApp
{
    public int AppId { get; set; }
    public string Name { get; set; } = "";
    public string? StoreUrlPath { get; set; }
    public string? CapsuleUrl { get; set; }
    public bool IsFree { get; set; }

    public DateOnly? ReleaseDate { get; set; }
    public DatePrecision Precision { get; set; }
    public bool IsComingSoon { get; set; }
    public bool IsEarlyAccess { get; set; }
    public string? CustomMessage { get; set; }

    /// <summary>Set when a game in Early Access has a dated 1.0 launch.</summary>
    public DateOnly? EarlyAccessExitDate { get; set; }

    public DateTime FetchedUtc { get; set; }

    /// <summary>
    /// False while the store has never returned this app — delisted and region-locked
    /// items in a library are never resolvable, and must not be presented as upcoming
    /// games just because nothing is known about them.
    /// </summary>
    public bool Resolved { get; set; }

    public string StoreUrl => $"https://store.steampowered.com/app/{AppId}/";
}

public class Entry
{
    public long SteamId { get; set; }
    public int AppId { get; set; }
    public EntrySource Source { get; set; }

    public int? Priority { get; set; }
    public DateTime? AddedUtc { get; set; }
    public int? PlaytimeMinutes { get; set; }

    public SteamApp? App { get; set; }
}

/// <summary>
/// Every time a release date or its precision moves. Steam never shows you this,
/// and it can't be backfilled — so it is recorded from the first sync onward.
/// </summary>
public class DateChange
{
    public int Id { get; set; }
    public int AppId { get; set; }
    public DateTime SeenUtc { get; set; }

    public DateOnly? OldDate { get; set; }
    public DatePrecision OldPrecision { get; set; }
    public DateOnly? NewDate { get; set; }
    public DatePrecision NewPrecision { get; set; }

    public SteamApp? App { get; set; }

    /// <summary>Positive when the date moved later (a delay), negative when it was pulled in.</summary>
    public int? DaysMoved => OldDate is { } o && NewDate is { } n ? n.DayNumber - o.DayNumber : null;
}

public class SyncRun
{
    public int Id { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime? FinishedUtc { get; set; }

    public int WishlistItems { get; set; }
    public int OwnedItems { get; set; }
    public int AppsRefreshed { get; set; }
    public int DateChanges { get; set; }
    public int StoreRequests { get; set; }

    public string? Error { get; set; }

    public bool Succeeded => FinishedUtc is not null && Error is null;
}
