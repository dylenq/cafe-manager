using Cafe.Server.Data;

namespace Cafe.Server.Customers;

/// <summary>One non-zero balance bucket; StationTypeId null is the any-station-type bucket.</summary>
public sealed record BalanceBucketRow(int? StationTypeId, string? StationTypeName, int Minutes);

/// <summary>A customer's balance: non-zero buckets (any-type first) and their sum.</summary>
public sealed record BalanceSummary(IReadOnlyList<BalanceBucketRow> Buckets, int TotalMinutes)
{
    public static BalanceSummary Empty { get; } = new([], 0);
}

public sealed record SaleRow(int Id, string PackageName, decimal Price, int Minutes, int? StationTypeId, PaymentMethod? Method, DateTimeOffset AtUtc);

public sealed record LedgerRow(
    int Id,
    DateTimeOffset AtUtc,
    int? StationTypeId,
    int MinutesDelta,
    LedgerReason Reason,
    string ReasonText,
    int? SessionId,
    string? Note);

public sealed record RecentSessionRow(
    int Id,
    string StationName,
    SessionMode Mode,
    decimal ChargedAmount,
    SessionEndReason? EndReason,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? EndedAtUtc);

/// <summary>Read model behind /customers/{id}.</summary>
public sealed record CustomerDetail(
    Customer Customer,
    BalanceSummary Balance,
    IReadOnlyList<SaleRow> Sales,
    IReadOnlyList<LedgerRow> Ledger,
    IReadOnlyList<RecentSessionRow> Sessions);
