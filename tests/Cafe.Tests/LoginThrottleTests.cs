using System.Net;
using Cafe.Server.Auth;
using Cafe.Tests.Infrastructure;

namespace Cafe.Tests;

public class LoginThrottleTests
{
    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    [Fact]
    public void Locks_after_five_failures_and_unlocks_after_five_minutes()
    {
        var time = new ManualTime();
        var throttle = new LoginThrottle(time);
        for (var i = 0; i < 4; i++)
        {
            throttle.RecordFailure("a");
        }

        Assert.False(throttle.IsLockedOut("a", out _));
        throttle.RecordFailure("a");
        Assert.True(throttle.IsLockedOut("a", out var remaining));
        Assert.Equal(LoginThrottle.Lockout, remaining);
        Assert.False(throttle.IsLockedOut("b", out _));

        time.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        Assert.False(throttle.IsLockedOut("a", out _));
    }

    [Fact]
    public void Failures_outside_the_window_do_not_accumulate()
    {
        var time = new ManualTime();
        var throttle = new LoginThrottle(time);
        for (var i = 0; i < 4; i++)
        {
            throttle.RecordFailure("a");
        }

        time.Advance(TimeSpan.FromMinutes(6));
        throttle.RecordFailure("a");

        Assert.False(throttle.IsLockedOut("a", out _));
    }

    [Fact]
    public void Success_reset_clears_the_counter()
    {
        var throttle = new LoginThrottle(new ManualTime());
        for (var i = 0; i < 4; i++)
        {
            throttle.RecordFailure("a");
        }

        throttle.Reset("a");
        throttle.RecordFailure("a");

        Assert.False(throttle.IsLockedOut("a", out _));
    }

    [Fact]
    public async Task Login_is_refused_after_five_wrong_passwords_even_with_the_right_one()
    {
        using var factory = new CafeWebApplicationFactory();
        using var _ = await factory.CreateAuthenticatedClientAsync();
        using var client = factory.CreateNoRedirectClient();

        for (var i = 0; i < 5; i++)
        {
            await FormPostHelper.PostFormAsync(client, "/login", new Dictionary<string, string> { ["Password"] = "wrong-password" });
        }

        var response = await FormPostHelper.PostFormAsync(client, "/login",
            new Dictionary<string, string> { ["Password"] = AuthTestExtensions.TestPassword });
        var body = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Too many failed attempts", body);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }
}
