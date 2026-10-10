using ReleaseCalendar.Web.Data;

namespace ReleaseCalendar.Web.Steam;

/// <summary>What a store item's <c>release</c> block actually means.</summary>
/// <param name="Date">
/// The release date, or null when there isn't one. For <see cref="DatePrecision.Quarter"/>
/// and <see cref="DatePrecision.Year"/> this is Valve's placeholder — the last day of the
/// period — so it must never be rendered as an exact day.
/// </param>
public readonly record struct MappedRelease(
    DateOnly? Date,
    DatePrecision Precision,
    bool IsComingSoon,
    string? CustomMessage,
    DateOnly? EarlyAccessExitDate);

public static class ReleaseMapper
{
    /// <summary>
    /// Steam's release timestamps are local-midnight values for the store's region, which
    /// lands at 07:00–08:00 UTC the same calendar day. Taking the UTC date reproduces the
    /// day the store page shows (verified against TF2, Baldur's Gate 3 and 157 wishlist items).
    /// </summary>
    public static DateOnly ToDate(long unixSeconds) =>
        DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime);

    public static MappedRelease Map(StoreRelease? r)
    {
        // No release block at all. Delisted games come back this way, so calling it
        // "coming soon" would file Battle vs Chess alongside Deadlock.
        if (r is null)
            return new(null, DatePrecision.Unknown, false, null, null);

        var precision = r.ComingSoonDisplay switch
        {
            "date_full" => DatePrecision.Exact,
            "date_quarter" => DatePrecision.Quarter,
            "date_year" => DatePrecision.Year,
            "text_comingsoon" => DatePrecision.ComingSoon,
            "text_tba" => DatePrecision.Tba,

            // Already shipped: no coming_soon_display at all.
            null when !r.IsComingSoon => DatePrecision.Exact,

            // Unknown enum value from a future Steam change: degrade, don't throw.
            _ => DatePrecision.ComingSoon,
        };

        // A timestamp is only meaningful for the three dated tiers. text_tba and
        // text_comingsoon items can still carry one; it is not a date.
        var dated = precision is DatePrecision.Exact or DatePrecision.Quarter or DatePrecision.Year;

        var date = dated && (r.SteamReleaseDate ?? r.OriginalReleaseDate) is { } ts and > 0
            ? ToDate(ts)
            : (DateOnly?)null;

        // A dated tier with no timestamp is a contradiction Steam does return — delisted
        // apps, betas and region-locked items. Left as Exact-with-no-date these fall
        // through every calendar tier unseen, so name the state instead.
        if (dated && date is null)
            precision = r.IsComingSoon ? DatePrecision.Tba : DatePrecision.Unknown;

        // An Early Access exit only matters while it is still ahead of the game.
        var eaExit = r.ReleaseFromEarlyAccessDate is { } ea and > 0 ? ToDate(ea) : (DateOnly?)null;

        return new(date, precision, r.IsComingSoon, r.CustomReleaseDateMessage, eaExit);
    }

    /// <summary>
    /// Human-readable date for a precision tier. Never prints a day for a fuzzy date.
    /// </summary>
    public static string Describe(DateOnly? date, DatePrecision precision, string? customMessage) => precision switch
    {
        DatePrecision.Exact when date is { } d => d.ToString("d MMM yyyy"),
        DatePrecision.Quarter when date is { } d => $"Q{Quarter(d)} {d.Year}",
        DatePrecision.Year when date is { } d => d.Year.ToString(),
        DatePrecision.ComingSoon => customMessage is { Length: > 0 } m ? m : "Coming soon",
        DatePrecision.Tba => customMessage is { Length: > 0 } m ? m : "To be announced",
        _ => customMessage is { Length: > 0 } m ? m : "No date",
    };

    public static int Quarter(DateOnly d) => (d.Month - 1) / 3 + 1;

    /// <summary>
    /// Where a fuzzy date should sit on a calendar: the *start* of the period it names,
    /// so a "Q2 2027" game appears when the quarter opens rather than on 30 June.
    /// </summary>
    public static DateOnly? PeriodStart(DateOnly? date, DatePrecision precision) => (date, precision) switch
    {
        ({ } d, DatePrecision.Exact) => d,
        ({ } d, DatePrecision.Quarter) => new DateOnly(d.Year, (Quarter(d) - 1) * 3 + 1, 1),
        ({ } d, DatePrecision.Year) => new DateOnly(d.Year, 1, 1),
        _ => null,
    };

    /// <summary>Build a usable image URL out of the store's asset format string.</summary>
    public static string? CapsuleUrl(StoreAssets? a)
    {
        if (a?.AssetUrlFormat is not { Length: > 0 } format) return null;
        var file = a.Header ?? a.MainCapsule ?? a.SmallCapsule ?? a.LibraryCapsule;
        if (file is null) return null;
        return "https://shared.akamai.steamstatic.com/store_item_assets/"
             + format.Replace("${FILENAME}", file);
    }
}
