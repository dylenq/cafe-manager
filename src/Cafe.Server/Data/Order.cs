namespace Cafe.Server.Data;

/// <summary>
/// A food and drink order. Attached to a session (a tab), a customer, or neither (walk-in).
/// Lines are immutable snapshots; to change an order, void it and ring it again.
/// </summary>
public class Order
{
    public int Id { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public int? CustomerId { get; set; }

    public Customer? Customer { get; set; }

    public int? SessionId { get; set; }

    public Session? Session { get; set; }

    public OrderStatus Status { get; set; }

    public decimal Total { get; set; }

    public string? Note { get; set; }

    public ICollection<OrderLine> Lines { get; set; } = new List<OrderLine>();
}
