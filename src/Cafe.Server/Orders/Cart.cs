namespace Cafe.Server.Orders;

/// <summary>One cart line. UnitPrice is display only; OrderService re-snapshots the real price when the order is created.</summary>
public sealed record CartLine(int ProductId, string Name, decimal UnitPrice, int Quantity)
{
    public decimal LineTotal => UnitPrice * Quantity;
}

/// <summary>The POS cart (D-03). Pure in-memory model: lines keep insertion order, quantity is capped at 99.</summary>
public sealed class Cart
{
    private readonly List<CartLine> _lines = [];

    public IReadOnlyList<CartLine> Lines => _lines;

    public bool IsEmpty => _lines.Count == 0;

    public decimal Total => _lines.Sum(l => l.LineTotal);

    /// <summary>Adds one unit. Returns false (and changes nothing) when the line is already at the cap.</summary>
    public bool Add(int productId, string name, decimal price)
    {
        var index = _lines.FindIndex(l => l.ProductId == productId);
        if (index < 0)
        {
            _lines.Add(new CartLine(productId, name, price, 1));
            return true;
        }

        return Increment(productId);
    }

    public bool Increment(int productId)
    {
        var index = _lines.FindIndex(l => l.ProductId == productId);
        if (index < 0 || _lines[index].Quantity >= OrderService.MaxQuantity)
        {
            return false;
        }

        _lines[index] = _lines[index] with { Quantity = _lines[index].Quantity + 1 };
        return true;
    }

    /// <summary>Removes one unit; the line disappears at quantity 1.</summary>
    public void Decrement(int productId)
    {
        var index = _lines.FindIndex(l => l.ProductId == productId);
        if (index < 0)
        {
            return;
        }

        if (_lines[index].Quantity <= 1)
        {
            _lines.RemoveAt(index);
        }
        else
        {
            _lines[index] = _lines[index] with { Quantity = _lines[index].Quantity - 1 };
        }
    }

    public void Remove(int productId) => _lines.RemoveAll(l => l.ProductId == productId);

    public void Clear() => _lines.Clear();

    public OrderDraft ToDraft(int? customerId, int? sessionId, string? note = null) =>
        new(_lines.Select(l => new OrderLineDraft(l.ProductId, l.Quantity)).ToList(), customerId, sessionId, note);
}
