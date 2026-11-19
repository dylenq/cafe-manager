using Cafe.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Cafe.Tests.Infrastructure;

/// <summary>Wraps a real factory but makes every SaveChanges fail with a non-constraint error (simulates a locked or read-only DB).</summary>
public sealed class FailingSaveDbContextFactory(string connectionString) : IDbContextFactory<CafeDbContext>
{
    private sealed class ThrowOnSave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            throw new DbUpdateException("disk I/O error", new InvalidOperationException("simulated"));
    }

    public CafeDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<CafeDbContext>()
            .UseSqlite(connectionString)
            .AddInterceptors(new ThrowOnSave())
            .Options);
}
