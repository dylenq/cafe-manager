using Cafe.Server.Data;
using Cafe.Server.Orders;
using Microsoft.EntityFrameworkCore;

namespace Cafe.Tests.Infrastructure;

/// <summary>Helpers for report tests: place rows on exact instants without going through the services.</summary>
public abstract class ReportTestBase : PosTestBase
{
    /// <summary>Inserts one payment (Amount = sum of the allocations) with its allocation rows.</summary>
    protected async Task<int> AddPaymentAsync(
        PaymentMethod method, DateTimeOffset atUtc, params (AllocationTargetType Type, int TargetId, decimal Amount)[] allocations)
    {
        await using var db = await DbAsync();
        var payment = new Payment
        {
            Amount = allocations.Sum(a => a.Amount),
            Method = method,
            AtUtc = atUtc,
        };
        foreach (var (type, targetId, amount) in allocations)
        {
            payment.Allocations.Add(new PaymentAllocation { TargetType = type, TargetId = targetId, Amount = amount });
        }

        db.Payments.Add(payment);
        await db.SaveChangesAsync();
        return payment.Id;
    }

    /// <summary>Inserts an ended session with one closed segment per (station, start, end) tuple.</summary>
    protected async Task<int> AddEndedSessionAsync(decimal charged, params (int StationId, DateTimeOffset Start, DateTimeOffset End)[] segments)
    {
        await using var db = await DbAsync();
        var session = new Session
        {
            StationId = segments[^1].StationId,
            Mode = SessionMode.OpenEnded,
            Status = SessionStatus.Ended,
            StartedAtUtc = segments[0].Start,
            EndedAtUtc = segments[^1].End,
            EndReason = SessionEndReason.Stopped,
            ChargedAmount = charged,
        };
        foreach (var (stationId, start, end) in segments)
        {
            session.Segments.Add(new SessionSegment { StationId = stationId, StartUtc = start, EndUtc = end });
        }

        db.Sessions.Add(session);
        await db.SaveChangesAsync();
        return session.Id;
    }

    /// <summary>Inserts an order with snapshot lines (product ids must exist).</summary>
    protected async Task<int> AddOrderAsync(
        DateTimeOffset createdAtUtc, OrderStatus status, params (int ProductId, string Name, int Quantity, decimal UnitPrice)[] lines)
    {
        await using var db = await DbAsync();
        var order = new Order { CreatedAtUtc = createdAtUtc, Status = status };
        foreach (var (productId, name, quantity, unitPrice) in lines)
        {
            order.Lines.Add(new OrderLine
            {
                ProductId = productId, ProductNameSnapshot = name, Quantity = quantity,
                UnitPriceSnapshot = unitPrice, LineTotal = quantity * unitPrice,
            });
        }

        order.Total = order.Lines.Sum(l => l.LineTotal);
        db.Orders.Add(order);
        await db.SaveChangesAsync();
        return order.Id;
    }

    protected async Task<int> AddPackageSaleAsync(int customerId, int packageId, decimal price, DateTimeOffset atUtc, int? stationTypeId = null)
    {
        await using var db = await DbAsync();
        var sale = new PackageSale
        {
            CustomerId = customerId, TimePackageId = packageId, Price = price, Minutes = 60,
            StationTypeId = stationTypeId, AtUtc = atUtc,
        };
        db.PackageSales.Add(sale);
        await db.SaveChangesAsync();
        return sale.Id;
    }

    protected async Task SetBillingStartAsync(DateTimeOffset value)
    {
        await using var db = await DbAsync();
        var setting = await db.Settings.SingleAsync(s => s.Key == OpenTabsService.BillingStartKey);
        setting.Value = value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
        await db.SaveChangesAsync();
    }
}
