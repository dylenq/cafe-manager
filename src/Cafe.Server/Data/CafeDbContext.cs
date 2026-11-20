using Microsoft.EntityFrameworkCore;

namespace Cafe.Server.Data;

public class CafeDbContext(DbContextOptions<CafeDbContext> options) : DbContext(options)
{
    public DbSet<Setting> Settings => Set<Setting>();

    public DbSet<StationType> StationTypes => Set<StationType>();

    public DbSet<Station> Stations => Set<Station>();

    public DbSet<Session> Sessions => Set<Session>();

    public DbSet<SessionSegment> SessionSegments => Set<SessionSegment>();

    public DbSet<SessionEvent> SessionEvents => Set<SessionEvent>();

    public DbSet<RateRule> RateRules => Set<RateRule>();

    public DbSet<Customer> Customers => Set<Customer>();

    public DbSet<TimePackage> TimePackages => Set<TimePackage>();

    public DbSet<CustomerMinuteLedger> CustomerMinuteLedger => Set<CustomerMinuteLedger>();

    public DbSet<PackageSale> PackageSales => Set<PackageSale>();

    public DbSet<Payment> Payments => Set<Payment>();

    public DbSet<Product> Products => Set<Product>();

    public DbSet<Order> Orders => Set<Order>();

    public DbSet<OrderLine> OrderLines => Set<OrderLine>();

    public DbSet<PaymentAllocation> PaymentAllocations => Set<PaymentAllocation>();

    public DbSet<AgentCommandLog> AgentCommandLogs => Set<AgentCommandLog>();

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
            e.Property(t => t.MemberHourlyRate).HasColumnType("TEXT").HasConversion<string?>();
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

        // DateTimeOffset is stored as binary (UTC ticks) so SQLite can ORDER BY it.
        var dto = new Microsoft.EntityFrameworkCore.Storage.ValueConversion.DateTimeOffsetToBinaryConverter();
        var dtoNullable = new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTimeOffset?, long?>(
            v => v.HasValue ? v.Value.ToUniversalTime().UtcTicks : null,
            v => v.HasValue ? new DateTimeOffset(v.Value, TimeSpan.Zero) : null);

        modelBuilder.Entity<Session>(e =>
        {
            e.Property(x => x.Mode).HasConversion<int>();
            e.Property(x => x.Status).HasConversion<int>();
            e.Property(x => x.EndReason).HasConversion<int?>();
            e.Property(x => x.StartedAtUtc).HasConversion(dto);
            e.Property(x => x.EndedAtUtc).HasConversion(dtoNullable);
            e.Property(x => x.GraceStartedAtUtc).HasConversion(dtoNullable);
            e.Property(x => x.ChargedAmount).HasColumnType("TEXT").HasConversion<string>();
            e.Property(x => x.Notes).HasMaxLength(500);
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasIndex(x => x.CustomerId);
            e.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
            // At most one non-ended session (Status 3 = Ended) per station, enforced by the database.
            e.HasIndex(x => x.StationId).IsUnique().HasDatabaseName("IX_Sessions_StationId_Active")
                .HasFilter("\"Status\" <> 3");
            e.HasOne(x => x.Station).WithMany().HasForeignKey(x => x.StationId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Segments).WithOne(x => x.Session).HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Events).WithOne(x => x.Session).HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SessionSegment>(e =>
        {
            e.Property(x => x.StartUtc).HasConversion(dto);
            e.Property(x => x.EndUtc).HasConversion(dtoNullable);
            e.HasIndex(x => x.SessionId);
        });

        modelBuilder.Entity<SessionEvent>(e =>
        {
            e.Property(x => x.Type).HasConversion<int>();
            e.Property(x => x.AtUtc).HasConversion(dto);
            e.Property(x => x.Details).IsRequired().HasMaxLength(300);
            e.HasIndex(x => new { x.SessionId, x.AtUtc });
        });

        modelBuilder.Entity<AgentCommandLog>(e =>
        {
            e.Property(x => x.StationName).IsRequired().HasMaxLength(100);
            e.Property(x => x.Command).HasConversion<int>();
            e.Property(x => x.AtUtc).HasConversion(dto);
            e.Property(x => x.Details).IsRequired().HasMaxLength(300);
            e.HasIndex(x => x.AtUtc);
            e.HasIndex(x => x.StationId);
        });

        modelBuilder.Entity<RateRule>(e =>
        {
            e.Property(x => x.Name).IsRequired().HasMaxLength(100);
            e.Property(x => x.DaysOfWeek).HasConversion<int>();
            e.Property(x => x.Kind).HasConversion<int>();
            e.Property(x => x.Value).HasColumnType("TEXT").HasConversion<string>();
            e.Property(x => x.AppliesTo).HasConversion<int>();
            e.HasOne(x => x.StationType).WithMany().HasForeignKey(x => x.StationTypeId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Customer>(e =>
        {
            e.Property(x => x.Name).IsRequired().HasMaxLength(100);
            e.Property(x => x.Phone).HasMaxLength(20);
            // SQLite allows many NULLs in a unique index, so customers without a phone are unaffected.
            e.HasIndex(x => x.Phone).IsUnique();
            e.Property(x => x.Notes).HasMaxLength(500);
            e.Property(x => x.CreatedAtUtc).HasConversion(dto);
        });

        modelBuilder.Entity<TimePackage>(e =>
        {
            e.Property(x => x.Name).IsRequired().HasMaxLength(100).UseCollation("NOCASE");
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.Price).HasColumnType("TEXT").HasConversion<string>();
            e.HasOne(x => x.StationType).WithMany().HasForeignKey(x => x.StationTypeId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CustomerMinuteLedger>(e =>
        {
            e.ToTable("CustomerMinuteLedger");
            e.Property(x => x.Reason).HasConversion<int>();
            e.Property(x => x.AtUtc).HasConversion(dto);
            e.Property(x => x.Note).HasMaxLength(300);
            e.HasIndex(x => new { x.CustomerId, x.StationTypeId });
            e.HasIndex(x => x.SessionId);
            e.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<StationType>().WithMany().HasForeignKey(x => x.StationTypeId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Session).WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.PackageSale).WithMany().HasForeignKey(x => x.PackageSaleId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PackageSale>(e =>
        {
            e.Property(x => x.Price).HasColumnType("TEXT").HasConversion<string>();
            e.Property(x => x.AtUtc).HasConversion(dto);
            e.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.TimePackage).WithMany().HasForeignKey(x => x.TimePackageId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Payment>(e =>
        {
            e.Property(x => x.Amount).HasColumnType("TEXT").HasConversion<string>();
            e.Property(x => x.Method).HasConversion<int>();
            e.Property(x => x.AtUtc).HasConversion(dto);
            e.Property(x => x.Note).HasMaxLength(300);
            e.HasOne(x => x.PackageSale).WithMany().HasForeignKey(x => x.PackageSaleId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Session).WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Order).WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.PrimaryPayment).WithMany().HasForeignKey(x => x.PrimaryPaymentId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.PrimaryPaymentId);
            e.HasMany(x => x.Allocations).WithOne(x => x.Payment).HasForeignKey(x => x.PaymentId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Product>(e =>
        {
            e.Property(x => x.Name).IsRequired().HasMaxLength(100).UseCollation("NOCASE");
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.Category).IsRequired().HasMaxLength(50);
            e.Property(x => x.Price).HasColumnType("TEXT").HasConversion<string>();
        });

        modelBuilder.Entity<Order>(e =>
        {
            e.Property(x => x.CreatedAtUtc).HasConversion(dto);
            e.Property(x => x.Status).HasConversion<int>();
            e.Property(x => x.Total).HasColumnType("TEXT").HasConversion<string>();
            e.Property(x => x.Note).HasMaxLength(300);
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.SessionId);
            e.HasIndex(x => x.CustomerId);
            e.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Session).WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Lines).WithOne(x => x.Order).HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<OrderLine>(e =>
        {
            e.Property(x => x.ProductNameSnapshot).IsRequired().HasMaxLength(100);
            e.Property(x => x.UnitPriceSnapshot).HasColumnType("TEXT").HasConversion<string>();
            e.Property(x => x.LineTotal).HasColumnType("TEXT").HasConversion<string>();
            e.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PaymentAllocation>(e =>
        {
            e.Property(x => x.TargetType).HasConversion<int>();
            e.Property(x => x.Amount).HasColumnType("TEXT").HasConversion<string>();
            e.HasIndex(x => new { x.TargetType, x.TargetId });
            e.HasIndex(x => x.PaymentId);
        });
    }
}
