using Microsoft.EntityFrameworkCore;
using ReleaseCalendar.Web;
using ReleaseCalendar.Web.Data;
using ReleaseCalendar.Web.Services;
using ReleaseCalendar.Web.Steam;
using Xunit;

namespace ReleaseCalendar.Tests;

public class PriceMapperTests
{
    [Fact]
    public void prices_arrive_as_strings_and_are_parsed()
    {
        // Steam really does send "1999", not 1999.
        var mapped = PriceMapper.Map(new BestPurchaseOption
        {
            FinalPriceInCents = "1999",
            OriginalPriceInCents = "3999",
            DiscountPct = 50,
            FormattedFinalPrice = "$19.99",
            FormattedOriginalPrice = "$39.99",
        });

        Assert.Equal(1999, mapped.FinalCents);
        Assert.Equal(3999, mapped.OriginalCents);
        Assert.Equal(50, mapped.DiscountPct);
        Assert.True(mapped.OnSale);
    }

    [Fact]
    public void a_missing_purchase_option_is_no_price_not_a_price_of_zero()
    {
        // Free games and unreleased apps both omit the block; neither costs 0.
        var mapped = PriceMapper.Map(null);

        Assert.Null(mapped.FinalCents);
        Assert.False(mapped.HasPrice);
        Assert.False(mapped.OnSale);
    }

    [Fact]
    public void an_undiscounted_price_reports_the_original_as_equal()
    {
        var mapped = PriceMapper.Map(new BestPurchaseOption
        {
            FinalPriceInCents = "2999",
            DiscountPct = 0,
            FormattedFinalPrice = "$29.99",
        });

        Assert.Equal(2999, mapped.FinalCents);
        Assert.Equal(2999, mapped.OriginalCents);
        Assert.False(mapped.OnSale);
    }

    [Fact]
    public void the_soonest_discount_end_date_is_taken()
    {
        var mapped = PriceMapper.Map(new BestPurchaseOption
        {
            FinalPriceInCents = "1000",
            DiscountPct = 20,
            ActiveDiscounts =
            [
                new ActiveDiscount { DiscountEndDate = 1792774837 },
                new ActiveDiscount { DiscountEndDate = 1792083600 },
            ],
        });

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1792083600).UtcDateTime, mapped.DiscountEndsUtc);
    }

    [Fact]
    public void no_active_discount_means_no_end_date()
    {
        var mapped = PriceMapper.Map(new BestPurchaseOption { FinalPriceInCents = "1000" });
        Assert.Null(mapped.DiscountEndsUtc);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a number")]
    [InlineData("-5")]
    public void unparseable_cents_are_null_rather_than_zero(string? value)
    {
        Assert.Null(PriceMapper.ParseCents(value));
    }

    [Theory]
    // The currency decoration is lifted from a price Steam already formatted, so
    // history points render in the right currency without hardcoding a symbol.
    [InlineData(1999, "$19.99", 1999, "$19.99")]
    [InlineData(999, "$19.99", 1999, "$9.99")]
    [InlineData(500, "19,99€", 1999, "5,00€")]
    [InlineData(1000, "R$ 19.99", 1999, "R$ 10.00")]
    public void formatting_reuses_steams_own_currency_decoration(
        int cents, string example, int exampleCents, string expected)
    {
        Assert.Equal(expected, PriceMapper.Format(cents, example, exampleCents));
    }

    [Fact]
    public void formatting_falls_back_to_a_bare_amount_when_there_is_no_example()
    {
        Assert.Equal("19.99", PriceMapper.Format(1999, null, null));
    }
}

public class SalesTests
{
    private const long Me = 76561190000000000;

    private static object Item(int appId, string name, int final, int original, int discountPct, long? endsAt = null)
    {
        var option = new Dictionary<string, object>
        {
            ["final_price_in_cents"] = final.ToString(),
            ["original_price_in_cents"] = original.ToString(),
            ["discount_pct"] = discountPct,
            ["formatted_final_price"] = $"${final / 100.0:0.00}",
        };

        if (endsAt is { } e)
            option["active_discounts"] = new[] { new { discount_end_date = e } };

        return new
        {
            appid = appId,
            success = 1,
            visible = true,
            name,
            release = new { steam_release_date = 1191999600 },
            best_purchase_option = option,
        };
    }

    private static string Wishlist(params int[] appIds) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            response = new { items = appIds.Select(a => new { appid = a, priority = 0, date_added = 1727629363 }) },
        });

    private static string Items(params object[] items) =>
        System.Text.Json.JsonSerializer.Serialize(new { response = new { store_items = items } });

    [Fact]
    public async Task a_price_is_recorded_on_the_first_sighting()
    {
        var stub = new SteamStub
        {
            WishlistJson = Wishlist(440),
            GetItemsJson = Items(Item(440, "Team Fortress 2", 1999, 1999, 0)),
        };

        await using var host = new TestHost(stub.Build());
        var run = await host.Sync.SyncAsync();

        Assert.Equal(1, run.PriceChanges);
        Assert.Equal(0, run.NewSales);

        await using var db = host.NewDb();
        var app = await db.Apps.SingleAsync();
        Assert.Equal(1999, app.PriceCents);
        Assert.Equal(0, app.DiscountPct);
        Assert.Single(db.PricePoints);
    }

    [Fact]
    public async Task an_unchanged_price_adds_no_history_point()
    {
        var stub = new SteamStub
        {
            WishlistJson = Wishlist(440),
            GetItemsJson = Items(Item(440, "Team Fortress 2", 1999, 1999, 0)),
        };

        await using var host = new TestHost(stub.Build());
        await host.Sync.SyncAsync();
        await host.Sync.SyncAsync(force: true);

        await using var db = host.NewDb();

        // Sparse by design: one row per change, not one per sync.
        Assert.Single(db.PricePoints);
    }

    [Fact]
    public async Task a_discount_appearing_counts_as_a_new_sale()
    {
        var stub = new SteamStub
        {
            WishlistJson = Wishlist(440),
            GetItemsJson = Items(Item(440, "Team Fortress 2", 1999, 1999, 0)),
        };

        await using var host = new TestHost(stub.Build());
        await host.Sync.SyncAsync();

        stub.GetItemsJson = Items(Item(440, "Team Fortress 2", 999, 1999, 50));
        host.Clock.Advance(TimeSpan.FromHours(7));
        var second = await host.Sync.SyncAsync(force: true);

        Assert.Equal(1, second.NewSales);
        Assert.Equal(1, second.PriceChanges);

        await using var db = host.NewDb();
        var app = await db.Apps.SingleAsync();
        Assert.Equal(50, app.DiscountPct);
        Assert.Equal(host.Clock.Now.UtcDateTime, app.SaleSeenUtc);
        Assert.Equal(2, await db.PricePoints.CountAsync());
    }

    [Fact]
    public async Task a_sale_already_running_at_the_first_sync_is_not_news()
    {
        var stub = new SteamStub
        {
            WishlistJson = Wishlist(440),
            GetItemsJson = Items(Item(440, "Team Fortress 2", 999, 1999, 50)),
        };

        await using var host = new TestHost(stub.Build());
        var first = await host.Sync.SyncAsync();

        // Without this rule a cold start flags every discount on the wishlist.
        Assert.Equal(0, first.NewSales);

        await using var db = host.NewDb();
        var app = await db.Apps.SingleAsync();
        Assert.Equal(50, app.DiscountPct);
        Assert.Null(app.SaleSeenUtc);
    }

    [Fact]
    public async Task a_discount_that_merely_continues_is_not_a_new_sale()
    {
        var stub = new SteamStub
        {
            WishlistJson = Wishlist(440),
            GetItemsJson = Items(Item(440, "Team Fortress 2", 1999, 1999, 0)),
        };

        await using var host = new TestHost(stub.Build());
        await host.Sync.SyncAsync();

        stub.GetItemsJson = Items(Item(440, "Team Fortress 2", 999, 1999, 50));
        host.Clock.Advance(TimeSpan.FromHours(7));
        Assert.Equal(1, (await host.Sync.SyncAsync(force: true)).NewSales);

        // Same discount, a sync later.
        host.Clock.Advance(TimeSpan.FromHours(7));
        Assert.Equal(0, (await host.Sync.SyncAsync(force: true)).NewSales);
    }

    [Fact]
    public async Task a_sale_ending_clears_the_marker_so_the_next_one_counts_again()
    {
        var stub = new SteamStub
        {
            WishlistJson = Wishlist(440),
            GetItemsJson = Items(Item(440, "Team Fortress 2", 999, 1999, 50)),
        };

        await using var host = new TestHost(stub.Build());
        await host.Sync.SyncAsync();

        // Sale ends.
        stub.GetItemsJson = Items(Item(440, "Team Fortress 2", 1999, 1999, 0));
        host.Clock.Advance(TimeSpan.FromHours(7));
        await host.Sync.SyncAsync(force: true);

        await using (var db = host.NewDb())
            Assert.Null((await db.Apps.SingleAsync()).SaleSeenUtc);

        // And comes back.
        stub.GetItemsJson = Items(Item(440, "Team Fortress 2", 499, 1999, 75));
        host.Clock.Advance(TimeSpan.FromHours(7));
        var third = await host.Sync.SyncAsync(force: true);

        Assert.Equal(1, third.NewSales);
    }

    [Fact]
    public async Task the_sales_view_separates_new_from_ongoing()
    {
        var stub = new SteamStub
        {
            WishlistJson = Wishlist(1, 2),
            GetItemsJson = Items(
                Item(1, "Already Cheap", 500, 1000, 50),
                Item(2, "Full Price", 2000, 2000, 0)),
        };

        await using var host = new TestHost(stub.Build());
        await host.Sync.SyncAsync();

        // Second game goes on sale; first one's discount continues.
        stub.GetItemsJson = Items(
            Item(1, "Already Cheap", 500, 1000, 50),
            Item(2, "Full Price", 1000, 2000, 50));
        host.Clock.Advance(TimeSpan.FromHours(7));
        await host.Sync.SyncAsync(force: true);

        var view = await host.Queries.SalesAsync();

        Assert.Equal(2, view.Count);
        Assert.Equal("Full Price", Assert.Single(view.New).Name);
        Assert.Equal("Already Cheap", Assert.Single(view.Ongoing).Name);
        Assert.Equal(1500, view.TotalSavingCents);   // 500 + 1000
    }

    [Fact]
    public async Task an_owned_game_on_sale_is_not_listed()
    {
        var stub = new SteamStub
        {
            WishlistJson = Wishlist(1),
            GetItemsJson = Items(Item(1, "Wishlisted", 500, 1000, 50)),
        };

        await using var host = new TestHost(stub.Build());
        await host.Sync.SyncAsync();

        // A sale only matters for something you haven't bought.
        await using (var db = host.NewDb())
        {
            db.Entries.Remove(await db.Entries.SingleAsync());
            await db.SaveChangesAsync();
        }

        var view = await host.Queries.SalesAsync();
        Assert.Equal(0, view.Count);
    }

    [Fact]
    public async Task history_accumulates_in_order_and_reports_the_lowest_seen()
    {
        var stub = new SteamStub
        {
            WishlistJson = Wishlist(1),
            GetItemsJson = Items(Item(1, "Slider", 2000, 2000, 0)),
        };

        await using var host = new TestHost(stub.Build());
        await host.Sync.SyncAsync();

        foreach (var (final, pct) in new[] { (1000, 50), (600, 70), (1400, 30) })
        {
            stub.GetItemsJson = Items(Item(1, "Slider", final, 2000, pct));
            host.Clock.Advance(TimeSpan.FromHours(7));
            await host.Sync.SyncAsync(force: true);
        }

        var row = (await host.Queries.SalesAsync()).Ongoing.Concat((await host.Queries.SalesAsync()).New).Single();

        Assert.Equal(4, row.Points.Count);
        Assert.Equal([2000, 1000, 600, 1400], row.Points.Select(p => p.FinalCents).ToArray());
        Assert.Equal(600, row.LowestEverCents);
        Assert.False(row.AtLowestSeen);   // currently 1400
    }

    [Fact]
    public async Task a_free_game_records_no_price()
    {
        var stub = new SteamStub
        {
            WishlistJson = Wishlist(440),
            GetItemsJson = Items(new
            {
                appid = 440,
                success = 1,
                visible = true,
                name = "Team Fortress 2",
                is_free = true,
                release = new { steam_release_date = 1191999600 },
            }),
        };

        await using var host = new TestHost(stub.Build());
        var run = await host.Sync.SyncAsync();

        Assert.Equal(0, run.PriceChanges);

        await using var db = host.NewDb();
        Assert.Null((await db.Apps.SingleAsync()).PriceCents);
        Assert.Empty(db.PricePoints);
    }
}

public class ReviewTests
{
    [Fact]
    public void a_real_review_summary_is_mapped()
    {
        var mapped = ReviewMapper.Map(new StoreReviews
        {
            SummaryFiltered = new ReviewSummary
            {
                ReviewCount = 215621,
                PercentPositive = 97,
                ReviewScore = 9,
                ReviewScoreLabel = "Overwhelmingly Positive",
            },
        });

        Assert.True(mapped.HasReviews);
        Assert.Equal(97, mapped.Percent);
        Assert.Equal(215621, mapped.Count);
        Assert.Equal("Overwhelmingly Positive", mapped.Label);
    }

    [Fact]
    public void zero_reviews_is_no_score_rather_than_a_score_of_zero()
    {
        // This is exactly what an unreleased game returns: a complete, valid-looking
        // block reading 0% of 0. Rendering that as "0%" would libel the game.
        var mapped = ReviewMapper.Map(new StoreReviews
        {
            SummaryFiltered = new ReviewSummary
            {
                ReviewCount = 0,
                PercentPositive = 0,
                ReviewScoreLabel = "No user reviews",
            },
        });

        Assert.False(mapped.HasReviews);
        Assert.Null(mapped.Percent);
        Assert.Null(mapped.Count);
    }

    [Fact]
    public void a_missing_reviews_block_is_no_score()
    {
        // Delisted apps omit it entirely.
        Assert.Null(ReviewMapper.Map(null).Percent);
        Assert.Null(ReviewMapper.Map(new StoreReviews()).Percent);
    }

    [Fact]
    public void the_all_language_summary_is_preferred_over_the_localised_one()
    {
        var mapped = ReviewMapper.Map(new StoreReviews
        {
            SummaryFiltered = new ReviewSummary { ReviewCount = 1000, PercentPositive = 87 },
            SummaryLanguageSpecific = new ReviewSummary { ReviewCount = 10, PercentPositive = 40 },
        });

        Assert.Equal(87, mapped.Percent);
        Assert.Equal(1000, mapped.Count);
    }

    [Theory]
    [InlineData(97, false)]
    [InlineData(70, false)]
    [InlineData(69, true)]
    [InlineData(31, true)]
    public void only_a_weak_score_gets_flagged(int percent, bool expected)
    {
        Assert.Equal(expected, new ReviewView(percent, 100, null).IsWeak);
    }

    [Fact]
    public void the_view_is_null_without_reviews_so_nothing_renders()
    {
        Assert.Null(ReviewView.From(null, null, null));
        Assert.Null(ReviewView.From(0, 0, "No user reviews"));
        Assert.NotNull(ReviewView.From(97, 215621, "Overwhelmingly Positive"));
    }

    [Fact]
    public void the_tooltip_carries_the_sample_size_and_steams_wording()
    {
        var view = new ReviewView(97, 215621, "Overwhelmingly Positive");

        Assert.Contains("97% positive", view.Tooltip);
        Assert.Contains("215,621", view.Tooltip);
        Assert.Contains("Overwhelmingly Positive", view.Tooltip);
    }
}

public class ReviewSampleTests
{
    [Theory]
    [InlineData(215621, false)]
    [InlineData(50, false)]
    [InlineData(49, true)]
    [InlineData(20, true)]   // the real case: "100% of 20"
    public void a_thin_sample_is_flagged(int count, bool expected)
    {
        Assert.Equal(expected, new ReviewView(100, count, null).IsThinSample);
    }

    [Fact]
    public void weak_and_thin_are_independent_signals()
    {
        var thinButGreat = new ReviewView(100, 20, "Positive");
        Assert.True(thinButGreat.IsThinSample);
        Assert.False(thinButGreat.IsWeak);

        var solidButBad = new ReviewView(47, 4000, "Mixed");
        Assert.False(solidButBad.IsThinSample);
        Assert.True(solidButBad.IsWeak);
    }
}
