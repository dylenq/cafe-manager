namespace Cafe.Server.Orders;

public sealed record OrderLineDraft(int ProductId, int Quantity);

/// <summary>A cart ready to be rung up. Both ids null = walk-in.</summary>
public sealed record OrderDraft(IReadOnlyList<OrderLineDraft> Lines, int? CustomerId, int? SessionId, string? Note = null);
