namespace Cafe.Server.Data;

/// <summary>Explicit values: stored as integers.</summary>
public enum OrderStatus
{
    Open = 0,
    Paid = 1,
    Voided = 2,
}

/// <summary>What a payment allocation pays towards. Explicit values: stored as integers.</summary>
public enum AllocationTargetType
{
    Session = 0,
    Order = 1,
    PackageSale = 2,
}
