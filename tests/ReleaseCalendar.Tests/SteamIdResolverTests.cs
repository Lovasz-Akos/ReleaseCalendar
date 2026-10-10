using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReleaseCalendar.Web;
using ReleaseCalendar.Web.Steam;
using Xunit;

namespace ReleaseCalendar.Tests;

public class SteamIdResolverTests
{
    private const long Id = 76561190000000000;

    // What steamcommunity.com/id/<name>/?xml=1 actually returns.
    private static string ProfileXml(long id) =>
        $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><profile><steamID64>{id}</steamID64><steamID><![CDATA[someone]]></steamID></profile>""";

    private const string NotFoundXml =
        """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><response><error><![CDATA[The specified profile could not be found.]]></error></response>""";

    private static (SteamIdResolver Resolver, SteamAccount Account, StubHandler Handler) Build(
        string body, CalendarOptions? options = null)
    {
        var handler = new StubHandler(body);
        var account = new SteamAccount();
        var resolver = new SteamIdResolver(
            new HttpClient(handler),
            account,
            Options.Create(options ?? new CalendarOptions()),
            NullLogger<SteamIdResolver>.Instance);

        return (resolver, account, handler);
    }

    // ---- input shapes ------------------------------------------------------

    [Theory]
    [InlineData("naara_", "naara_")]
    [InlineData("  naara_  ", "naara_")]
    [InlineData("https://steamcommunity.com/id/naara_", "naara_")]
    [InlineData("https://steamcommunity.com/id/naara_/", "naara_")]
    [InlineData("steamcommunity.com/id/naara_/#games", "naara_")]
    [InlineData("https://steamcommunity.com/profiles/76561190000000000", "76561190000000000")]
    [InlineData("https://steamcommunity.com/profiles/76561190000000000/", "76561190000000000")]
    [InlineData("76561190000000000", "76561190000000000")]
    public void normalize_accepts_whatever_is_in_the_address_bar(string input, string expected)
    {
        Assert.Equal(expected, SteamIdResolver.Normalize(input));
    }

    [Theory]
    [InlineData("76561190000000000", true)]
    [InlineData("76561198214766635", true)]
    [InlineData("12345", false)]          // a vanity name may be all digits
    [InlineData("7656119", false)]        // too short
    [InlineData("765611900000000000", false)]  // too long
    [InlineData("12345678901234567", false)]   // 17 digits but not a steamID64
    [InlineData("naara_", false)]
    public void steamid64_is_recognised_by_shape_not_just_by_being_numeric(string value, bool expected)
    {
        Assert.Equal(expected, SteamIdResolver.LooksLikeSteamId64(value));
    }

    // ---- resolution --------------------------------------------------------

    [Fact]
    public async Task a_numeric_id_resolves_without_any_network_call()
    {
        var (resolver, _, handler) = Build(NotFoundXml);

        Assert.Equal(Id, await resolver.ResolveAsync("76561190000000000"));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task a_vanity_name_is_looked_up_keylessly()
    {
        var (resolver, _, handler) = Build(ProfileXml(Id));

        Assert.Equal(Id, await resolver.ResolveAsync("naara_"));

        Assert.Single(handler.Requests);
        Assert.Contains("steamcommunity.com/id/naara_", handler.Requests[0].ToString());
        Assert.Contains("xml=1", handler.Requests[0].ToString());

        // No key involved.
        Assert.DoesNotContain("key=", handler.Requests[0].ToString());
    }

    [Fact]
    public async Task a_profile_url_resolves_the_same_as_the_bare_name()
    {
        var (resolver, _, _) = Build(ProfileXml(Id));

        Assert.Equal(Id, await resolver.ResolveAsync("https://steamcommunity.com/id/naara_/"));
    }

    [Fact]
    public async Task an_unknown_vanity_throws_with_steams_own_reason()
    {
        // The real failure is HTTP 200 with an <error> element, so a status check would
        // happily accept this as success.
        var (resolver, _, _) = Build(NotFoundXml);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => resolver.ResolveAsync("definitely-not-real"));

        Assert.Contains("could not be found", ex.Message);
        Assert.Contains("definitely-not-real", ex.Message);
    }

    [Fact]
    public async Task a_vanity_name_is_url_encoded()
    {
        var (resolver, _, handler) = Build(ProfileXml(Id));

        await resolver.ResolveAsync("name with spaces");

        // AbsoluteUri, not ToString(): ToString() decodes percent-escapes for display and
        // would show the raw spaces even though the request was encoded correctly.
        Assert.Contains("name%20with%20spaces", handler.Requests[0].AbsoluteUri);
    }

    // ---- EnsureResolvedAsync ----------------------------------------------

    [Fact]
    public async Task ensure_prefers_an_explicit_numeric_id_over_a_lookup()
    {
        var (resolver, account, handler) = Build(ProfileXml(999),
            new CalendarOptions { SteamId = Id, Account = "naara_" });

        Assert.True(await resolver.EnsureResolvedAsync());
        Assert.Equal(Id, account.SteamId);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ensure_records_where_the_id_came_from()
    {
        var (resolver, account, _) = Build(ProfileXml(Id), new CalendarOptions { Account = "naara_" });

        Assert.True(await resolver.EnsureResolvedAsync());
        Assert.Equal(Id, account.SteamId);
        Assert.Equal("vanity naara_", account.Source);
    }

    [Fact]
    public async Task ensure_is_a_no_op_once_resolved()
    {
        var (resolver, account, handler) = Build(ProfileXml(Id), new CalendarOptions { Account = "naara_" });

        await resolver.EnsureResolvedAsync();
        await resolver.EnsureResolvedAsync();

        Assert.Single(handler.Requests);
        Assert.Equal(Id, account.SteamId);
    }

    [Fact]
    public async Task ensure_returns_false_rather_than_throwing_when_steam_is_unreachable()
    {
        // A failure at startup must not stop the app booting — the UI reports it and the
        // background sync retries.
        var (resolver, account, _) = Build(NotFoundXml, new CalendarOptions { Account = "nope" });

        Assert.False(await resolver.EnsureResolvedAsync());
        Assert.False(account.IsConfigured);
    }

    [Fact]
    public async Task ensure_returns_false_when_nothing_is_configured()
    {
        var (resolver, account, handler) = Build(ProfileXml(Id), new CalendarOptions());

        Assert.False(await resolver.EnsureResolvedAsync());
        Assert.False(account.IsConfigured);
        Assert.Empty(handler.Requests);
    }
}
