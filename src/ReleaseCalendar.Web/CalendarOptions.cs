namespace ReleaseCalendar.Web;

public sealed class CalendarOptions
{
    public const string SectionName = "Calendar";

    /// <summary>Your steamID64. Resolve a vanity URL with https://steamcommunity.com/id/&lt;name&gt;/?xml=1</summary>
    public long SteamId { get; set; }

    /// <summary>
    /// Steam Web API key — only needed for the owned-games half. Get one at
    /// https://steamcommunity.com/dev/apikey. Keep it out of appsettings.json;
    /// use `dotnet user-secrets set "Calendar:ApiKey" "..."`.
    /// </summary>
    public string? ApiKey { get; set; }

    public int SyncIntervalHours { get; set; } = 6;

    /// <summary>Delay before the first sync after startup, so the UI paints immediately.</summary>
    public int StartupSyncDelaySeconds { get; set; } = 3;

    public string Language { get; set; } = "english";
    public string CountryCode { get; set; } = "US";

    /// <summary>How long a release stays in the "out now, still wishlisted" tier.</summary>
    public int RecentlyReleasedDays { get; set; } = 180;

    public bool HasOwnedGamesAccess => !string.IsNullOrWhiteSpace(ApiKey) && SteamId != 0;
}
