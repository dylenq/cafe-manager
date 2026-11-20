using Cafe.Server.Orders;

namespace Cafe.Tests;

public class AttachRulesTests
{
    [Theory]
    [InlineData(AttachKind.Session, AttachRules.ChooseSession)]
    [InlineData(AttachKind.Customer, AttachRules.ChooseCustomer)]
    public void IN02_charge_now_with_an_attach_mode_but_no_target_is_an_error(AttachKind kind, string message) =>
        Assert.Equal(message, AttachRules.ChargeError(kind, hasTarget: false));

    [Theory]
    [InlineData(AttachKind.WalkIn, false)]
    [InlineData(AttachKind.WalkIn, true)]
    [InlineData(AttachKind.Session, true)]
    [InlineData(AttachKind.Customer, true)]
    public void IN02_walk_in_or_a_chosen_target_may_be_charged(AttachKind kind, bool hasTarget) =>
        Assert.Null(AttachRules.ChargeError(kind, hasTarget));
}
