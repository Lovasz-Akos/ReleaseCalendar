using System.Text.Json.Serialization;

namespace ReleaseCalendar.Web.Steam;

// ---------------------------------------------------------------------------
// IWishlistService/GetWishlist/v1 — keyless. Returns {"response":{}} with no
// "items" key when the wishlist is private or empty, so Items is nullable.
// ---------------------------------------------------------------------------

public sealed class WishlistResponse
{
    [JsonPropertyName("response")] public WishlistPayload? Response { get; set; }
}

public sealed class WishlistPayload
{
    [JsonPropertyName("items")] public List<WishlistItem>? Items { get; set; }
}

public sealed class WishlistItem
{
    [JsonPropertyName("appid")] public int AppId { get; set; }
    [JsonPropertyName("priority")] public int Priority { get; set; }
    [JsonPropertyName("date_added")] public long DateAdded { get; set; }
}

// ---------------------------------------------------------------------------
// IStoreBrowseService/GetItems/v1 — keyless, up to 100 ids per request.
// ---------------------------------------------------------------------------

public sealed class StoreItemsResponse
{
    [JsonPropertyName("response")] public StoreItemsPayload? Response { get; set; }
}

public sealed class StoreItemsPayload
{
    [JsonPropertyName("store_items")] public List<StoreItem>? StoreItems { get; set; }
}

public sealed class StoreItem
{
    [JsonPropertyName("appid")] public int AppId { get; set; }
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("success")] public int Success { get; set; }
    [JsonPropertyName("visible")] public bool Visible { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("type")] public int Type { get; set; }
    [JsonPropertyName("is_free")] public bool IsFree { get; set; }
    [JsonPropertyName("store_url_path")] public string? StoreUrlPath { get; set; }
    [JsonPropertyName("assets")] public StoreAssets? Assets { get; set; }
    [JsonPropertyName("release")] public StoreRelease? Release { get; set; }

    /// <summary>Absent for free games and for anything not yet purchasable.</summary>
    [JsonPropertyName("best_purchase_option")] public BestPurchaseOption? BestPurchaseOption { get; set; }

    [JsonPropertyName("reviews")] public StoreReviews? Reviews { get; set; }

    /// <summary>Some items carry the id in "id" rather than "appid".</summary>
    public int ResolvedAppId => AppId != 0 ? AppId : Id;
}

public sealed class StoreRelease
{
    [JsonPropertyName("steam_release_date")] public long? SteamReleaseDate { get; set; }
    [JsonPropertyName("original_release_date")] public long? OriginalReleaseDate { get; set; }
    [JsonPropertyName("is_coming_soon")] public bool IsComingSoon { get; set; }
    [JsonPropertyName("is_early_access")] public bool IsEarlyAccess { get; set; }

    /// <summary>date_full | date_quarter | date_year | text_comingsoon | text_tba</summary>
    [JsonPropertyName("coming_soon_display")] public string? ComingSoonDisplay { get; set; }

    [JsonPropertyName("custom_release_date_message")] public string? CustomReleaseDateMessage { get; set; }
    [JsonPropertyName("release_from_early_access_date")] public long? ReleaseFromEarlyAccessDate { get; set; }
}

/// <summary>
/// Prices arrive as strings rather than numbers — "1999", not 1999 — so these stay
/// strings and are parsed by <see cref="PriceMapper"/>.
/// </summary>
public sealed class BestPurchaseOption
{
    [JsonPropertyName("packageid")] public int PackageId { get; set; }
    [JsonPropertyName("purchase_option_name")] public string? PurchaseOptionName { get; set; }
    [JsonPropertyName("final_price_in_cents")] public string? FinalPriceInCents { get; set; }
    [JsonPropertyName("original_price_in_cents")] public string? OriginalPriceInCents { get; set; }
    [JsonPropertyName("formatted_final_price")] public string? FormattedFinalPrice { get; set; }
    [JsonPropertyName("formatted_original_price")] public string? FormattedOriginalPrice { get; set; }
    [JsonPropertyName("discount_pct")] public int DiscountPct { get; set; }
    [JsonPropertyName("active_discounts")] public List<ActiveDiscount>? ActiveDiscounts { get; set; }
}

public sealed class ActiveDiscount
{
    [JsonPropertyName("discount_amount")] public string? DiscountAmount { get; set; }
    [JsonPropertyName("discount_description")] public string? DiscountDescription { get; set; }
    [JsonPropertyName("discount_end_date")] public long? DiscountEndDate { get; set; }
}

public sealed class StoreReviews
{
    /// <summary>All languages — this is the figure the store page headlines.</summary>
    [JsonPropertyName("summary_filtered")] public ReviewSummary? SummaryFiltered { get; set; }

    [JsonPropertyName("summary_language_specific")] public ReviewSummary? SummaryLanguageSpecific { get; set; }
}

public sealed class ReviewSummary
{
    [JsonPropertyName("review_count")] public int ReviewCount { get; set; }
    [JsonPropertyName("percent_positive")] public int PercentPositive { get; set; }

    /// <summary>Steam's bucketed score, roughly 1-9.</summary>
    [JsonPropertyName("review_score")] public int ReviewScore { get; set; }

    [JsonPropertyName("review_score_label")] public string? ReviewScoreLabel { get; set; }
}

public sealed class StoreAssets
{
    /// <summary>e.g. "steam/apps/440/${FILENAME}?t=1757348372"</summary>
    [JsonPropertyName("asset_url_format")] public string? AssetUrlFormat { get; set; }
    [JsonPropertyName("header")] public string? Header { get; set; }
    [JsonPropertyName("small_capsule")] public string? SmallCapsule { get; set; }
    [JsonPropertyName("library_capsule")] public string? LibraryCapsule { get; set; }
    [JsonPropertyName("main_capsule")] public string? MainCapsule { get; set; }
}

// ---------------------------------------------------------------------------
// IPlayerService/GetOwnedGames/v1 — requires a Web API key and the profile's
// "Game details" set to Public.
// ---------------------------------------------------------------------------

public sealed class OwnedGamesResponse
{
    [JsonPropertyName("response")] public OwnedGamesPayload? Response { get; set; }
}

public sealed class OwnedGamesPayload
{
    [JsonPropertyName("game_count")] public int GameCount { get; set; }
    [JsonPropertyName("games")] public List<OwnedGame>? Games { get; set; }
}

public sealed class OwnedGame
{
    [JsonPropertyName("appid")] public int AppId { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("playtime_forever")] public int PlaytimeForever { get; set; }
}
