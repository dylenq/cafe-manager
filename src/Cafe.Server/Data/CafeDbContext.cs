using Microsoft.EntityFrameworkCore;

namespace Cafe.Server.Data;

public class CafeDbContext(DbContextOptions<CafeDbContext> options) : DbContext(options)
{
    public DbSet<Setting> Settings => Set<Setting>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Setting>(e =>
        {
            e.HasKey(s => s.Key);
            e.Property(s => s.Key).IsRequired();
            e.Property(s => s.Value).IsRequired();
        });
    }
}
