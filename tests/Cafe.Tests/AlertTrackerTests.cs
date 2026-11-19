using Cafe.Server.Dashboard;

namespace Cafe.Tests;

public class AlertTrackerTests
{
    private static AlertInput In(int id, AlertLevel level, string? name = null) => new(id, name ?? $"S{id}", level);

    [Fact]
    public void First_warning_is_new_and_repeat_is_not()
    {
        var tracker = new AlertTracker();

        var first = tracker.Update([In(1, AlertLevel.Warning)]);
        var second = tracker.Update([In(1, AlertLevel.Warning)]);

        Assert.Single(first.NewAlerts);
        Assert.Empty(second.NewAlerts);
        Assert.Single(second.ActiveAlerts);
    }

    [Fact]
    public void Warning_to_time_up_is_a_new_alert()
    {
        var tracker = new AlertTracker();
        tracker.Update([In(1, AlertLevel.Warning)]);

        var update = tracker.Update([In(1, AlertLevel.TimeUp)]);

        Assert.Equal(AlertLevel.TimeUp, Assert.Single(update.NewAlerts).Level);
    }

    [Fact]
    public void Acknowledge_flags_the_alert_and_silences_repeats()
    {
        var tracker = new AlertTracker();
        tracker.Update([In(1, AlertLevel.Warning)]);

        tracker.Acknowledge(1);
        var update = tracker.Update([In(1, AlertLevel.Warning)]);

        Assert.Empty(update.NewAlerts);
        Assert.True(Assert.Single(update.ActiveAlerts).Acknowledged);
    }

    [Fact]
    public void Level_change_unacknowledges()
    {
        var tracker = new AlertTracker();
        tracker.Update([In(1, AlertLevel.Warning)]);
        tracker.Acknowledge(1);

        var update = tracker.Update([In(1, AlertLevel.TimeUp)]);

        Assert.Single(update.NewAlerts);
        Assert.False(Assert.Single(update.ActiveAlerts).Acknowledged);
    }

    [Fact]
    public void Clearing_and_reentering_beeps_again_and_unacknowledges()
    {
        var tracker = new AlertTracker();
        tracker.Update([In(1, AlertLevel.Warning)]);
        tracker.Acknowledge(1);

        var cleared = tracker.Update([In(1, AlertLevel.None)]);
        var again = tracker.Update([In(1, AlertLevel.Warning)]);

        Assert.Empty(cleared.ActiveAlerts);
        Assert.Single(again.NewAlerts);
        Assert.False(Assert.Single(again.ActiveAlerts).Acknowledged);
    }

    [Fact]
    public void Two_stations_entering_together_both_are_new_and_ordered_time_up_first_then_name()
    {
        var tracker = new AlertTracker();

        var update = tracker.Update([In(1, AlertLevel.Warning, "Alpha"), In(2, AlertLevel.TimeUp, "Zed"), In(3, AlertLevel.Warning, "Beta")]);

        Assert.Equal(3, update.NewAlerts.Count);
        Assert.Equal(["Zed", "Alpha", "Beta"], update.ActiveAlerts.Select(a => a.StationName));
    }

    [Fact]
    public void Empty_update_clears_everything()
    {
        var tracker = new AlertTracker();
        tracker.Update([In(1, AlertLevel.TimeUp)]);

        var update = tracker.Update([]);

        Assert.Empty(update.ActiveAlerts);
        Assert.Empty(update.NewAlerts);
    }
}
