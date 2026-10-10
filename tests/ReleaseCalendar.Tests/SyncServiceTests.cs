using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ReleaseCalendar.Web;
using ReleaseCalendar.Web.Data;
using ReleaseCalendar.Web.Services;
using Xunit;

namespace ReleaseCalendar.Tests;

public class SyncServiceTests
{
    private const long Me = 76561190000000000;

    // Built through the serializer rather than hand-written JSON so the shapes stay valid.
    private static string Wishlist(params (int AppId, int Priority)[] items) =>
        JsonSerializer.Serialize(new
        {
            response = new
            {
                items = items.Select(i => new { appid = i.AppId, priority = i.Priority, date_added = 1727629363 }),
            },
        });

    private static string Items(params object[] storeItems) =>
        JsonSerializer.Serialize(new { response = new { store_items = storeItems } });

    private static object Item(
        int appId, string name, string? display = null, long? releaseTs = null,
        bool comingSoon = false, long? eaExitTs = null)
    {
        var release = new Dictionary<string, object>();
        if (releaseTs is { } ts) release["steam_release_date"] = ts;
        if (comingSoon) release["is_coming_soon"] = true;
        if (display is not null) release["coming_soon_display"] = display;
        if (eaExitTs is { } ea) release["release_from_early_access_date"] = ea;

        return new { appid = appId, success = 1, visible = true, name, type = 0, release };
    }

    [Fact]
    public async Task a_first_sync_creates_apps_and_entries()
    {
        var stub = new SteamStub
        {
            WishlistJson = Wishlist((440, 0), (2828710, 1)),
            GetItemsJson = Items(
                Item(440, "Team Fortress 2", releaseTs: 1191999600),
                Item(2828710, "Valor Mortis", "date_full", 1791910800, comingSoon: true)),
        };

        await using var host = new TestHost(stub.Build());

        var run = await host.Sync.SyncAsync();

        Assert.Null(run.Error);
        Assert.Equal(2, run.WishlistItems);
        Assert.Equal(2, run.AppsRefreshed);

        // The first sighting of a game is not a date change.
        Assert.Equal(0, run.DateChanges);

        await using var db = host.NewDb();
        Assert.Equal(2, await db.Apps.CountAsync());
        Assert.Equal(2, await db.Entries.CountAsync(e => e.SteamId == Me && e.Source == EntrySource.Wishlist));

        var valor = await db.Apps.SingleAsync(a => a.AppId == 2828710);
        Assert.Equal("Valor Mortis", valor.Name);
        Assert.Equal(new DateOnly(2026, 10, 13), valor.ReleaseDate);
        Assert.Equal(DatePrecision.Exact, valor.Precision);
        Assert.True(valor.IsComingSoon);
    }

    [Fact]
    public async Task a_moved_date_is_recorded_as_a_change()
    {
        var stub = new SteamStub
        {
            WishlistJson = Wishlist((2828710, 0)),
            GetItemsJson = Items(Item(2828710, "Valor Mortis", "date_full", 1791910800, comingSoon: true)),
        };

        await using var host = new TestHost(stub.Build());
        await host.Sync.SyncAsync();

        // Steam slips the date by a month and drops to quarter precision.
        stub.GetItemsJson = Items(Item(2828710, "Valor Mortis", "date_quarter", 1806476400, comingSoon: true));
        host.Clock.Advance(TimeSpan.FromHours(7));   // past the 6h window for an imminent release

        var second = await host.Sync.SyncAsync();

        Assert.Equal(1, second.DateChanges);

        await using var db = host.NewDb();
        var change = await db.DateChanges.SingleAsync();

        Assert.Equal(new DateOnly(2026, 10, 13), change.OldDate);
        Assert.Equal(DatePrecision.Exact, change.OldPrecision);
        Assert.Equal(new DateOnly(2027, 3, 31), change.NewDate);
        Assert.Equal(DatePrecision.Quarter, change.NewPrecision);
        Assert.True(change.DaysMoved > 0, "a later date should read as a delay");
    }

    [Fact]
    public async Task an_unchanged_date_records_nothing()
    {
        var stub = new SteamStub
        {
            WishlistJson = Wishlist((2828710, 0)),
            GetItemsJson = Items(Item(2828710, "Valor Mortis", "date_full", 1791910800, comingSoon: true)),
        };

        await using var host = new TestHost(stub.Build());
        await host.Sync.SyncAsync();
        host.Clock.Advance(TimeSpan.FromHours(7));
        await host.Sync.SyncAsync();

        await using var db = host.NewDb();
        Assert.Empty(db.DateChanges);
    }

    [Fact]
    public async Task buying_a_game_removes_it_from_the_wishlist_here_too()
    {
        var stub = new SteamStub
        {
            WishlistJson = Wishlist((440, 0), (2828710, 1)),
            GetItemsJson = Items(
                Item(440, "Team Fortress 2", releaseTs: 1191999600),
                Item(2828710, "Valor Mortis", "date_full", 1791910800, comingSoon: true)),
        };

        await using var host = new TestHost(stub.Build());
        await host.Sync.SyncAsync();

        stub.WishlistJson = Wishlist((2828710, 0));     // 440 bought, so Steam drops it
        host.Clock.Advance(TimeSpan.FromHours(7));
        await host.Sync.SyncAsync();

        await using var db = host.NewDb();
        Assert.Equal(1, await db.Entries.CountAsync(e => e.Source == EntrySource.Wishlist));
        Assert.False(await db.Entries.AnyAsync(e => e.AppId == 440 && e.Source == EntrySource.Wishlist));

        // The app row survives — it may still be referenced by history.
        Assert.True(await db.Apps.AnyAsync(a => a.AppId == 440));
    }

    [Fact]
    public async Task owned_games_are_merged_as_a_second_source()
    {
        var stub = new SteamStub
        {
            WishlistJson = Wishlist((2828710, 0)),
            OwnedJson = """{"response":{"game_count":1,"games":[{"appid":1203620,"name":"Enshrouded","playtime_forever":4000}]}}""",
            GetItemsJson = Items(
                Item(2828710, "Valor Mortis", "date_full", 1791910800, comingSoon: true),
                Item(1203620, "Enshrouded", releaseTs: 1706107189, eaExitTs: 1792047600)),
        };

        await using var host = new TestHost(stub.Build(), new CalendarOptions { SteamId = Me, ApiKey = "test-key" });

        var run = await host.Sync.SyncAsync();

        Assert.Equal(1, run.WishlistItems);
        Assert.Equal(1, run.OwnedItems);

        await using var db = host.NewDb();
        Assert.Equal(1, await db.Entries.CountAsync(e => e.Source == EntrySource.Owned));
        Assert.Equal(4000, (await db.Entries.SingleAsync(e => e.Source == EntrySource.Owned)).PlaytimeMinutes);

        var enshrouded = await db.Apps.SingleAsync(a => a.AppId == 1203620);
        Assert.Equal(new DateOnly(2026, 10, 15), enshrouded.EarlyAccessExitDate);
    }

    [Fact]
    public async Task a_refused_api_key_does_not_break_the_wishlist_half()
    {
        var stub = new SteamStub
        {
            WishlistJson = Wishlist((2828710, 0)),
            OwnedStatus = HttpStatusCode.Forbidden,
            GetItemsJson = Items(Item(2828710, "Valor Mortis", "date_full", 1791910800, comingSoon: true)),
        };

        await using var host = new TestHost(stub.Build(), new CalendarOptions { SteamId = Me, ApiKey = "bad" });

        var run = await host.Sync.SyncAsync();

        Assert.Null(run.Error);
        Assert.Equal(1, run.WishlistItems);
        Assert.Equal(0, run.OwnedItems);

        await using var db = host.NewDb();
        Assert.Equal(1, await db.Entries.CountAsync());
    }

    [Fact]
    public async Task a_missing_account_is_reported_not_thrown()
    {
        await using var host = new TestHost(new SteamStub().Build(), new CalendarOptions { SteamId = 0 });

        var run = await host.Sync.SyncAsync();

        Assert.NotNull(run.Error);
        Assert.Contains("No Steam account configured", run.Error);
        Assert.NotNull(run.FinishedUtc);
    }

    [Fact]
    public async Task apps_steam_declines_to_return_are_stamped_so_they_back_off()
    {
        var stub = new SteamStub
        {
            WishlistJson = Wishlist((2378500, 0)),      // a DLC appid GetItems returns nothing for
            GetItemsJson = Items(),                      // empty store_items
        };

        await using var host = new TestHost(stub.Build());
        await host.Sync.SyncAsync();

        await using var db = host.NewDb();
        var app = await db.Apps.SingleAsync();

        Assert.NotEqual(default, app.FetchedUtc);
        Assert.Equal(host.Clock.Now.UtcDateTime, app.FetchedUtc);

        // An app the store won't describe is not an upcoming game.
        Assert.False(app.Resolved);
        Assert.False(app.IsComingSoon);
        Assert.Equal(DatePrecision.Unknown, app.Precision);
    }

    [Fact]
    public async Task a_transient_miss_does_not_wipe_a_game_we_already_know()
    {
        var stub = new SteamStub
        {
            WishlistJson = Wishlist((2828710, 0)),
            GetItemsJson = Items(Item(2828710, "Valor Mortis", "date_full", 1791910800, comingSoon: true)),
        };

        await using var host = new TestHost(stub.Build());
        await host.Sync.SyncAsync();

        // Steam hiccups and returns nothing on the next refresh.
        stub.GetItemsJson = Items();
        host.Clock.Advance(TimeSpan.FromHours(7));
        await host.Sync.SyncAsync();

        await using var db = host.NewDb();
        var app = await db.Apps.SingleAsync();

        Assert.True(app.Resolved);
        Assert.Equal("Valor Mortis", app.Name);
        Assert.Equal(new DateOnly(2026, 10, 13), app.ReleaseDate);
        Assert.Equal(DatePrecision.Exact, app.Precision);
    }

    [Fact]
    public async Task a_resolved_app_is_marked_resolved()
    {
        var stub = new SteamStub
        {
            WishlistJson = Wishlist((440, 0)),
            GetItemsJson = Items(Item(440, "Team Fortress 2", releaseTs: 1191999600)),
        };

        await using var host = new TestHost(stub.Build());
        await host.Sync.SyncAsync();

        await using var db = host.NewDb();
        Assert.True((await db.Apps.SingleAsync()).Resolved);
    }

    [Fact]
    public async Task a_fresh_app_is_not_refetched_on_the_next_sync()
    {
        var stub = new SteamStub
        {
            WishlistJson = Wishlist((440, 0)),
            GetItemsJson = Items(Item(440, "Team Fortress 2", releaseTs: 1191999600)),
        };

        await using var host = new TestHost(stub.Build());
        await host.Sync.SyncAsync();

        var itemsCallsAfterFirst = stub.GetItemsRequests.Count;
        host.Clock.Advance(TimeSpan.FromHours(1));

        var second = await host.Sync.SyncAsync();

        // A released game is cached for 30 days, so the second sync is wishlist-only.
        Assert.Equal(itemsCallsAfterFirst, stub.GetItemsRequests.Count);
        Assert.Equal(0, second.AppsRefreshed);
        Assert.Equal(1, second.StoreRequests);
    }

    [Fact]
    public async Task force_refetches_everything()
    {
        var stub = new SteamStub
        {
            WishlistJson = Wishlist((440, 0)),
            GetItemsJson = Items(Item(440, "Team Fortress 2", releaseTs: 1191999600)),
        };

        await using var host = new TestHost(stub.Build());
        await host.Sync.SyncAsync();
        var before = stub.GetItemsRequests.Count;

        await host.Sync.SyncAsync(force: true);

        Assert.Equal(before + 1, stub.GetItemsRequests.Count);
    }

    // ---- refresh tiers -----------------------------------------------------

    private static readonly DateOnly Today = new(2026, 10, 10);

    [Fact]
    public void an_imminent_release_is_checked_every_six_hours()
    {
        var app = new SteamApp { IsComingSoon = true, ReleaseDate = Today.AddDays(5), Precision = DatePrecision.Exact };
        Assert.Equal(TimeSpan.FromHours(6), SyncService.RefreshInterval(app, Today));
    }

    [Fact]
    public void a_distant_upcoming_release_is_checked_daily()
    {
        var app = new SteamApp { IsComingSoon = true, ReleaseDate = Today.AddDays(200), Precision = DatePrecision.Quarter };
        Assert.Equal(TimeSpan.FromHours(24), SyncService.RefreshInterval(app, Today));
    }

    [Fact]
    public void an_undated_game_is_checked_daily_so_a_new_date_shows_up_quickly()
    {
        var app = new SteamApp { IsComingSoon = true, ReleaseDate = null, Precision = DatePrecision.Tba };
        Assert.Equal(TimeSpan.FromHours(24), SyncService.RefreshInterval(app, Today));
    }

    [Fact]
    public void a_released_game_is_near_static()
    {
        var app = new SteamApp { IsComingSoon = false, ReleaseDate = Today.AddDays(-400), Precision = DatePrecision.Exact };
        Assert.Equal(TimeSpan.FromDays(30), SyncService.RefreshInterval(app, Today));
    }

    [Fact]
    public void a_released_game_with_a_pending_one_point_oh_is_still_watched_closely()
    {
        var app = new SteamApp
        {
            IsComingSoon = false,
            ReleaseDate = Today.AddDays(-400),
            EarlyAccessExitDate = Today.AddDays(5),
            Precision = DatePrecision.Exact,
        };

        Assert.Equal(TimeSpan.FromHours(6), SyncService.RefreshInterval(app, Today));
    }
}
