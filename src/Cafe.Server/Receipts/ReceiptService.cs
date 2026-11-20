using System.Globalization;
using Cafe.Server.Data;
using Cafe.Server.Pricing;
using Cafe.Server.Stations;
using Microsoft.EntityFrameworkCore;

namespace Cafe.Server.Receipts;

/// <summary>Loads the payments of one checkout and the things they paid for, then builds the receipt (POS-04).</summary>
public class ReceiptService(IDbContextFactory<CafeDbContext> dbFactory, PricingConfigService pricing)
{
    public const string CafeNameKey = "Receipts:CafeName";
    public const string AddressKey = "Receipts:Address";
    public const string PhoneKey = "Receipts:Phone";
    public const string FooterKey = "Receipts:Footer";
    public const string NotFoundMessage = "That receipt does not exist.";

    public async Task<ServiceResult<ReceiptDocument>> GetAsync(
        int paymentId, decimal? changeGiven = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var payment = await db.Payments.AsNoTracking().FirstOrDefaultAsync(p => p.Id == paymentId, cancellationToken);
        if (payment is null)
        {
            return ServiceResult<ReceiptDocument>.NotFound(NotFoundMessage);
        }

        var rootId = payment.PrimaryPaymentId ?? payment.Id;
        var payments = (await db.Payments.AsNoTracking()
            .Where(p => p.Id == rootId || p.PrimaryPaymentId == rootId).ToListAsync(cancellationToken))
            .OrderBy(p => p.Id).ToList();
        var paymentIds = payments.Select(p => p.Id).ToList();
        var mine = (await db.PaymentAllocations.AsNoTracking()
            .Where(a => paymentIds.Contains(a.PaymentId)).ToListAsync(cancellationToken))
            .OrderBy(a => a.Id).ToList();

        var targets = new List<ReceiptTarget>();
        foreach (var key in mine.Select(a => (a.TargetType, a.TargetId)).Distinct())
        {
            var applied = mine.Where(a => a.TargetType == key.TargetType && a.TargetId == key.TargetId).Sum(a => a.Amount);
            var allocatedAll = (await db.PaymentAllocations.AsNoTracking()
                .Where(a => a.TargetType == key.TargetType && a.TargetId == key.TargetId).ToListAsync(cancellationToken))
                .Select(a => a.Amount).ToList();
            var target = await LoadTargetAsync(db, key.TargetType, key.TargetId, applied, allocatedAll, cancellationToken);
            if (target is not null)
            {
                targets.Add(target);
            }
        }

        var settings = await db.Settings.AsNoTracking()
            .Where(s => s.Key == CafeNameKey || s.Key == AddressKey || s.Key == PhoneKey || s.Key == FooterKey)
            .ToDictionaryAsync(s => s.Key, s => s.Value, cancellationToken);
        string Get(string key, string fallback) =>
            settings.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : fallback;

        var lines = new[] { Get(AddressKey, string.Empty), Get(PhoneKey, string.Empty) }
            .Where(l => l.Length > 0).ToList();
        var header = new ReceiptHeader(Get(CafeNameKey, "Cafe"), lines, Get(FooterKey, "Thank you!"));

        var source = new ReceiptSource(
            payments[0].Id,
            payments[0].AtUtc,
            pricing.TimeZone,
            targets,
            payments.Select(p => new ReceiptPayment(p.Method, p.Amount)).ToList(),
            changeGiven,
            header);
        return ServiceResult<ReceiptDocument>.Ok(ReceiptBuilder.Build(source));
    }

    private static async Task<ReceiptTarget?> LoadTargetAsync(
        CafeDbContext db, AllocationTargetType type, int id, decimal applied, List<decimal> allocatedAll, CancellationToken ct)
    {
        switch (type)
        {
            case AllocationTargetType.Session:
                var session = await db.Sessions.AsNoTracking().Include(s => s.Station).Include(s => s.Segments)
                    .FirstOrDefaultAsync(s => s.Id == id, ct);
                if (session is null)
                {
                    return null;
                }

                var seconds = session.Segments.Where(s => s.EndUtc is not null).Sum(s => (long)(s.EndUtc!.Value - s.StartUtc).TotalSeconds);
                var minutes = (seconds + 59) / 60;
                var mode = session.Mode switch
                {
                    SessionMode.Prepaid => "Prepaid",
                    SessionMode.Balance => "Balance",
                    _ => "Open-ended",
                };
                var detail = string.Create(CultureInfo.InvariantCulture, $"{minutes} min, {mode}");
                return new ReceiptTarget(
                    ReceiptTargetKind.Session, session.Station.Name, detail, session.ChargedAmount, applied,
                    RemainingOf(session.ChargedAmount, allocatedAll), []);
            case AllocationTargetType.Order:
                var order = await db.Orders.AsNoTracking().Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == id, ct);
                if (order is null)
                {
                    return null;
                }

                var items = order.Lines.OrderBy(l => l.Id)
                    .Select(l => new ReceiptItem(l.ProductNameSnapshot, l.Quantity, l.UnitPriceSnapshot, l.LineTotal)).ToList();
                return new ReceiptTarget(
                    ReceiptTargetKind.Order, $"Order #{order.Id}", null, order.Total, applied,
                    order.Status == OrderStatus.Voided ? 0m : RemainingOf(order.Total, allocatedAll), items);
            default:
                var sale = await db.PackageSales.AsNoTracking().Include(s => s.TimePackage).FirstOrDefaultAsync(s => s.Id == id, ct);
                if (sale is null)
                {
                    return null;
                }

                return new ReceiptTarget(
                    ReceiptTargetKind.Package, sale.TimePackage.Name, $"{sale.Minutes} min", sale.Price, applied,
                    RemainingOf(sale.Price, allocatedAll), []);
        }
    }

    private static decimal RemainingOf(decimal total, List<decimal> allocated) =>
        Cafe.Server.Payments.OutstandingCalculator.Remaining(total, allocated);
}
