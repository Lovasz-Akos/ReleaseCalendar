using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace ReleaseCalendar.Web.Steam;

/// <summary>
/// Turns whatever the user put in configuration — a steamID64, a vanity name, or a pasted
/// profile URL — into a steamID64.
/// </summary>
public sealed partial class SteamIdResolver(
    HttpClient http,
    SteamAccount account,
    IOptions<CalendarOptions> options,
    ILogger<SteamIdResolver> log)
{
    // Individual accounts are 17 digits beginning 7656119. Checking the shape matters
    // because a vanity name is allowed to be all digits — "12345" is a valid vanity.
    [GeneratedRegex(@"^7656119\d{10}$")]
    private static partial Regex SteamId64Shape();

    [GeneratedRegex(@"steamcommunity\.com/(?:id|profiles)/([^/?#]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ProfileUrl();

    [GeneratedRegex(@"<steamID64>(\d+)</steamID64>")]
    private static partial Regex Id64InXml();

    [GeneratedRegex(@"<error>(?:<!\[CDATA\[)?(.*?)(?:\]\]>)?</error>", RegexOptions.Singleline)]
    private static partial Regex ErrorInXml();

    /// <summary>
    /// Reduce an input to a bare id or vanity name. Accepts
    /// <c>https://steamcommunity.com/id/name/</c>, <c>.../profiles/7656…</c>, or either
    /// bare — people paste whichever is in their address bar.
    /// </summary>
    public static string Normalize(string account)
    {
        var value = account.Trim();
        if (value.Length == 0) return value;

        if (ProfileUrl().Match(value) is { Success: true } url)
            value = url.Groups[1].Value;

        return value.Trim('/').Trim();
    }

    public static bool LooksLikeSteamId64(string value) => SteamId64Shape().IsMatch(value);

    /// <summary>
    /// Populate <see cref="SteamAccount"/> if it isn't already. Returns false rather than
    /// throwing when the account can't be resolved, so a Steam outage at startup leaves the
    /// app running (and saying so) instead of failing to boot.
    /// </summary>
    public async Task<bool> EnsureResolvedAsync(CancellationToken ct = default)
    {
        if (account.IsConfigured) return true;

        var opts = options.Value;

        // An explicit numeric setting wins and needs no network call.
        if (opts.SteamId != 0)
        {
            account.SteamId = opts.SteamId;
            account.Source = "configured steamID64";
            return true;
        }

        if (string.IsNullOrWhiteSpace(opts.Account))
        {
            log.LogWarning("Neither Calendar:Account nor Calendar:SteamId is set.");
            return false;
        }

        try
        {
            var normalized = Normalize(opts.Account);
            account.SteamId = await ResolveAsync(opts.Account, ct);
            account.Source = LooksLikeSteamId64(normalized)
                ? "configured steamID64"
                : $"vanity {normalized}";

            log.LogInformation("Resolved {Account} to steamID64 {SteamId}.", opts.Account, account.SteamId);
            return true;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Could not resolve Calendar:Account '{Account}'.", opts.Account);
            return false;
        }
    }

    public async Task<long> ResolveAsync(string input, CancellationToken ct = default)
    {
        var value = Normalize(input);

        if (value.Length == 0)
            throw new InvalidOperationException("No account configured.");

        if (LooksLikeSteamId64(value))
            return long.Parse(value, CultureInfo.InvariantCulture);

        // Keyless route: the community profile page serves its own XML.
        var xml = await http.GetStringAsync(
            $"https://steamcommunity.com/id/{Uri.EscapeDataString(value)}/?xml=1", ct);

        if (Id64InXml().Match(xml) is { Success: true } hit)
            return long.Parse(hit.Groups[1].Value, CultureInfo.InvariantCulture);

        // An unknown vanity answers HTTP 200 with an <error> element, so the status code
        // tells us nothing — the body is the only signal.
        var reason = ErrorInXml().Match(xml) is { Success: true } err
            ? err.Groups[1].Value.Trim()
            : "no steamID64 in the profile response";

        // The documented endpoint needs a key, so it's only a fallback.
        if (options.Value.ApiKey is { Length: > 0 } key)
        {
            log.LogWarning("Vanity lookup for '{Vanity}' failed ({Reason}); trying ResolveVanityURL.", value, reason);

            if (await ResolveViaApiAsync(value, key, ct) is { } id)
                return id;
        }

        throw new InvalidOperationException(
            $"Could not resolve '{value}' to a steamID64: {reason}. "
          + "Set Calendar:SteamId to the numeric id instead, or check the vanity name "
          + "at https://steamcommunity.com/my/edit.");
    }

    private async Task<long?> ResolveViaApiAsync(string vanity, string apiKey, CancellationToken ct)
    {
        try
        {
            var url = "https://api.steampowered.com/ISteamUser/ResolveVanityURL/v1/"
                    + $"?key={Uri.EscapeDataString(apiKey)}&vanityurl={Uri.EscapeDataString(vanity)}";

            using var doc = JsonDocument.Parse(await http.GetStringAsync(url, ct));

            if (!doc.RootElement.TryGetProperty("response", out var response)) return null;
            if (!response.TryGetProperty("success", out var success) || success.GetInt32() != 1) return null;
            if (!response.TryGetProperty("steamid", out var steamId)) return null;

            return long.Parse(steamId.GetString()!, CultureInfo.InvariantCulture);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "ResolveVanityURL fallback failed for '{Vanity}'.", vanity);
            return null;
        }
    }
}
