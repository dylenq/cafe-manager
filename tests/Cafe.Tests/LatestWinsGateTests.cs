using Cafe.Server.Dashboard;

namespace Cafe.Tests;

public class LatestWinsGateTests
{
    [Fact]
    public void An_older_result_arriving_after_a_newer_one_is_dropped()
    {
        var gate = new LatestWinsGate();
        var older = gate.Begin();
        var newer = gate.Begin();

        Assert.True(gate.TryApply(newer));
        Assert.False(gate.TryApply(older));
    }

    [Fact]
    public void Results_arriving_in_order_are_all_applied_and_a_ticket_applies_once()
    {
        var gate = new LatestWinsGate();
        var first = gate.Begin();
        var second = gate.Begin();

        Assert.True(gate.TryApply(first));
        Assert.True(gate.TryApply(second));
        Assert.False(gate.TryApply(second));
    }
}
