using System.Text;
using ReleaseCalendar.Web.Data;
using ReleaseCalendar.Web.Services;
using Xunit;

namespace ReleaseCalendar.Tests;

public class IcsFeedTests
{
    private static readonly DateTime Stamp = new(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);

    private static CalendarEvent Event(
        string name = "Valor Mortis",
        DatePrecision precision = DatePrecision.Exact,
        DateOnly? date = null,
        EventKind kind = EventKind.Release,
        int appId = 2828710) =>
        new(appId, name, kind, date ?? new DateOnly(2026, 10, 13), precision, null, null, false, true);

    private static string[] Lines(string ics) => ics.Split("\r\n");

    [Fact]
    public void an_exact_release_is_an_all_day_confirmed_event()
    {
        var ics = IcsFeed.Build([Event()], "Steam Releases", Stamp);

        Assert.Contains("BEGIN:VCALENDAR", ics);
        Assert.Contains("DTSTART;VALUE=DATE:20261013", ics);

        // All-day DTEND is exclusive — the day *after* the release.
        Assert.Contains("DTEND;VALUE=DATE:20261014", ics);
        Assert.Contains("SUMMARY:Valor Mortis", ics);
        Assert.Contains("STATUS:CONFIRMED", ics);
        Assert.Contains("UID:2828710-0@release-calendar", ics);
        Assert.Contains("END:VCALENDAR", ics);
    }

    [Fact]
    public void every_line_ends_with_crlf()
    {
        var ics = IcsFeed.Build([Event()], "Steam Releases", Stamp);

        Assert.All(ics.Split("\r\n").SkipLast(1), line => Assert.DoesNotContain("\n", line));
        Assert.EndsWith("\r\n", ics);
    }

    [Fact]
    public void a_quarter_only_game_lands_at_the_start_of_the_quarter_and_is_tentative()
    {
        var ics = IcsFeed.Build(
            [Event("Low Level Hunters", DatePrecision.Quarter, new DateOnly(2027, 3, 31))],
            "Steam Releases", Stamp);

        // Valve's placeholder is 31 March; the feed must not claim that day.
        Assert.Contains("DTSTART;VALUE=DATE:20270101", ics);
        Assert.DoesNotContain("DTSTART;VALUE=DATE:20270331", ics);
        Assert.Contains("STATUS:TENTATIVE", ics);
        Assert.Contains("Q1 2027 (estimated)", ics);
    }

    [Fact]
    public void a_year_only_game_lands_on_the_first_of_january()
    {
        var ics = IcsFeed.Build(
            [Event("Witchbrook", DatePrecision.Year, new DateOnly(2026, 12, 31))],
            "Steam Releases", Stamp);

        Assert.Contains("DTSTART;VALUE=DATE:20260101", ics);
        Assert.Contains("sometime in 2026", ics);
        Assert.Contains("STATUS:TENTATIVE", ics);
    }

    [Theory]
    [InlineData(DatePrecision.Tba)]
    [InlineData(DatePrecision.ComingSoon)]
    public void undated_games_are_left_out_entirely(DatePrecision precision)
    {
        var ics = IcsFeed.Build(
            [new CalendarEvent(612400, "The Last Night", EventKind.Release, null, precision, "To be announced", null, false, true, true)],
            "Steam Releases", Stamp);

        Assert.DoesNotContain("BEGIN:VEVENT", ics);
        Assert.DoesNotContain("The Last Night", ics);
    }

    [Fact]
    public void an_early_access_exit_is_its_own_event()
    {
        var ics = IcsFeed.Build(
            [Event("Enshrouded", date: new DateOnly(2026, 10, 15), kind: EventKind.EarlyAccessExit, appId: 1203620)],
            "Steam Releases", Stamp);

        Assert.Contains("SUMMARY:Enshrouded leaves Early Access", ics);

        // Kind is part of the UID so a release and its 1.0 don't collide.
        Assert.Contains("UID:1203620-1@release-calendar", ics);
    }

    [Fact]
    public void release_and_early_access_events_for_one_app_get_distinct_uids()
    {
        var ics = IcsFeed.Build(
        [
            Event("Enshrouded", date: new DateOnly(2024, 1, 24), appId: 1203620),
            Event("Enshrouded", date: new DateOnly(2026, 10, 15), kind: EventKind.EarlyAccessExit, appId: 1203620),
        ], "Steam Releases", Stamp);

        var uids = Lines(ics).Where(l => l.StartsWith("UID:")).ToList();

        Assert.Equal(2, uids.Count);
        Assert.Equal(2, uids.Distinct().Count());
    }

    [Fact]
    public void special_characters_are_escaped()
    {
        var ics = IcsFeed.Build(
            [Event("Half-Life: Alyx, Chapter 1; a test \\ of escaping")],
            "Steam Releases", Stamp);

        Assert.Contains(@"Chapter 1\; a test", ics);
        Assert.Contains(@"Alyx\, Chapter", ics);
        Assert.Contains(@"test \\ of", ics);
    }

    [Fact]
    public void newlines_in_a_description_become_literal_escapes()
    {
        var ics = IcsFeed.Build([Event()], "Steam Releases", Stamp);

        var description = Lines(ics).First(l => l.StartsWith("DESCRIPTION:"));

        // A raw newline would terminate the property and corrupt the feed.
        Assert.Contains(@"\n", description);
    }

    [Fact]
    public void long_lines_are_folded_at_seventy_five_octets()
    {
        var ics = IcsFeed.Build(
            [Event(new string('A', 200))],
            "Steam Releases", Stamp);

        foreach (var line in Lines(ics).Where(l => l.Length > 0))
            Assert.True(Encoding.UTF8.GetByteCount(line) <= 75,
                $"line was {Encoding.UTF8.GetByteCount(line)} octets: {line[..Math.Min(40, line.Length)]}…");

        // Continuation lines are marked with a leading space.
        Assert.Contains(Lines(ics), l => l.StartsWith(" A"));
    }

    [Fact]
    public void folding_does_not_split_a_multi_byte_character()
    {
        // "。" is three octets in UTF-8; a byte-blind fold would cut it in half.
        var ics = IcsFeed.Build([Event(string.Concat(Enumerable.Repeat("。", 60)))], "Steam Releases", Stamp);

        foreach (var line in Lines(ics).Where(l => l.Length > 0))
            Assert.True(Encoding.UTF8.GetByteCount(line) <= 75);

        // Round-trips cleanly: unfold and the original text is intact.
        var unfolded = ics.Replace("\r\n ", "");
        Assert.Contains(string.Concat(Enumerable.Repeat("。", 60)), unfolded);
    }

    [Fact]
    public void events_are_ordered_by_date()
    {
        var ics = IcsFeed.Build(
        [
            Event("Later", date: new DateOnly(2027, 5, 1), appId: 2),
            Event("Sooner", date: new DateOnly(2026, 11, 1), appId: 1),
        ], "Steam Releases", Stamp);

        Assert.True(ics.IndexOf("Sooner", StringComparison.Ordinal) < ics.IndexOf("Later", StringComparison.Ordinal));
    }

    [Fact]
    public void releases_older_than_the_cutoff_are_left_out_of_a_subscription()
    {
        var events = new[]
        {
            Event("Ancient History", date: new DateOnly(2016, 6, 6), appId: 347170),
            Event("Out Last Month", date: new DateOnly(2026, 9, 15), appId: 2),
            Event("Coming Up", date: new DateOnly(2026, 10, 13), appId: 3),
        };

        var ics = IcsFeed.Build(events, "Steam Releases", Stamp, from: new DateOnly(2026, 4, 13));

        Assert.DoesNotContain("Ancient History", ics);
        Assert.Contains("Out Last Month", ics);
        Assert.Contains("Coming Up", ics);
    }

    [Fact]
    public void a_fuzzy_period_already_underway_lands_on_today_not_back_at_its_start()
    {
        var today = new DateOnly(2026, 10, 10);

        // Witchbrook is date_year 2026: period start 1 Jan, placeholder 31 Dec. Seen in
        // October it is still unreleased, so it must survive the cutoff and show up now.
        var ics = IcsFeed.Build(
            [Event("Witchbrook", DatePrecision.Year, new DateOnly(2026, 12, 31), appId: 1846700)],
            "Steam Releases", Stamp, from: today.AddDays(-180), today: today);

        Assert.Contains("Witchbrook", ics);
        Assert.Contains("DTSTART;VALUE=DATE:20261010", ics);
        Assert.DoesNotContain("DTSTART;VALUE=DATE:20260101", ics);
    }

    [Fact]
    public void a_fuzzy_period_entirely_in_the_past_is_still_dropped()
    {
        var today = new DateOnly(2026, 10, 10);

        var ics = IcsFeed.Build(
            [Event("Never Shipped", DatePrecision.Year, new DateOnly(2024, 12, 31), appId: 9)],
            "Steam Releases", Stamp, from: today.AddDays(-180), today: today);

        Assert.DoesNotContain("Never Shipped", ics);
    }

    [Fact]
    public void a_future_fuzzy_period_keeps_its_own_start_date()
    {
        var today = new DateOnly(2026, 10, 10);

        var ics = IcsFeed.Build(
            [Event("Low Level Hunters", DatePrecision.Quarter, new DateOnly(2027, 3, 31))],
            "Steam Releases", Stamp, from: today.AddDays(-180), today: today);

        Assert.Contains("DTSTART;VALUE=DATE:20270101", ics);
    }

    [Fact]
    public void an_exact_past_release_is_not_dragged_forward_to_today()
    {
        var today = new DateOnly(2026, 10, 10);

        var ics = IcsFeed.Build(
            [Event("Out Last Month", DatePrecision.Exact, new DateOnly(2026, 9, 15), appId: 2)],
            "Steam Releases", Stamp, from: today.AddDays(-180), today: today);

        Assert.Contains("DTSTART;VALUE=DATE:20260915", ics);
    }

    [Fact]
    public void a_recent_release_you_already_own_is_not_in_the_feed()
    {
        var today = new DateOnly(2026, 10, 10);

        var owned = new CalendarEvent(
            1, "Bought It Already", EventKind.Release, new DateOnly(2026, 9, 1),
            DatePrecision.Exact, null, null, Owned: true, Wishlisted: false);

        var stillWanted = new CalendarEvent(
            2, "Still Unbought", EventKind.Release, new DateOnly(2026, 9, 1),
            DatePrecision.Exact, null, null, Owned: false, Wishlisted: true);

        var ics = IcsFeed.Build([owned, stillWanted], "Steam Releases", Stamp,
            from: today.AddDays(-180), today: today);

        Assert.DoesNotContain("Bought It Already", ics);
        Assert.Contains("Still Unbought", ics);
    }

    [Fact]
    public void a_future_release_you_already_own_stays_in_the_feed()
    {
        var today = new DateOnly(2026, 10, 10);

        var preorder = new CalendarEvent(
            1, "Preordered", EventKind.Release, new DateOnly(2026, 12, 1),
            DatePrecision.Exact, null, null, Owned: true, Wishlisted: false);

        var ics = IcsFeed.Build([preorder], "Steam Releases", Stamp,
            from: today.AddDays(-180), today: today);

        Assert.Contains("Preordered", ics);
    }

    [Fact]
    public void an_early_access_exit_you_own_stays_in_the_feed()
    {
        var today = new DateOnly(2026, 10, 10);

        var exit = new CalendarEvent(
            2124120, "SULFUR", EventKind.EarlyAccessExit, new DateOnly(2026, 10, 28),
            DatePrecision.Exact, null, null, Owned: true, Wishlisted: false);

        var ics = IcsFeed.Build([exit], "Steam Releases", Stamp,
            from: today.AddDays(-180), today: today);

        Assert.Contains("SULFUR leaves Early Access", ics);
    }

    [Fact]
    public void without_a_cutoff_everything_dated_is_included()
    {
        var ics = IcsFeed.Build(
            [Event("Ancient History", date: new DateOnly(2016, 6, 6), appId: 347170)],
            "Steam Releases", Stamp);

        Assert.Contains("Ancient History", ics);
    }

    [Fact]
    public void the_calendar_advertises_a_refresh_interval()
    {
        var ics = IcsFeed.Build([], "Steam Releases", Stamp);

        Assert.Contains("REFRESH-INTERVAL;VALUE=DURATION:PT6H", ics);
        Assert.Contains("X-WR-CALNAME:Steam Releases", ics);
        Assert.Contains("DTSTAMP", IcsFeed.Build([Event()], "x", Stamp));
    }
}
