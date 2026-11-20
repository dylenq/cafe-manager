using Cafe.Server.Data;
using Cafe.Server.Payments;
using Cafe.Server.Stations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cafe.Server.Customers;

public class PackageService(
    IDbContextFactory<CafeDbContext> dbFactory, TimeProvider time, BalanceLock balanceLock, ILogger<PackageService>? logger = null)
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
    /// Sells a package for one full-price payment of <paramref name="method"/> (Phase 5 entry point). It shares one code
    /// path with <see cref="SellPaidAsync"/>, so the sale also writes its payment allocation. When
    /// <paramref name="expectedPrice"/> is given (the price the cashier saw) and the package price has changed since,
    /// nothing is sold.
    /// </summary>
    public async Task<ServiceResult<PackageSale>> SellAsync(
        int customerId, int packageId, PaymentMethod method, decimal? expectedPrice = null, CancellationToken cancellationToken = default)
    {
        var sold = await SellCoreAsync(
            customerId, packageId, price => [new TenderLine(method, price)], expectedPrice, "Price changed — review the package and try again.", cancellationToken);
        return sold.Success
            ? ServiceResult<PackageSale>.Ok(sold.Value!.Sale)
            : sold.Kind == FailureKind.NotFound ? ServiceResult<PackageSale>.NotFound(sold.Error!) : ServiceResult<PackageSale>.Fail(sold.Error!, sold.FieldErrors);
    }

    /// <summary>
    /// Sells a package through the payment path (D-06): PackageSale, ledger credit, one Payment per tender line and the
    /// allocations to the sale, all in one transaction. The sale must be paid in full; cash may be tendered above the
    /// price (change is returned, never stored). <paramref name="expectedDue"/> is the price the cashier saw.
    /// </summary>
    public async Task<ServiceResult<SettleResult>> SellPaidAsync(
        int customerId, int packageId, IReadOnlyList<TenderLine> tenders, decimal expectedDue, CancellationToken cancellationToken = default)
    {
        var sold = await SellCoreAsync(customerId, packageId, _ => tenders, expectedDue, SettlementService.ChangedMessage, cancellationToken);
        return sold.Success
            ? ServiceResult<SettleResult>.Ok(sold.Value!.Result)
            : sold.Kind == FailureKind.NotFound ? ServiceResult<SettleResult>.NotFound(sold.Error!) : ServiceResult<SettleResult>.Fail(sold.Error!, sold.FieldErrors);
    }

    private sealed record Sold(PackageSale Sale, SettleResult Result);

    private async Task<ServiceResult<Sold>> SellCoreAsync(
        int customerId, int packageId, Func<decimal, IReadOnlyList<TenderLine>> tendersFor, decimal? expectedPrice,
        string changedMessage, CancellationToken cancellationToken)
    {
        using var _ = await balanceLock.AcquireAsync(cancellationToken);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var customer = await CustomerService.LoadLinkableAsync(db, customerId, cancellationToken);
        if (!customer.Success)
        {
            return customer.Kind == FailureKind.NotFound
                ? ServiceResult<Sold>.NotFound(customer.Error!)
                : ServiceResult<Sold>.Fail(customer.Error!);
        }

        var package = await db.TimePackages.FirstOrDefaultAsync(p => p.Id == packageId, cancellationToken);
        if (package is null)
        {
            return ServiceResult<Sold>.NotFound(Gone);
        }

        if (!package.IsActive)
        {
            return ServiceResult<Sold>.Fail("That package is no longer on sale.");
        }

        if (expectedPrice is { } expected && expected != package.Price)
        {
            return ServiceResult<Sold>.Fail(changedMessage);
        }

        var tenders = tendersFor(package.Price);
        if (package.Price == 0m)
        {
            // A free package still records its (zero) payment as in Phase 5; there is nothing to validate or allocate.
            tenders = [new TenderLine(tenders.Count > 0 ? tenders[0].Method : PaymentMethod.Cash, 0m)];
        }
        else if (SettlementService.ValidateTenders(tenders) is { } tenderError)
        {
            return ServiceResult<Sold>.Fail(tenderError, new Dictionary<string, string> { ["Tenders"] = tenderError });
        }

        var applied = tenders.Sum(t => t.Amount);
        if (applied < package.Price)
        {
            return ServiceResult<Sold>.Fail("A package must be paid in full.");
        }

        if (applied > package.Price)
        {
            return ServiceResult<Sold>.Fail(SettlementService.OverpayMessage, new Dictionary<string, string> { ["Tenders"] = SettlementService.OverpayMessage });
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
        var payments = new List<Payment>();
        foreach (var tender in tenders)
        {
            var payment = new Payment
            {
                Amount = tender.Amount, Method = tender.Method, AtUtc = now, PackageSale = sale,
                PrimaryPayment = payments.Count == 0 ? null : payments[0],
            };
            payments.Add(payment);
            db.Payments.Add(payment);
        }

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
            // The sale id is only known after the first save, and the allocation has no foreign key to it, so the two
            // saves share one transaction: SQLite commits the sale, ledger credit, payments and allocations or none.
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            var plan = AllocationPlanner.Plan([new PlanTarget(AllocationTargetType.PackageSale, sale.Id, sale.Price)], tenders.Select(t => t.Amount).ToList());
            for (var i = 0; i < payments.Count; i++)
            {
                foreach (var part in plan[i])
                {
                    payments[i].Allocations.Add(new PaymentAllocation { TargetType = part.Type, TargetId = part.Id, Amount = part.Amount });
                }
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            // Uncommitted transaction rolls back on dispose: nothing was written. Report it instead of leaking the exception.
            logger?.LogError(ex, "Package sale could not be saved (customer {CustomerId}, package {PackageId}).", customerId, packageId);
            return ServiceResult<Sold>.Fail("The sale could not be saved, so nothing was recorded. Try again.");
        }

        var change = tenders.Where(t => t.Method == PaymentMethod.Cash && t.Tendered is not null).Sum(t => t.Tendered!.Value - t.Amount);
        var result = new SettleResult(payments.Select(p => p.Id).ToList(), payments[0].Id, package.Price, applied, 0m, change, []);
        return ServiceResult<Sold>.Ok(new Sold(sale, result));
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
