using Microsoft.EntityFrameworkCore;

namespace ReleaseCalendar.Web.Data;

public class CalendarDb(DbContextOptions<CalendarDb> options) : DbContext(options)
{
    public DbSet<SteamApp> Apps => Set<SteamApp>();
    public DbSet<Entry> Entries => Set<Entry>();
    public DbSet<DateChange> DateChanges => Set<DateChange>();
    public DbSet<PricePoint> PricePoints => Set<PricePoint>();
    public DbSet<SyncRun> SyncRuns => Set<SyncRun>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<SteamApp>(e =>
        {
            e.HasKey(a => a.AppId);
            e.Property(a => a.AppId).ValueGeneratedNever();
            e.Property(a => a.Name).HasMaxLength(400);
            e.HasIndex(a => a.ReleaseDate);
            e.HasIndex(a => a.FetchedUtc);
        });

        b.Entity<Entry>(e =>
        {
            e.HasKey(x => new { x.SteamId, x.AppId, x.Source });
            e.HasOne(x => x.App).WithMany().HasForeignKey(x => x.AppId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<DateChange>(e =>
        {
            e.HasOne(x => x.App).WithMany().HasForeignKey(x => x.AppId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.SeenUtc);
            e.Ignore(x => x.DaysMoved);
        });

        b.Entity<PricePoint>(e =>
        {
            e.HasOne(x => x.App).WithMany().HasForeignKey(x => x.AppId).OnDelete(DeleteBehavior.Cascade);

            // Every read is "this app's points, oldest first".
            e.HasIndex(x => new { x.AppId, x.SeenUtc });
        });

        b.Entity<SyncRun>(e =>
        {
            e.HasIndex(x => x.StartedUtc);
            e.Ignore(x => x.Succeeded);
        });

        b.Entity<SteamApp>().Ignore(a => a.StoreUrl);
    }
}
