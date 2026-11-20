using Cafe.Server.Data;
using Cafe.Server.Stations;
using Microsoft.EntityFrameworkCore;

namespace Cafe.Server.Customers;

public class PackageService(IDbContextFactory<CafeDbContext> dbFactory, TimeProvider time, BalanceLock balanceLock)
{
    public async Task<List<TimePackage>> ListAsync(bool includeInactive = true, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.TimePackages.AsNoTracking().Where(p => includeInactive || p.IsActive).ToListAsync(cancellationToken);
        return rows.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(p => p.Id).ToList();
    }

    public async Task<TimePackage?> GetAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.TimePackages.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
    }

    public async Task<ServiceResult<TimePackage>> CreateAsync(TimePackageInput input, CancellationToken cancellationToken = default)
    {
        if (InputValidation.Validate(input) is { } errors)
        {
            return ServiceResult<TimePackage>.Fail(InputValidation.Summarise(errors), errors);
        }

        var name = input.Name.Trim();
        if (name.Length == 0)
        {
            return FieldFail("Name", "Name is required.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (await CheckAsync(db, name, input.StationTypeId, null, cancellationToken) is { } failure)
        {
            return failure;
        }

        var entity = new TimePackage
        {
            Name = name, Minutes = input.Minutes, Price = input.Price, StationTypeId = input.StationTypeId, IsActive = input.IsActive,
        };
        db.TimePackages.Add(entity);
        return await SaveAsync(db, entity, name, cancellationToken);
    }

    /// <summary>Edits a package. Past sales keep their own snapshot of price, minutes and bucket, so they are never touched.</summary>
    public async Task<ServiceResult<TimePackage>> UpdateAsync(int id, TimePackageInput input, CancellationToken cancellationToken = default)
    {
        if (InputValidation.Validate(input) is { } errors)
        {
            return ServiceResult<TimePackage>.Fail(InputValidation.Summarise(errors), errors);
        }

        var name = input.Name.Trim();
        if (name.Length == 0)
        {
            return FieldFail("Name", "Name is required.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.TimePackages.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (entity is null)
        {
            return ServiceResult<TimePackage>.NotFound(Gone);
        }

        if (await CheckAsync(db, name, input.StationTypeId, id, cancellationToken) is { } failure)
        {
            return failure;
        }

        entity.Name = name;
        entity.Minutes = input.Minutes;
        entity.Price = input.Price;
        entity.StationTypeId = input.StationTypeId;
        entity.IsActive = input.IsActive;
        return await SaveAsync(db, entity, name, cancellationToken);
    }

    /// <summary>Takes a package off sale (or back on). Past sales and their ledger credits stay valid.</summary>
    public async Task<ServiceResult> SetActiveAsync(int id, bool active, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.TimePackages.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (entity is null)
        {
            return ServiceResult.NotFound(Gone);
        }

        entity.IsActive = active;
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult.Ok();
    }

    /// <summary>Deletes a package that was never sold; a sold one must be deactivated instead.</summary>
    public async Task<ServiceResult> DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.TimePackages.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (entity is null)
        {
            return ServiceResult.NotFound(Gone);
        }

        const string Sold = "This package has been sold, so it cannot be deleted. Deactivate it instead.";
        if (await db.PackageSales.AnyAsync(s => s.TimePackageId == id, cancellationToken))
        {
            return ServiceResult.Fail(Sold);
        }

        db.TimePackages.Remove(entity);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (InputValidation.IsForeignKeyViolation(ex))
        {
            return ServiceResult.Fail(Sold);
        }

        return ServiceResult.Ok();
    }

    /// <summary>
    /// Sells a package: the PackageSale, the Payment and the ledger credit are added to one context and saved with a
    /// single SaveChanges, so SQLite commits all three or none (CUST-04). When <paramref name="expectedPrice"/> is given
    /// (the price the cashier saw) and the package price has changed since, nothing is sold.
    /// </summary>
    public async Task<ServiceResult<PackageSale>> SellAsync(
        int customerId, int packageId, PaymentMethod method, decimal? expectedPrice = null, CancellationToken cancellationToken = default)
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
            return ServiceResult<PackageSale>.NotFound(Gone);
        }

        if (!package.IsActive)
        {
            return ServiceResult<PackageSale>.Fail("That package is no longer on sale.");
        }

        if (expectedPrice is { } expected && expected != package.Price)
        {
            return ServiceResult<PackageSale>.Fail("Price changed — review the package and try again.");
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

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // One SaveChanges is one transaction: nothing was written. Report it instead of leaking the exception.
            return ServiceResult<PackageSale>.Fail("The sale could not be saved, so nothing was recorded. Try again.");
        }

        return ServiceResult<PackageSale>.Ok(sale);
    }

    private const string Gone = "That package no longer exists. Refresh the list.";

    private static ServiceResult<TimePackage> FieldFail(string field, string message) =>
        ServiceResult<TimePackage>.Fail(message, new Dictionary<string, string> { [field] = message });

    /// <summary>Unique name (case-insensitive) and an existing station type; null when fine.</summary>
    private static async Task<ServiceResult<TimePackage>?> CheckAsync(CafeDbContext db, string name, int? stationTypeId, int? exceptId, CancellationToken ct)
    {
        if (await db.TimePackages.AnyAsync(p => p.Name == name && (exceptId == null || p.Id != exceptId), ct))
        {
            return Duplicate(name);
        }

        if (stationTypeId is { } typeId && !await db.StationTypes.AnyAsync(t => t.Id == typeId, ct))
        {
            return FieldFail("StationTypeId", "That station type no longer exists.");
        }

        return null;
    }

    private static async Task<ServiceResult<TimePackage>> SaveAsync(CafeDbContext db, TimePackage entity, string name, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (InputValidation.IsUniqueViolation(ex))
        {
            return Duplicate(name);
        }
        catch (DbUpdateException ex) when (InputValidation.IsForeignKeyViolation(ex))
        {
            return FieldFail("StationTypeId", "That station type no longer exists.");
        }

        return ServiceResult<TimePackage>.Ok(entity);
    }

    private static ServiceResult<TimePackage> Duplicate(string name) =>
        ServiceResult<TimePackage>.Fail($"A package named \"{name}\" already exists.",
            new Dictionary<string, string> { ["Name"] = $"A package named \"{name}\" already exists." });
}
