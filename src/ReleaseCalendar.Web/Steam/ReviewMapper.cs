namespace ReleaseCalendar.Web.Steam;

/// <param name="Percent">Percent positive, 0-100.</param>
public readonly record struct MappedReviews(int? Percent, int? Count, int? Score, string? Label)
{
    public static readonly MappedReviews None = new(null, null, null, null);
    public bool HasReviews => Count is > 0;
}

public static class ReviewMapper
{
    /// <summary>
    /// An unreleased game returns a complete block reading "0% of 0 reviews", labelled
    /// "No user reviews". Zero reviews is an absence of data, not a score of zero, so the
    /// review count — never the percentage — decides whether there is anything to show.
    /// </summary>
    public static MappedReviews Map(StoreReviews? reviews)
    {
        if (reviews?.SummaryFiltered is not { ReviewCount: > 0 } summary)
            return MappedReviews.None;

        return new MappedReviews(
            summary.PercentPositive,
            summary.ReviewCount,
            summary.ReviewScore,
            summary.ReviewScoreLabel);
    }
}
