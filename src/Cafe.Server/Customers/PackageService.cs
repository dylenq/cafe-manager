using Cafe.Server.Data;
using Cafe.Server.Stations;
using Microsoft.EntityFrameworkCore;

namespace Cafe.Server.Customers;

public class PackageService(IDbContextFactory<CafeDbContext> dbFactory, TimeProvider time, BalanceLock balanceLock)
{
    public async Task<ServiceResult<TimePackage>> CreateAsync(TimePackageInput input, CancellationToken cancellationToken = default)
    {
        if (InputValidation.Validate(input) is { } errors)
        {
            return ServiceResult<TimePackage>.Fail(InputValidation.Summarise(errors), errors);
        }

        var name = input.Name.Trim();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (await db.TimePackages.AnyAsync(p => p.Name == name, cancellationToken))
        {
            return Duplicate(name);
        }

        var entity = new TimePackage
        {
            Name = name, Minutes = input.Minutes, Price = input.Price, StationTypeId = input.StationTypeId, IsActive = input.IsActive,
        };
        db.TimePackages.Add(entity);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (InputValidation.IsUniqueViolation(ex))
        {
            return Duplicate(name);
        }
        catch (DbUpdateException ex) when (InputValidation.IsForeignKeyViolation(ex))
        {
            return ServiceResult<TimePackage>.Fail("That station type no longer exists.");
        }

        return ServiceResult<TimePackage>.Ok(entity);
    }

    /// <summary>
    /// Sells a package: the PackageSale, the Payment and the ledger credit are added to one context and saved with a
    /// single SaveChanges, so SQLite commits all three or none (CUST-04).
    /// </summary>
    public async Task<ServiceResult<PackageSale>> SellAsync(int customerId, int packageId, PaymentMethod method, CancellationToken cancellationToken = default)
    {
        using var _ = await balanceLock.AcquireAsync(cancellationToken);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var customer = await CustomerService.LoadLinkableAsync(db, customerId, cancellationToken);
        if (!customer.Success)
        {
            return customer.Kind == FailureKind.NotFound
                ? ServiceResult<PackageSale>.NotFound(customer.Error!)
                : ServiceResult<PackageSale>.Fail(customer.Error!);
        }

        var package = await db.TimePackages.FirstOrDefaultAsync(p => p.Id == packageId, cancellationToken);
        if (package is null)
        {
            return ServiceResult<PackageSale>.NotFound("That package no longer exists. Refresh the list.");
        }

        if (!package.IsActive)
        {
            return ServiceResult<PackageSale>.Fail("That package is no longer for sale.");
        }

        var now = time.GetUtcNow();
        var sale = new PackageSale
        {
            CustomerId = customerId,
            TimePackageId = package.Id,
            Price = package.Price,
            Minutes = package.Minutes,
            StationTypeId = package.StationTypeId,
            AtUtc = now,
        };
        db.PackageSales.Add(sale);
        db.Payments.Add(new Payment { Amount = package.Price, Method = method, AtUtc = now, PackageSale = sale });
        db.CustomerMinuteLedger.Add(new CustomerMinuteLedger
        {
            CustomerId = customerId,
            StationTypeId = package.StationTypeId,
            MinutesDelta = package.Minutes,
            Reason = LedgerReason.PackagePurchase,
            PackageSale = sale,
            AtUtc = now,
        });

        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult<PackageSale>.Ok(sale);
    }

    private static ServiceResult<TimePackage> Duplicate(string name) =>
        ServiceResult<TimePackage>.Fail($"A package named \"{name}\" already exists.",
            new Dictionary<string, string> { ["Name"] = $"A package named \"{name}\" already exists." });
}
