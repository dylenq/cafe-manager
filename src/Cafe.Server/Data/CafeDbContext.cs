using Microsoft.EntityFrameworkCore;

namespace Cafe.Server.Data;

public class CafeDbContext(DbContextOptions<CafeDbContext> options) : DbContext(options)
{
    public DbSet<Setting> Settings => Set<Setting>();

    public DbSet<StationType> StationTypes => Set<StationType>();

    public DbSet<Station> Stations => Set<Station>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Setting>(e =>
        {
            e.HasKey(s => s.Key);
            e.Property(s => s.Key).IsRequired();
            e.Property(s => s.Value).IsRequired();
        });

        modelBuilder.Entity<StationType>(e =>
        {
            e.Property(t => t.Name).IsRequired().HasMaxLength(100).UseCollation("NOCASE");
            e.HasIndex(t => t.Name).IsUnique();
            // TEXT storage keeps decimals exact (SQLite has no decimal type); converter uses invariant culture.
            e.Property(t => t.HourlyRate).HasColumnType("TEXT").HasConversion<string>();
        });

        modelBuilder.Entity<Station>(e =>
        {
            e.Property(s => s.Name).IsRequired().HasMaxLength(100).UseCollation("NOCASE");
            e.HasIndex(s => s.Name).IsUnique();
            e.Property(s => s.Kind).HasConversion<int>();
            e.Property(s => s.AgentMachineId).HasMaxLength(100).UseCollation("NOCASE");
            // SQLite allows many NULLs in a unique index, so console stations (no agent) are unaffected.
            e.HasIndex(s => s.AgentMachineId).IsUnique();
            e.Property(s => s.TvIpAddress).HasMaxLength(45);
            e.Property(s => s.TvMacAddress).HasMaxLength(17);
            e.Property(s => s.TvToken).HasMaxLength(200);
            e.HasOne(s => s.StationType)
                .WithMany(t => t.Stations)
                .HasForeignKey(s => s.StationTypeId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
