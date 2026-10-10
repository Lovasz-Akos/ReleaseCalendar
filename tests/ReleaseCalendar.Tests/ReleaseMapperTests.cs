using ReleaseCalendar.Web.Data;
using ReleaseCalendar.Web.Steam;
using Xunit;

namespace ReleaseCalendar.Tests;

public class ReleaseMapperTests
{
    // Timestamps taken from the live API on 2026-10-10.
    private const long Tf2Release = 1191999600;          // Team Fortress 2
    private const long Bg3Release = 1691074826;          // Baldur's Gate 3
    private const long ValorMortis = 1791910800;         // date_full
    private const long LowLevelHunters = 1806476400;     // date_quarter → Q1 2027
    private const long Witchbrook = 1798704000;          // date_year  → 2026
    private const long EnshroudedEaExit = 1792047600;    // release_from_early_access_date

    [Theory]
    [InlineData(Tf2Release, 2007, 10, 10)]
    [InlineData(Bg3Release, 2023, 8, 3)]
    [InlineData(ValorMortis, 2026, 10, 13)]
    public void ToDate_reproduces_the_day_the_store_page_shows(long ts, int y, int m, int d)
    {
        Assert.Equal(new DateOnly(y, m, d), ReleaseMapper.ToDate(ts));
    }

    [Fact]
    public void date_full_is_exact()
    {
        var mapped = ReleaseMapper.Map(new StoreRelease
        {
            SteamReleaseDate = ValorMortis,
            IsComingSoon = true,
            ComingSoonDisplay = "date_full",
        });

        Assert.Equal(DatePrecision.Exact, mapped.Precision);
        Assert.Equal(new DateOnly(2026, 10, 13), mapped.Date);
    }

    [Fact]
    public void date_quarter_keeps_the_placeholder_but_is_marked_fuzzy()
    {
        var mapped = ReleaseMapper.Map(new StoreRelease
        {
            SteamReleaseDate = LowLevelHunters,
            IsComingSoon = true,
            ComingSoonDisplay = "date_quarter",
        });

        Assert.Equal(DatePrecision.Quarter, mapped.Precision);

        // Valve's placeholder is the last day of the quarter...
        Assert.Equal(new DateOnly(2027, 3, 31), mapped.Date);

        // ...but the calendar must show it at the start of the period it names.
        Assert.Equal(new DateOnly(2027, 1, 1), ReleaseMapper.PeriodStart(mapped.Date, mapped.Precision));
        Assert.Equal("Q1 2027", ReleaseMapper.Describe(mapped.Date, mapped.Precision, null));
    }

    [Fact]
    public void date_year_renders_as_a_year_not_new_years_eve()
    {
        var mapped = ReleaseMapper.Map(new StoreRelease
        {
            SteamReleaseDate = Witchbrook,
            IsComingSoon = true,
            ComingSoonDisplay = "date_year",
        });

        Assert.Equal(DatePrecision.Year, mapped.Precision);
        Assert.Equal(new DateOnly(2026, 12, 31), mapped.Date);
        Assert.Equal(new DateOnly(2026, 1, 1), ReleaseMapper.PeriodStart(mapped.Date, mapped.Precision));

        // The bug this whole field exists to prevent.
        Assert.Equal("2026", ReleaseMapper.Describe(mapped.Date, mapped.Precision, null));
        Assert.DoesNotContain("31", ReleaseMapper.Describe(mapped.Date, mapped.Precision, null));
    }

    [Fact]
    public void text_tba_has_no_date_even_when_a_timestamp_is_present()
    {
        var mapped = ReleaseMapper.Map(new StoreRelease
        {
            SteamReleaseDate = Witchbrook,          // a real-looking timestamp...
            IsComingSoon = true,
            ComingSoonDisplay = "text_tba",         // ...that means nothing
            CustomReleaseDateMessage = "To be announced",
        });

        Assert.Equal(DatePrecision.Tba, mapped.Precision);
        Assert.Null(mapped.Date);
        Assert.Null(ReleaseMapper.PeriodStart(mapped.Date, mapped.Precision));
        Assert.Equal("To be announced", ReleaseMapper.Describe(mapped.Date, mapped.Precision, mapped.CustomMessage));
    }

    [Fact]
    public void text_comingsoon_uses_the_store_message()
    {
        var mapped = ReleaseMapper.Map(new StoreRelease
        {
            IsComingSoon = true,
            ComingSoonDisplay = "text_comingsoon",
            CustomReleaseDateMessage = "Coming soon",
        });

        Assert.Equal(DatePrecision.ComingSoon, mapped.Precision);
        Assert.Null(mapped.Date);
        Assert.Equal("Coming soon", ReleaseMapper.Describe(mapped.Date, mapped.Precision, mapped.CustomMessage));
    }

    [Fact]
    public void a_released_game_has_no_display_field_and_is_exact()
    {
        var mapped = ReleaseMapper.Map(new StoreRelease
        {
            SteamReleaseDate = Bg3Release,
            OriginalReleaseDate = Bg3Release,
            IsComingSoon = false,
            ComingSoonDisplay = null,
        });

        Assert.Equal(DatePrecision.Exact, mapped.Precision);
        Assert.Equal(new DateOnly(2023, 8, 3), mapped.Date);
        Assert.False(mapped.IsComingSoon);
    }

    [Fact]
    public void an_unknown_display_value_degrades_instead_of_throwing()
    {
        var mapped = ReleaseMapper.Map(new StoreRelease
        {
            IsComingSoon = true,
            ComingSoonDisplay = "date_week_of",   // hypothetical future Steam value
            SteamReleaseDate = Witchbrook,
        });

        Assert.Equal(DatePrecision.ComingSoon, mapped.Precision);
        Assert.Null(mapped.Date);
    }

    [Fact]
    public void a_released_game_with_no_timestamp_is_unknown_not_exact()
    {
        // Steam really does this for delisted apps, betas and region-locked items —
        // GTA III and Metro Exodus Enhanced Edition both came back this way.
        var mapped = ReleaseMapper.Map(new StoreRelease
        {
            SteamReleaseDate = null,
            OriginalReleaseDate = null,
            IsComingSoon = false,
            ComingSoonDisplay = null,
        });

        // Left as Exact-with-no-date it would fall through every calendar tier unseen.
        Assert.Equal(DatePrecision.Unknown, mapped.Precision);
        Assert.Null(mapped.Date);
        Assert.Null(ReleaseMapper.PeriodStart(mapped.Date, mapped.Precision));
    }

    [Fact]
    public void an_upcoming_game_claiming_an_exact_date_without_one_falls_back_to_tba()
    {
        var mapped = ReleaseMapper.Map(new StoreRelease
        {
            SteamReleaseDate = null,
            IsComingSoon = true,
            ComingSoonDisplay = "date_full",   // contradicts itself
        });

        Assert.Equal(DatePrecision.Tba, mapped.Precision);
        Assert.Null(mapped.Date);
    }

    [Fact]
    public void a_missing_release_block_is_unknown_not_coming_soon()
    {
        // Delisted games (Battle vs Chess, F1 2019) come back with no release block at
        // all. Treating that as "coming soon" files them next to genuinely unannounced
        // games like Deadlock.
        var mapped = ReleaseMapper.Map(null);

        Assert.Equal(DatePrecision.Unknown, mapped.Precision);
        Assert.False(mapped.IsComingSoon);
        Assert.Null(mapped.Date);
    }

    [Fact]
    public void early_access_exit_date_is_mapped_separately()
    {
        var mapped = ReleaseMapper.Map(new StoreRelease
        {
            SteamReleaseDate = 1706107189,              // already out, in Early Access
            IsComingSoon = false,
            ReleaseFromEarlyAccessDate = EnshroudedEaExit,
        });

        Assert.Equal(new DateOnly(2026, 10, 15), mapped.EarlyAccessExitDate);
        Assert.NotEqual(mapped.Date, mapped.EarlyAccessExitDate);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 1)]
    [InlineData(4, 2)]
    [InlineData(6, 2)]
    [InlineData(7, 3)]
    [InlineData(9, 3)]
    [InlineData(10, 4)]
    [InlineData(12, 4)]
    public void quarter_boundaries(int month, int expected)
    {
        Assert.Equal(expected, ReleaseMapper.Quarter(new DateOnly(2027, month, 1)));
    }

    [Fact]
    public void capsule_url_substitutes_the_filename_placeholder()
    {
        var url = ReleaseMapper.CapsuleUrl(new StoreAssets
        {
            AssetUrlFormat = "steam/apps/440/${FILENAME}?t=1757348372",
            Header = "header.jpg",
        });

        Assert.Equal("https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/440/header.jpg?t=1757348372", url);
    }

    [Fact]
    public void capsule_url_is_null_without_assets() => Assert.Null(ReleaseMapper.CapsuleUrl(null));
}
