namespace ReleaseCalendar.Web;

/// <summary>
/// The account this instance tracks, as a resolved steamID64.
///
/// Configuration accepts a vanity name or a profile URL, which needs a network call to
/// turn into an id. Resolving once into this singleton keeps everything downstream
/// synchronous — queries, the feed route and the UI all just read <see cref="SteamId"/>.
/// </summary>
public sealed class SteamAccount
{
    public long SteamId { get; set; }

    /// <summary>Where the id came from, for the Settings page: "vanity naara_", say.</summary>
    public string Source { get; set; } = "not configured";

    public bool IsConfigured => SteamId != 0;
}
