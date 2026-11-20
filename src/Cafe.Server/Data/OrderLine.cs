namespace Cafe.Server.Data;

/// <summary>One product on an order. Name and price are snapshots, so later product edits never change history.</summary>
public class OrderLine
{
    public int Id { get; set; }

    public int OrderId { get; set; }

    public Order Order { get; set; } = null!;

    public int ProductId { get; set; }

    public Product Product { get; set; } = null!;

    public string ProductNameSnapshot { get; set; } = string.Empty;

    public decimal UnitPriceSnapshot { get; set; }

    public int Quantity { get; set; }

    public decimal LineTotal { get; set; }
}
