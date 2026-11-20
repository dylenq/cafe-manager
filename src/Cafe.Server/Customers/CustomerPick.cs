namespace Cafe.Server.Customers;

/// <summary>A customer chosen in the dashboard picker.</summary>
public sealed record CustomerPick(int Id, string Name, bool IsMember, string? Phone);
