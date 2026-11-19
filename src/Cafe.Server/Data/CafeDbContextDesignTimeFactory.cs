using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Cafe.Server.Data;

/// <summary>Used only by dotnet ef at design time.</summary>
public class CafeDbContextDesignTimeFactory : IDesignTimeDbContextFactory<CafeDbContext>
{
    public CafeDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<CafeDbContext>()
            .UseSqlite("Data Source=cafe.db")
            .Options;
        return new CafeDbContext(options);
    }
}
