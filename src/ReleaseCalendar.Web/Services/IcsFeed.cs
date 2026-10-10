using System.Text;
using Microsoft.Extensions.Options;
using ReleaseCalendar.Web;
using ReleaseCalendar.Web.Data;
using ReleaseCalendar.Web.Steam;

namespace ReleaseCalendar.Web.Services;

/// <summary>
/// Builds an RFC 5545 calendar feed. Subscribe once in Google Calendar / Outlook / iOS and
/// releases keep arriving — the highest-value feature per line of code in the project.
/// </summary>
public sealed class IcsFeed(CalendarQueries queries, IOptions<CalendarOptions> options, TimeProvider clock)
{
    public async Task<string> BuildAsync(CancellationToken ct = default)
    {
        var events = await queries.GetEventsAsync(ct);
        var now = clock.GetUtcNow().UtcDateTime;

        // A subscription is for what's coming, not a decade of back catalogue. Recent
        // releases stay in because "out now, still not bought" is worth seeing.
        var today = DateOnly.FromDateTime(now);
        var from = today.AddDays(-options.Value.RecentlyReleasedDays);

        return Build(events, "Steam Releases", now, from, today);
    }

    public static string Build(
        IEnumerable<CalendarEvent> events, string calendarName, DateTime stampUtc,
        DateOnly? from = null, DateOnly? today = null)
    {
        var sb = new StringBuilder();
        var stamp = stampUtc.ToString("yyyyMMdd'T'HHmmss'Z'");

        Write(sb, "BEGIN:VCALENDAR");
        Write(sb, "VERSION:2.0");
        Write(sb, "PRODID:-//ReleaseCalendar//Steam Release Calendar//EN");
        Write(sb, "CALSCALE:GREGORIAN");
        Write(sb, "METHOD:PUBLISH");
        Write(sb, $"X-WR-CALNAME:{Escape(calendarName)}");
        Write(sb, "X-PUBLISHED-TTL:PT6H");
        Write(sb, "REFRESH-INTERVAL;VALUE=DURATION:PT6H");

        foreach (var e in events.OrderBy(e => e.GridDate).ThenBy(e => e.Name))
        {
            // Undated games have nowhere to sit; they appear once Steam dates them.
            if (e.GridDate is not { } start) continue;

            // "Sometime in 2026", seen in October, belongs on today — not back in January.
            // Without this, a fuzzy period already underway reads as a past event and the
            // cutoff below would drop a game that hasn't come out yet.
            if (today is { } t
                && start < t
                && e.Precision is DatePrecision.Quarter or DatePrecision.Year
                && e.Date is { } periodEnd && periodEnd >= t)
            {
                start = t;
            }

            if (from is { } cutoff && start < cutoff) continue;

            // A past release is only worth a calendar entry if you still haven't bought it.
            // An owned game that shipped three months ago is nothing to act on.
            if (today is { } t2 && start < t2 && !(e.Wishlisted && !e.Owned)) continue;

            Write(sb, "BEGIN:VEVENT");
            Write(sb, $"UID:{e.AppId}-{(int)e.Kind}@release-calendar");
            Write(sb, $"DTSTAMP:{stamp}");

            // All-day event: DTEND is exclusive, so it is the following day.
            Write(sb, $"DTSTART;VALUE=DATE:{start:yyyyMMdd}");
            Write(sb, $"DTEND;VALUE=DATE:{start.AddDays(1):yyyyMMdd}");

            Write(sb, $"SUMMARY:{Escape(Summary(e))}");
            Write(sb, $"DESCRIPTION:{Escape(Description(e))}");
            Write(sb, $"URL:{e.StoreUrl}");
            Write(sb, "TRANSP:TRANSPARENT");

            // Fuzzy dates are estimates, so mark them tentative rather than confirmed.
            Write(sb, e.Precision == DatePrecision.Exact ? "STATUS:CONFIRMED" : "STATUS:TENTATIVE");

            Write(sb, "END:VEVENT");
        }

        Write(sb, "END:VCALENDAR");
        return sb.ToString();
    }

    internal static string Summary(CalendarEvent e) => e.Kind switch
    {
        EventKind.EarlyAccessExit => $"{e.Name} leaves Early Access",
        _ => e.Precision switch
        {
            DatePrecision.Quarter when e.Date is { } d => $"{e.Name} — Q{ReleaseMapper.Quarter(d)} {d.Year} (estimated)",
            DatePrecision.Year when e.Date is { } d => $"{e.Name} — sometime in {d.Year}",
            _ => e.Name,
        },
    };

    internal static string Description(CalendarEvent e)
    {
        var parts = new List<string>
        {
            e.Kind == EventKind.EarlyAccessExit ? "Leaves Early Access" : $"Release: {e.When}",
            $"On your {e.SourceLabel}",
        };

        if (e.Precision is DatePrecision.Quarter or DatePrecision.Year)
            parts.Add("Steam has only given a " + (e.Precision == DatePrecision.Quarter ? "quarter" : "year")
                    + " — this date is the start of that period, not a confirmed day.");

        parts.Add(e.StoreUrl);
        return string.Join("\n", parts);
    }

    /// <summary>RFC 5545 §3.3.11 — escape backslash, semicolon, comma and newlines.</summary>
    internal static string Escape(string value) => value
        .Replace("\\", "\\\\")
        .Replace(";", "\\;")
        .Replace(",", "\\,")
        .Replace("\r\n", "\\n")
        .Replace("\n", "\\n")
        .Replace("\r", "\\n");

    /// <summary>
    /// RFC 5545 §3.1 — content lines are folded at 75 octets, continuations begin with a
    /// space. Folding counts bytes, not characters, so a multi-byte title can't be split
    /// mid-codepoint.
    /// </summary>
    internal static void Write(StringBuilder sb, string line)
    {
        const int maxOctets = 75;
        var bytes = Encoding.UTF8.GetByteCount(line);

        if (bytes <= maxOctets)
        {
            sb.Append(line).Append("\r\n");
            return;
        }

        var budget = maxOctets;
        var current = new StringBuilder();
        var used = 0;

        foreach (var rune in line.EnumerateRunes())
        {
            var size = Encoding.UTF8.GetByteCount(rune.ToString());

            if (used + size > budget)
            {
                sb.Append(current).Append("\r\n");
                current.Clear();
                current.Append(' ');     // continuation marker
                used = 1;
                budget = maxOctets;
            }

            current.Append(rune.ToString());
            used += size;
        }

        if (current.Length > 0) sb.Append(current).Append("\r\n");
    }
}
