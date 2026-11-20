using Cafe.Server.Orders;

namespace Cafe.Tests;

public class CartTests
{
    [Fact]
    public void Adding_the_same_product_accumulates_quantity()
    {
        var cart = new Cart();
        cart.Add(1, "Cola", 2m);
        cart.Add(1, "Cola", 2m);

        var line = Assert.Single(cart.Lines);
        Assert.Equal(2, line.Quantity);
        Assert.Equal(4m, cart.Total);
    }

    [Fact]
    public void Different_products_keep_insertion_order()
    {
        var cart = new Cart();
        cart.Add(2, "Tea", 1m);
        cart.Add(1, "Cola", 2m);
        cart.Add(2, "Tea", 1m);

        Assert.Equal([2, 1], cart.Lines.Select(l => l.ProductId));
    }

    [Fact]
    public void Decrement_removes_the_line_at_quantity_one()
    {
        var cart = new Cart();
        cart.Add(1, "Cola", 2m);
        cart.Add(1, "Cola", 2m);

        cart.Decrement(1);
        Assert.Equal(1, Assert.Single(cart.Lines).Quantity);

        cart.Decrement(1);
        Assert.True(cart.IsEmpty);
    }

    [Fact]
    public void Increment_remove_and_clear()
    {
        var cart = new Cart();
        cart.Add(1, "Cola", 2m);
        cart.Add(2, "Tea", 1m);

        Assert.True(cart.Increment(2));
        Assert.False(cart.Increment(99));
        cart.Remove(1);
        Assert.Equal(2, Assert.Single(cart.Lines).Quantity);

        cart.Clear();
        Assert.True(cart.IsEmpty);
        Assert.Equal(0m, cart.Total);
    }

    [Fact]
    public void Quantity_is_capped_at_99()
    {
        var cart = new Cart();
        for (var i = 0; i < 99; i++)
        {
            Assert.True(cart.Add(1, "Cola", 1m));
        }

        Assert.False(cart.Add(1, "Cola", 1m));
        Assert.Equal(99, cart.Lines[0].Quantity);
    }

    [Fact]
    public void Total_is_decimal_exact()
    {
        var cart = new Cart();
        for (var i = 0; i < 3; i++)
        {
            cart.Add(1, "Sweet", 0.1m);
        }

        Assert.Equal(0.3m, cart.Total);
    }

    [Fact]
    public void ToDraft_carries_lines_customer_session_and_note()
    {
        var cart = new Cart();
        cart.Add(5, "Cola", 2m);
        cart.Add(5, "Cola", 2m);
        cart.Add(6, "Tea", 1m);

        var draft = cart.ToDraft(7, 8, "no ice");

        Assert.Equal(7, draft.CustomerId);
        Assert.Equal(8, draft.SessionId);
        Assert.Equal("no ice", draft.Note);
        Assert.Equal([(5, 2), (6, 1)], draft.Lines.Select(l => (l.ProductId, l.Quantity)));
    }
}
