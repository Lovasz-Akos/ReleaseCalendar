using System.Globalization;

namespace ReleaseCalendar.Web.Steam;

/// <param name="FinalCents">What you'd pay now. Null when the app has no price at all.</param>
/// <param name="OriginalCents">Pre-discount price; equals FinalCents when not on sale.</param>
/// <param name="DiscountPct">0 when not discounted.</param>
public readonly record struct MappedPrice(
    int? FinalCents,
    int? OriginalCents,
    int DiscountPct,
    string? Formatted,
    string? FormattedOriginal,
    DateTime? DiscountEndsUtc)
{
    public bool HasPrice => FinalCents is not null;
    public bool OnSale => DiscountPct > 0;

    public static readonly MappedPrice None = new(null, null, 0, null, null, null);
}

public static class PriceMapper
{
    /// <summary>
    /// <c>best_purchase_option</c> is absent for free games and for anything not yet
    /// purchasable, which is not the same as "costs nothing" — both map to no price.
    /// </summary>
    public static MappedPrice Map(BestPurchaseOption? option)
    {
        if (option is null) return MappedPrice.None;

        var final = ParseCents(option.FinalPriceInCents);
        var original = ParseCents(option.OriginalPriceInCents) ?? final;

        // Only a discount that is live right now has an end date worth showing.
        var ends = option.ActiveDiscounts?
            .Select(d => d.DiscountEndDate)
            .Where(d => d is > 0)
            .Select(d => DateTimeOffset.FromUnixTimeSeconds(d!.Value).UtcDateTime)
            .OrderBy(d => d)
            .FirstOrDefault();

        return new MappedPrice(
            final,
            original,
            Math.Max(0, option.DiscountPct),
            option.FormattedFinalPrice,
            option.FormattedOriginalPrice,
            ends == default ? null : ends);
    }

    /// <summary>Steam sends cents as a string: "1999", not 1999.</summary>
    public static int? ParseCents(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cents) && cents >= 0
            ? cents
            : null;

    /// <summary>
    /// Render cents using the currency decoration Steam already sent us. Deriving the
    /// prefix/suffix from a known formatted price keeps history points in the right
    /// currency without hardcoding a symbol or guessing from the country code.
    /// </summary>
    public static string Format(int cents, string? formattedExample, int? exampleCents)
    {
        if (formattedExample is { Length: > 0 } sample && exampleCents is { } known && known > 0)
        {
            // Steam formats per store region: "$19.99" but also "19,99€". Try both
            // separators and reuse whichever one the sample actually used.
            foreach (var separator in stackalloc[] { '.', ',' })
            {
                var knownText = Amount(known, separator);
                var at = sample.IndexOf(knownText, StringComparison.Ordinal);

                if (at < 0) continue;

                return string.Concat(
                    sample.AsSpan(0, at),
                    Amount(cents, separator),
                    sample.AsSpan(at + knownText.Length));
            }
        }

        // Currencies with thousands grouping or no minor unit (HUF renders as
        // "4 299 Ft") won't match; a bare amount is the honest fallback.
        return Amount(cents, '.');
    }

    private static string Amount(int cents, char separator) =>
        (cents / 100m).ToString("0.00", CultureInfo.InvariantCulture).Replace('.', separator);
}
