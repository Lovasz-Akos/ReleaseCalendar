using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReleaseCalendar.Web;
using ReleaseCalendar.Web.Data;
using ReleaseCalendar.Web.Services;
using ReleaseCalendar.Web.Steam;

namespace ReleaseCalendar.Tests;

/// <summary>Captured Steam responses — see tests/fixtures.</summary>
internal static class Fixtures
{
    private static string Path(string name) =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    public static string Wishlist => File.ReadAllText(Path("wishlist.json"));
    public static string GetItems => File.ReadAllText(Path("getitems.json"));

    public const string EmptyResponse = """{"response":{}}""";
}

/// <summary>Records every request and replies with whatever the responder returns.</summary>
internal sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
{
    public List<Uri> Requests { get; } = [];

    public StubHandler(string json, HttpStatusCode status = HttpStatusCode.OK)
        : this(_ => Json(json, status)) { }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request.RequestUri!);
        return Task.FromResult(responder(request));
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

internal sealed class FakeClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan by) => Now = Now.Add(by);
}

internal static class SteamClientFactory
{
    public static SteamClient Create(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://api.steampowered.com/") },
            NullLogger<SteamClient>.Instance);
}

/// <summary>
/// A throwaway app: real SQLite (in-memory, kept alive by the connection), a stubbed
/// Steam transport, and a clock the test controls.
/// </summary>
internal sealed class TestHost : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    public ServiceProvider Services { get; }
    public FakeClock Clock { get; }
    public StubHandler Handler { get; }

    public TestHost(StubHandler handler, CalendarOptions? options = null, DateTimeOffset? now = null)
    {
        Handler = handler;
        Clock = new FakeClock(now ?? new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero));

        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddLogging(b => b.ClearProviders());
        services.AddSingleton(Options.Create(options ?? new CalendarOptions { SteamId = 76561190000000000 }));
        services.AddSingleton<TimeProvider>(Clock);
        services.AddDbContextFactory<CalendarDb>(o => o.UseSqlite(_connection));
        services.AddSingleton(sp => SteamClientFactory.Create(Handler));
        services.AddSingleton<SyncService>();
        services.AddScoped<CalendarQueries>();
        services.AddScoped<IcsFeed>();

        Services = services.BuildServiceProvider();

        using var db = Services.GetRequiredService<IDbContextFactory<CalendarDb>>().CreateDbContext();
        db.Database.EnsureCreated();
    }

    public SyncService Sync => Services.GetRequiredService<SyncService>();
    public CalendarQueries Queries => Services.GetRequiredService<CalendarQueries>();
    public CalendarDb NewDb() => Services.GetRequiredService<IDbContextFactory<CalendarDb>>().CreateDbContext();

    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync();
        _connection.Dispose();
    }
}

/// <summary>Routes stub responses by endpoint so one host can answer every call in a sync.</summary>
internal sealed class SteamStub
{
    public string WishlistJson { get; set; } = Fixtures.EmptyResponse;
    public string GetItemsJson { get; set; } = """{"response":{"store_items":[]}}""";
    public string OwnedJson { get; set; } = Fixtures.EmptyResponse;
    public HttpStatusCode OwnedStatus { get; set; } = HttpStatusCode.OK;

    public List<Uri> GetItemsRequests { get; } = [];

    public StubHandler Build() => new(request =>
    {
        var url = request.RequestUri!.ToString();

        if (url.Contains("GetWishlist", StringComparison.OrdinalIgnoreCase))
            return StubHandler.Json(WishlistJson);

        if (url.Contains("GetItems", StringComparison.OrdinalIgnoreCase))
        {
            GetItemsRequests.Add(request.RequestUri);
            return StubHandler.Json(GetItemsJson);
        }

        if (url.Contains("GetOwnedGames", StringComparison.OrdinalIgnoreCase))
            return StubHandler.Json(OwnedJson, OwnedStatus);

        return StubHandler.Json(Fixtures.EmptyResponse, HttpStatusCode.NotFound);
    });
}
