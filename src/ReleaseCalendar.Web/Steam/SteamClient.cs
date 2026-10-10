using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace ReleaseCalendar.Web.Steam;

/// <summary>
/// Thin wrapper over the three Steam endpoints this app needs. Nothing here may be
/// called from a browser: none of these send CORS headers.
/// </summary>
public sealed class SteamClient(HttpClient http, ILogger<SteamClient> log)
{
    /// <summary>GetItems accepts 100 ids per request; the ids travel in the URL.</summary>
    public const int MaxIdsPerRequest = 100;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Number of HTTP requests issued since construction — surfaced in sync logs.</summary>
    public int RequestCount { get; private set; }

    public async Task<IReadOnlyList<WishlistItem>> GetWishlistAsync(long steamId, CancellationToken ct = default)
    {
        RequestCount++;
        var res = await http.GetFromJsonAsync<WishlistResponse>(
            $"IWishlistService/GetWishlist/v1/?steamid={steamId}", Json, ct);

        // A private or empty wishlist is {"response":{}} with HTTP 200 — not an error.
        var items = res?.Response?.Items;
        if (items is null)
            log.LogWarning("Wishlist for {SteamId} came back empty — private wishlist, or genuinely empty.", steamId);

        return items ?? [];
    }

    public async Task<IReadOnlyList<StoreItem>> GetItemsAsync(
        IEnumerable<int> appIds, string language = "english", string countryCode = "US", CancellationToken ct = default)
    {
        var all = new List<StoreItem>();

        foreach (var batch in appIds.Distinct().Chunk(MaxIdsPerRequest))
        {
            var input = JsonSerializer.Serialize(new
            {
                ids = batch.Select(id => new { appid = id }),
                context = new { language, country_code = countryCode },
                data_request = new
                {
                    include_release = true,
                    include_assets = true,
                    include_basic_info = true,
                },
            });

            RequestCount++;
            var res = await http.GetFromJsonAsync<StoreItemsResponse>(
                $"IStoreBrowseService/GetItems/v1/?input_json={Uri.EscapeDataString(input)}", Json, ct);

            if (res?.Response?.StoreItems is { Count: > 0 } items)
                all.AddRange(items);
            else
                log.LogWarning("GetItems returned nothing for a batch of {Count} appids.", batch.Length);
        }

        return all;
    }

    /// <summary>
    /// Requires a Web API key and the target profile's "Game details" privacy set to Public.
    /// Returns null — rather than throwing — when Steam refuses, so one broken half of a
    /// sync doesn't take down the other.
    /// </summary>
    public async Task<IReadOnlyList<OwnedGame>?> GetOwnedGamesAsync(
        long steamId, string apiKey, CancellationToken ct = default)
    {
        var url = $"IPlayerService/GetOwnedGames/v1/?key={Uri.EscapeDataString(apiKey)}&steamid={steamId}"
                + "&include_appinfo=1&include_played_free_games=1";

        RequestCount++;
        using var res = await http.GetAsync(url, ct);

        if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            log.LogWarning("GetOwnedGames refused ({Status}). Check the API key, and that the profile's "
                         + "Game details privacy is Public.", res.StatusCode);
            return null;
        }

        res.EnsureSuccessStatusCode();
        var payload = await res.Content.ReadFromJsonAsync<OwnedGamesResponse>(Json, ct);

        // A private library also comes back as an empty response object.
        return payload?.Response?.Games;
    }
}
