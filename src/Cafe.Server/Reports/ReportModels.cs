using Cafe.Server.Data;

namespace Cafe.Server.Reports;

public record ReportSummary(
    decimal Collected,
    decimal Cash,
    decimal Card,
    int SessionCount,
    decimal SessionHours,
    decimal AverageSessionMinutes,
    decimal ChargedUnpaid);

public record DayRow(DateOnly Date, decimal Collected, decimal Cash, decimal Card);

/// <summary>An ISO week (Monday start). Collected only counts days inside the report range.</summary>
public record WeekRow(DateOnly WeekStart, string IsoLabel, decimal Collected);

public record StationTypeRow(string Name, decimal Collected, decimal Hours);

public record ProductRow(string Name, string Category, decimal Quantity, decimal Revenue);

public record CategoryRow(string Category, decimal Quantity, decimal Revenue);

public record MethodRow(PaymentMethod Method, decimal Collected);

public record ReportData(
    ReportRange Range,
    ReportSummary Summary,
    IReadOnlyList<DayRow> Days,
    IReadOnlyList<WeekRow> Weeks,
    IReadOnlyList<StationTypeRow> StationTypes,
    IReadOnlyList<ProductRow> Products,
    IReadOnlyList<CategoryRow> Categories,
    IReadOnlyList<MethodRow> Methods);

/// <summary>One allocation of a payment towards a target.</summary>
public record AllocationFact(AllocationTargetType TargetType, int TargetId, decimal Amount);

public record PaymentFact(int Id, PaymentMethod Method, DateTimeOffset AtUtc, IReadOnlyList<AllocationFact> Allocations);

/// <summary>An ended session; ActiveSeconds is the summed duration of its segments.</summary>
public record SessionFact(int Id, string StationTypeName, long ActiveSeconds, DateTimeOffset EndedAtUtc);

public record OrderLineFact(string ProductName, string Category, int Quantity, decimal LineTotal);

/// <summary>A non-voided order with its lines. Voided orders are never passed in.</summary>
public record OrderFact(int Id, decimal Total, IReadOnlyList<OrderLineFact> Lines);

/// <summary>Something billed (session, order or package sale) with everything ever allocated to it.</summary>
public record ChargeFact(AllocationTargetType Type, int Id, DateTimeOffset AtUtc, decimal Total, decimal AllocatedAllTime);

/// <summary>Everything the pure builder needs, already loaded from the database.</summary>
public record ReportInput(
    IReadOnlyList<PaymentFact> Payments,
    IReadOnlyList<SessionFact>? Sessions = null,
    IReadOnlyList<OrderFact>? Orders = null,
    IReadOnlyList<ChargeFact>? Charges = null,
    /// <summary>Session id to the station type name of the session's first segment.</summary>
    IReadOnlyDictionary<int, string>? SessionStationTypes = null);
