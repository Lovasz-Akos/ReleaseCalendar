using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ReleaseCalendar.Web;
using ReleaseCalendar.Web.Data;
using ReleaseCalendar.Web.Services;
using Xunit;

namespace ReleaseCalendar.Tests;

public class CalendarQueriesTests
{
    private const long Me = 76561190000000000;
    private static readonly DateOnly Today = new(2026, 10, 10);

    private static async Task<TestHost> SeedAsync(params SteamApp[] apps)
    {
        var host = new TestHost(new SteamStub().Build(), new CalendarOptions { SteamId = Me });

        await using var db = host.NewDb();
        foreach (var app in apps)
        {
            app.FetchedUtc = host.Clock.Now.UtcDateTime;
            db.Apps.Add(app);
            db.Entries.Add(new Entry { SteamId = Me, AppId = app.AppId, Source = EntrySource.Wishlist });
        }
        await db.SaveChangesAsync();

        return host;
    }

    private static SteamApp Upcoming(int id, string name, DatePrecision precision, DateOnly? date) =>
        new()
        {
            AppId = id, Name = name, Precision = precision, ReleaseDate = date,
            IsComingSoon = true, Resolved = true,
        };

    private static SteamApp Released(int id, string name, DateOnly date) =>
        new()
        {
            AppId = id, Name = name, Precision = DatePrecision.Exact, ReleaseDate = date,
            IsComingSoon = false, Resolved = true,
        };

    [Fact]
    public async Task up_next_groups_by_how_precisely_steam_has_dated_things()
    {
        await using var host = await SeedAsync(
            Upcoming(1, "This Month", DatePrecision.Exact, Today.AddDays(5)),
            Upcoming(2, "Further Out", DatePrecision.Exact, new DateOnly(2027, 2, 4)),
            Upcoming(3, "Quarter Only", DatePrecision.Quarter, new DateOnly(2027, 3, 31)),
            Upcoming(4, "Year Only", DatePrecision.Year, new DateOnly(2027, 12, 31)),
            Upcoming(5, "No Date", DatePrecision.Tba, null),
            Released(6, "Out Last Week", Today.AddDays(-7)));

        var groups = await host.Queries.UpNextAsync();
        var titles = groups.Select(g => g.Title).ToList();

        Assert.Contains("Out now — still on your wishlist", titles);
        Assert.Contains("This month", titles);
        Assert.Contains("Dated, further out", titles);
        Assert.Contains("Quarter known", titles);
        Assert.Contains("Year known", titles);
        Assert.Contains("No date yet", titles);

        Assert.Equal("This Month", Single(groups, "This month").Name);
        Assert.Equal("Out Last Week", Single(groups, "Out now — still on your wishlist").Name);
        Assert.Equal("No Date", Single(groups, "No date yet").Name);
    }

    [Fact]
    public async Task an_owned_game_is_not_offered_as_out_now_still_wishlisted()
    {
        await using var host = await SeedAsync(Released(1, "Already Bought", Today.AddDays(-7)));

        await using (var db = host.NewDb())
        {
            db.Entries.Add(new Entry { SteamId = Me, AppId = 1, Source = EntrySource.Owned });
            await db.SaveChangesAsync();
        }

        var groups = await host.Queries.UpNextAsync();

        Assert.DoesNotContain("Out now — still on your wishlist", groups.Select(g => g.Title));
    }

    [Fact]
    public async Task a_release_long_past_drops_out_of_the_out_now_tier()
    {
        await using var host = await SeedAsync(Released(1, "Ancient", Today.AddDays(-400)));

        var groups = await host.Queries.UpNextAsync();

        Assert.DoesNotContain("Out now — still on your wishlist", groups.Select(g => g.Title));
    }

    [Fact]
    public async Task empty_tiers_are_omitted_rather_than_rendered_blank()
    {
        await using var host = await SeedAsync(Upcoming(1, "Only One", DatePrecision.Tba, null));

        var groups = await host.Queries.UpNextAsync();

        Assert.Single(groups);
        Assert.Equal("No date yet", groups[0].Title);
    }

    [Fact]
    public async Task an_early_access_exit_becomes_a_second_event_for_the_same_app()
    {
        await using var host = await SeedAsync(new SteamApp
        {
            AppId = 1203620,
            Name = "Enshrouded",
            Precision = DatePrecision.Exact,
            ReleaseDate = new DateOnly(2024, 1, 24),
            IsComingSoon = false,
            IsEarlyAccess = true,
            EarlyAccessExitDate = new DateOnly(2026, 10, 15),
        });

        var events = await host.Queries.GetEventsAsync();

        Assert.Equal(2, events.Count);
        Assert.Single(events, e => e.Kind == EventKind.Release);

        var exit = events.Single(e => e.Kind == EventKind.EarlyAccessExit);
        Assert.Equal(new DateOnly(2026, 10, 15), exit.Date);
        Assert.Equal(DatePrecision.Exact, exit.Precision);
    }

    [Fact]
    public async Task the_month_grid_only_carries_day_accurate_events()
    {
        await using var host = await SeedAsync(
            Upcoming(1, "Dated", DatePrecision.Exact, new DateOnly(2026, 10, 13)),
            Upcoming(2, "Quarter Only", DatePrecision.Quarter, new DateOnly(2026, 12, 31)),
            Upcoming(3, "Year Only", DatePrecision.Year, new DateOnly(2026, 12, 31)),
            Upcoming(4, "No Date", DatePrecision.Tba, null));

        var view = await host.Queries.MonthAsync(2026, 10);

        // Only the exact one gets a square.
        Assert.Equal(1, view.TotalEvents);
        Assert.Equal("Dated", view.ByDay[new DateOnly(2026, 10, 13)].Single().Name);

        // The year-only game overlaps October 2026, so it shows in the strip underneath.
        Assert.Contains("Year Only", view.Approximate.Select(e => e.Name));

        // The Q4 game also overlaps October.
        Assert.Contains("Quarter Only", view.Approximate.Select(e => e.Name));

        // Nothing is silently hidden.
        Assert.Contains("No Date", view.Undated.Select(e => e.Name));
    }

    [Fact]
    public async Task a_quarter_only_game_does_not_appear_in_a_month_outside_its_quarter()
    {
        await using var host = await SeedAsync(
            Upcoming(1, "Q4 Game", DatePrecision.Quarter, new DateOnly(2026, 12, 31)));

        var july = await host.Queries.MonthAsync(2026, 7);
        var october = await host.Queries.MonthAsync(2026, 10);

        Assert.Empty(july.Approximate);
        Assert.Single(october.Approximate);
    }

    [Fact]
    public async Task library_counts_split_owned_from_wishlisted()
    {
        await using var host = await SeedAsync(
            Released(1, "Owned And Played", new DateOnly(2020, 5, 1)),
            Released(2, "Out, Not Bought", Today.AddDays(-30)),
            Upcoming(3, "Owned Preorder", DatePrecision.Exact, Today.AddDays(40)));

        await using (var db = host.NewDb())
        {
            db.Entries.Add(new Entry { SteamId = Me, AppId = 1, Source = EntrySource.Owned, PlaytimeMinutes = 500 });
            db.Entries.Add(new Entry { SteamId = Me, AppId = 3, Source = EntrySource.Owned });
            await db.SaveChangesAsync();
        }

        var view = await host.Queries.LibraryAsync();

        Assert.Equal(2, view.OwnedCount);
        Assert.Equal(3, view.WishlistCount);
        Assert.Equal(1, view.OutNowStillWishlisted);
        Assert.Equal(1, view.UnreleasedOwnedCount);
        Assert.Contains((2020, 1), view.ByYear);
    }

    [Fact]
    public async Task delays_list_the_newest_change_first()
    {
        await using var host = await SeedAsync(Upcoming(1, "Slippery", DatePrecision.Exact, new DateOnly(2027, 1, 1)));

        await using (var db = host.NewDb())
        {
            db.DateChanges.Add(new DateChange
            {
                AppId = 1, SeenUtc = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
                OldDate = new DateOnly(2026, 11, 1), OldPrecision = DatePrecision.Exact,
                NewDate = new DateOnly(2026, 12, 1), NewPrecision = DatePrecision.Exact,
            });
            db.DateChanges.Add(new DateChange
            {
                AppId = 1, SeenUtc = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
                OldDate = new DateOnly(2026, 12, 1), OldPrecision = DatePrecision.Exact,
                NewDate = new DateOnly(2027, 1, 1), NewPrecision = DatePrecision.Exact,
            });
            await db.SaveChangesAsync();
        }

        var rows = await host.Queries.DelaysAsync();

        Assert.Equal(2, rows.Count);
        Assert.Equal(new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), rows[0].SeenUtc);
        Assert.Equal("Slippery", rows[0].Name);
        Assert.Equal(31, rows[0].DaysMoved);
        Assert.True(rows[0].IsDelay);
    }

    [Fact]
    public async Task owned_unreleased_separates_preorders_from_delisted_apps()
    {
        var preorder = Upcoming(1, "Deadlock", DatePrecision.Tba, null);
        var datedPreorder = Upcoming(2, "Bought Early", DatePrecision.Exact, Today.AddDays(40));
        var delisted = new SteamApp
        {
            AppId = 3, Name = "App 3", Precision = DatePrecision.Unknown,
            IsComingSoon = false, Resolved = false,
        };
        var shipped = Released(4, "Played To Death", new DateOnly(2019, 1, 1));

        await using var host = await SeedAsync(preorder, datedPreorder, delisted, shipped);

        await using (var db = host.NewDb())
        {
            foreach (var id in new[] { 1, 2, 3, 4 })
                db.Entries.Add(new Entry { SteamId = Me, AppId = id, Source = EntrySource.Owned });
            await db.SaveChangesAsync();
        }

        var view = await host.Queries.OwnedUnreleasedAsync();

        // Two real pre-orders; the delisted app is not one of them.
        Assert.Equal(2, view.PendingCount);
        Assert.Equal(4, view.OwnedCount);

        var pending = view.Groups.SelectMany(g => g.Events).Select(e => e.Name).ToList();
        Assert.Contains("Deadlock", pending);
        Assert.Contains("Bought Early", pending);
        Assert.DoesNotContain("App 3", pending);
        Assert.DoesNotContain("Played To Death", pending);

        Assert.Single(view.NoStoreData);
        Assert.Equal(3, view.NoStoreData[0].AppId);
    }

    [Fact]
    public async Task owned_unreleased_lists_a_pending_one_point_oh_but_not_a_past_one()
    {
        await using var host = await SeedAsync(
            new SteamApp
            {
                AppId = 1, Name = "SULFUR", Precision = DatePrecision.Exact,
                ReleaseDate = new DateOnly(2023, 1, 1), IsComingSoon = false,
                IsEarlyAccess = true, EarlyAccessExitDate = Today.AddDays(18), Resolved = true,
            },
            new SteamApp
            {
                AppId = 2, Name = "Palworld", Precision = DatePrecision.Exact,
                ReleaseDate = new DateOnly(2024, 1, 19), IsComingSoon = false,
                EarlyAccessExitDate = Today.AddDays(-90), Resolved = true,
            });

        await using (var db = host.NewDb())
        {
            db.Entries.Add(new Entry { SteamId = Me, AppId = 1, Source = EntrySource.Owned });
            db.Entries.Add(new Entry { SteamId = Me, AppId = 2, Source = EntrySource.Owned });
            await db.SaveChangesAsync();
        }

        var view = await host.Queries.OwnedUnreleasedAsync();

        Assert.Single(view.EarlyAccessExits);
        Assert.Equal("SULFUR", view.EarlyAccessExits[0].Name);
    }

    [Fact]
    public async Task a_wishlisted_only_preorder_is_not_on_the_owned_page()
    {
        await using var host = await SeedAsync(Upcoming(1, "Wishlisted Only", DatePrecision.Exact, Today.AddDays(20)));

        var view = await host.Queries.OwnedUnreleasedAsync();

        Assert.Equal(0, view.PendingCount);
        Assert.Empty(view.Groups);
    }

    [Fact]
    public async Task a_delisted_wishlist_item_is_surfaced_separately_not_as_upcoming()
    {
        await using var host = await SeedAsync(new SteamApp
        {
            AppId = 1, Name = "Battle vs Chess", Precision = DatePrecision.Unknown,
            IsComingSoon = false, Resolved = true,
        });

        var groups = await host.Queries.UpNextAsync();
        var titles = groups.Select(g => g.Title).ToList();

        // Not pretending it's coming...
        Assert.DoesNotContain("No date yet", titles);

        // ...but not silently dropped either.
        Assert.Contains("Out, but Steam gives no date", titles);
        Assert.Equal("Battle vs Chess", Single(groups, "Out, but Steam gives no date").Name);
    }

    [Fact]
    public async Task the_feed_is_built_from_the_cache()
    {
        await using var host = await SeedAsync(
            Upcoming(2828710, "Valor Mortis", DatePrecision.Exact, new DateOnly(2026, 10, 13)),
            Upcoming(612400, "The Last Night", DatePrecision.Tba, null));

        var feed = host.Services.GetRequiredService<IcsFeed>();
        var ics = await feed.BuildAsync();

        Assert.Contains("SUMMARY:Valor Mortis", ics);
        Assert.DoesNotContain("The Last Night", ics);
    }

    private static CalendarEvent Single(List<UpNextGroup> groups, string title) =>
        groups.Single(g => g.Title == title).Events.Single();
}
