using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReleaseCalendar.Web;
using ReleaseCalendar.Web.Components;
using ReleaseCalendar.Web.Data;
using ReleaseCalendar.Web.Services;
using ReleaseCalendar.Web.Steam;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<CalendarOptions>(builder.Configuration.GetSection(CalendarOptions.SectionName));
builder.Services.AddSingleton(TimeProvider.System);

// Keep the data file outside the deployment directory when a path is configured, so an
// upgrade that replaces the binaries can't take the cache and delay history with it.
var configuredDbPath = builder.Configuration[$"{CalendarOptions.SectionName}:DatabasePath"];

var dbPath = string.IsNullOrWhiteSpace(configuredDbPath)
    ? Path.Combine(builder.Environment.ContentRootPath, "calendar.db")
    : Path.GetFullPath(configuredDbPath);

Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
builder.Services.AddDbContextFactory<CalendarDb>(o => o.UseSqlite($"Data Source={dbPath}"));

// The only component that talks to Valve. Nothing here is reachable from a browser:
// none of these endpoints send CORS headers.
builder.Services.AddHttpClient<SteamClient>(c =>
{
    c.BaseAddress = new Uri("https://api.steampowered.com/");
    c.Timeout = TimeSpan.FromSeconds(30);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("ReleaseCalendar/1.0 (+personal use)");
});

builder.Services.AddSingleton<SteamAccount>();
builder.Services.AddHttpClient<SteamIdResolver>();
builder.Services.AddSingleton<SyncService>();
builder.Services.AddScoped<CalendarQueries>();
builder.Services.AddScoped<IcsFeed>();
builder.Services.AddHostedService<SyncWorker>();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

// SQLite file + schema. Safe to run on every start.
using (var scope = app.Services.CreateScope())
{
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<CalendarDb>>();
    await using var db = await factory.CreateDbContextAsync();
    await db.Database.MigrateAsync();
}

// Turn a vanity name or profile URL into a steamID64 once, so everything downstream
// stays synchronous. A failure here is logged, not fatal: the UI reports it and the
// background sync retries.
using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<SteamIdResolver>().EnsureResolvedAsync();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();
app.MapStaticAssets();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Subscribe to this in Google Calendar, Outlook or iOS and releases keep arriving.
app.MapGet("/feed/{steamId:long}.ics", async (
    long steamId, IcsFeed feed, SteamAccount account, CancellationToken ct) =>
{
    if (steamId != account.SteamId) return Results.NotFound();
    return Results.Text(await feed.BuildAsync(ct), "text/calendar; charset=utf-8");
});

app.MapGet("/feed.ics", async (IcsFeed feed, CancellationToken ct) =>
    Results.Text(await feed.BuildAsync(ct), "text/calendar; charset=utf-8"));

app.Run();
