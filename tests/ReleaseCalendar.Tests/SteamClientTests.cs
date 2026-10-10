using System.Net;
using ReleaseCalendar.Web.Data;
using ReleaseCalendar.Web.Steam;
using Xunit;

namespace ReleaseCalendar.Tests;

public class SteamClientTests
{
    [Fact]
    public async Task wishlist_parses_the_real_response()
    {
        var client = SteamClientFactory.Create(new StubHandler(Fixtures.Wishlist));

        var items = await client.GetWishlistAsync(76561190000000000);

        // The captured fixture is a real 157-item wishlist.
        Assert.Equal(157, items.Count);
        Assert.All(items, i => Assert.True(i.AppId > 0));
        Assert.Contains(items, i => i.AppId == 612400);     // The Last Night
        Assert.All(items, i => Assert.True(i.DateAdded > 1_000_000_000));
    }

    [Fact]
    public async Task a_private_or_empty_wishlist_is_empty_not_an_error()
    {
        // Steam answers HTTP 200 with {"response":{}} — no "items" key at all.
        var client = SteamClientFactory.Create(new StubHandler(Fixtures.EmptyResponse));

        var items = await client.GetWishlistAsync(76561190000000001);

        Assert.Empty(items);
    }

    [Fact]
    public async Task get_items_parses_every_precision_variant()
    {
        var client = SteamClientFactory.Create(new StubHandler(Fixtures.GetItems));

        var items = await client.GetItemsAsync([440, 2828710, 3982150, 1846700, 612400, 1203620, 2307350, 1485920, 1086940]);

        Assert.Equal(9, items.Count);

        var byId = items.ToDictionary(i => i.ResolvedAppId);

        Assert.Equal("Team Fortress 2", byId[440].Name);
        Assert.Equal(DatePrecision.Exact, ReleaseMapper.Map(byId[440].Release).Precision);
        Assert.Equal(DatePrecision.Exact, ReleaseMapper.Map(byId[2828710].Release).Precision);
        Assert.Equal(DatePrecision.Quarter, ReleaseMapper.Map(byId[3982150].Release).Precision);
        Assert.Equal(DatePrecision.Year, ReleaseMapper.Map(byId[1846700].Release).Precision);
        Assert.Equal(DatePrecision.Tba, ReleaseMapper.Map(byId[612400].Release).Precision);
        Assert.Equal(DatePrecision.ComingSoon, ReleaseMapper.Map(byId[1485920].Release).Precision);

        // Enshrouded: out, in Early Access, with a dated 1.0.
        Assert.Equal(new DateOnly(2026, 10, 15), ReleaseMapper.Map(byId[1203620].Release).EarlyAccessExitDate);
    }

    [Fact]
    public async Task get_items_chunks_at_a_hundred_ids_per_request()
    {
        var handler = new StubHandler("""{"response":{"store_items":[]}}""");
        var client = SteamClientFactory.Create(handler);

        await client.GetItemsAsync(Enumerable.Range(1, 157));

        // 157 appids is exactly the shape of the real wishlist: two requests, not 157.
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, u => Assert.Contains("IStoreBrowseService/GetItems/v1", u.ToString()));
    }

    [Fact]
    public async Task get_items_deduplicates_before_chunking()
    {
        var handler = new StubHandler("""{"response":{"store_items":[]}}""");
        var client = SteamClientFactory.Create(handler);

        await client.GetItemsAsync(Enumerable.Repeat(440, 250));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task get_items_url_stays_well_under_the_eight_kilobyte_limit()
    {
        var handler = new StubHandler("""{"response":{"store_items":[]}}""");
        var client = SteamClientFactory.Create(handler);

        // Worst case: 100 seven-digit appids in one request.
        await client.GetItemsAsync(Enumerable.Range(3_000_000, 100));

        Assert.True(handler.Requests[0].ToString().Length < 8192,
            $"URL was {handler.Requests[0].ToString().Length} chars; Steam refuses past ~8 KB.");
    }

    [Fact]
    public async Task owned_games_returns_null_when_steam_refuses()
    {
        var handler = new StubHandler("", HttpStatusCode.Forbidden);
        var client = SteamClientFactory.Create(handler);

        // A bad key or a private library must not take down the wishlist half of a sync.
        Assert.Null(await client.GetOwnedGamesAsync(76561190000000000, "bad-key"));
    }

    [Fact]
    public async Task owned_games_parses_a_normal_response()
    {
        const string json = """
        {"response":{"game_count":2,"games":[
          {"appid":440,"name":"Team Fortress 2","playtime_forever":120},
          {"appid":1203620,"name":"Enshrouded","playtime_forever":4000}]}}
        """;

        var client = SteamClientFactory.Create(new StubHandler(json));

        var games = await client.GetOwnedGamesAsync(76561190000000000, "key");

        Assert.NotNull(games);
        Assert.Equal(2, games.Count);
        Assert.Equal(4000, games.Single(g => g.AppId == 1203620).PlaytimeForever);
    }

    [Fact]
    public async Task the_api_key_is_url_encoded_into_the_request()
    {
        var handler = new StubHandler(Fixtures.EmptyResponse);
        var client = SteamClientFactory.Create(handler);

        await client.GetOwnedGamesAsync(123, "abc/def+ghi");

        var url = handler.Requests[0].ToString();
        Assert.DoesNotContain("abc/def+ghi", url);
        Assert.Contains("include_appinfo=1", url);
    }
}
