using Cafe.Server.Data;

namespace Cafe.Server.Reports;

public record ReportSummary(decimal Collected, decimal Cash, decimal Card);

public record DayRow(DateOnly Date, decimal Collected, decimal Cash, decimal Card);

public record ReportData(ReportRange Range, ReportSummary Summary, IReadOnlyList<DayRow> Days);

/// <summary>One allocation of a payment towards a target.</summary>
public record AllocationFact(AllocationTargetType TargetType, int TargetId, decimal Amount);

public record PaymentFact(int Id, PaymentMethod Method, DateTimeOffset AtUtc, IReadOnlyList<AllocationFact> Allocations);

/// <summary>Everything the pure builder needs, already loaded from the database.</summary>
public record ReportInput(IReadOnlyList<PaymentFact> Payments);
