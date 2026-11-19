using Cafe.Server.Agents;
using Microsoft.Extensions.Time.Testing;

namespace Cafe.Tests;

public class AgentRegistryTests
{
    private static AgentRegistry Create() => new(new FakeTimeProvider());

    [Fact]
    public void Throwing_subscriber_does_not_break_register_or_other_subscribers()
    {
        var registry = Create();
        var seen = new List<string>();
        registry.Changed += _ => throw new InvalidOperationException("boom");
        registry.Changed += id => seen.Add(id);

        registry.Register("pc-01", "c1", "1", () => { });
        registry.Unregister("pc-01", "c1");

        Assert.Equal(["pc-01", "pc-01"], seen);
        Assert.False(registry.IsConnected("pc-01"));
    }
}
