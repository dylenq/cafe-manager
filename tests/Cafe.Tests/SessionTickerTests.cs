using Cafe.Server.Data;
using Cafe.Server.Sessions;
using Cafe.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cafe.Tests;

public class SessionTickerTests : SessionTestBase
{
    private sealed class RecordingHandler : IExpiryActionHandler
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task ExecuteAsync(Station station, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.CompletedTask;
        }
    }

    /// <summary>Wraps an evaluator, counts completed evaluations and can be told to throw.</summary>
    private sealed class CountingEvaluator(IExpiryEvaluator inner) : IExpiryEvaluator
    {
        private int _completed;

        public int Completed => Volatile.Read(ref _completed);

        public int FailFirst { get; set; }

        public async Task EvaluateAsync(CancellationToken cancellationToken)
        {
            try
            {
                if (Completed < FailFirst)
                {
                    throw new InvalidOperationException("evaluator failed");
                }

                await inner.EvaluateAsync(cancellationToken);
            }
            finally
            {
                Interlocked.Increment(ref _completed);
            }
        }
    }

    private readonly RecordingHandler _handler = new();

    private CountingEvaluator Counting()
    {
        var sp = Factory.Services;
        var real = new ExpiryEvaluator(
            sp.GetRequiredService<IDbContextFactory<CafeDbContext>>(),
            Factory.Clock,
            sp.GetRequiredService<SessionSettingsService>(),
            sp.GetRequiredService<SessionNotifier>(),
            [_handler],
            NullLogger<ExpiryEvaluator>.Instance);
        return new CountingEvaluator(real);
    }

    private SessionTicker Ticker(IExpiryEvaluator evaluator) =>
        new(evaluator, Factory.Clock, NullLogger<SessionTicker>.Instance);

    private static async Task WaitForAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail($"Timed out after 5 real seconds waiting for: {what}");
            }

            await Task.Delay(5);
        }
    }

    /// <summary>Advances the fake clock one second and waits until the ticker finished the evaluation that tick caused.</summary>
    private async Task StepAsync(CountingEvaluator evaluator)
    {
        var before = evaluator.Completed;
        Advance(TimeSpan.FromSeconds(1));
        await WaitForAsync(() => evaluator.Completed > before, "an evaluation after advancing the fake clock");
    }

    [Fact]
    public async Task Start_evaluates_once_immediately_and_recovers_a_session_left_in_grace()
    {
        var session = await PrepaidAsync(await StationAsync(), 30);
        await UpdateAsync(session.Id, s =>
        {
            s.Status = SessionStatus.Grace;
            s.GraceStartedAtUtc = Start.AddMinutes(30);
            foreach (var seg in s.Segments)
            {
                seg.EndUtc = Start.AddMinutes(30);
            }
        });
        Advance(TimeSpan.FromHours(2));
        var evaluator = Counting();
        var ticker = Ticker(evaluator);

        await ticker.StartAsync(CancellationToken.None);
        try
        {
            await WaitForAsync(() => _handler.Calls == 1, "the handler to be called by the startup evaluation");
            Assert.Equal(SessionStatus.Ended, (await LoadAsync(session.Id)).Status);
            Assert.Equal(1, evaluator.Completed);
        }
        finally
        {
            await ticker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task One_second_steps_drive_a_session_through_grace_to_ended()
    {
        var session = await PrepaidAsync(await StationAsync(), 2);
        var evaluator = Counting();
        var ticker = Ticker(evaluator);
        await ticker.StartAsync(CancellationToken.None);
        try
        {
            await WaitForAsync(() => evaluator.Completed >= 1, "the startup evaluation");

            for (var i = 0; i < 125; i++)
            {
                await StepAsync(evaluator);
            }

            Assert.Equal(SessionStatus.Grace, (await LoadAsync(session.Id)).Status);
            Assert.Equal(0, _handler.Calls);

            for (var i = 0; i < 120; i++)
            {
                await StepAsync(evaluator);
            }

            var saved = await LoadAsync(session.Id);
            Assert.Equal(SessionStatus.Ended, saved.Status);
            Assert.Equal(SessionEndReason.Expired, saved.EndReason);
            Assert.Equal(1, _handler.Calls);
        }
        finally
        {
            await ticker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task An_evaluator_exception_does_not_stop_the_loop()
    {
        var evaluator = Counting();
        evaluator.FailFirst = 2;
        var ticker = Ticker(evaluator);
        await ticker.StartAsync(CancellationToken.None);
        try
        {
            await WaitForAsync(() => evaluator.Completed >= 1, "the failing startup evaluation");
            await StepAsync(evaluator);
            await StepAsync(evaluator);
            await StepAsync(evaluator);

            Assert.True(evaluator.Completed >= 4);
        }
        finally
        {
            await ticker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Stop_cancels_cleanly_and_no_further_evaluations_run()
    {
        var evaluator = Counting();
        var ticker = Ticker(evaluator);
        await ticker.StartAsync(CancellationToken.None);
        await WaitForAsync(() => evaluator.Completed >= 1, "the startup evaluation");

        await ticker.StopAsync(CancellationToken.None);
        var after = evaluator.Completed;
        Advance(TimeSpan.FromSeconds(30));
        await Task.Delay(100);

        Assert.Equal(after, evaluator.Completed);
    }

    [Fact]
    public void The_ticker_is_enabled_by_default_and_the_flag_can_disable_it()
    {
        Assert.True(SessionTicker.IsEnabled(new ConfigurationBuilder().Build()));
        var off = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Sessions:RunTicker"] = "false" }).Build();
        Assert.False(SessionTicker.IsEnabled(off));
    }

    [Fact]
    public void The_test_host_does_not_run_the_ticker_but_a_host_with_the_flag_on_does()
    {
        Assert.DoesNotContain(Factory.Services.GetServices<IHostedService>(), h => h is SessionTicker);

        using var enabled = new CafeWebApplicationFactory();
        enabled.ExtraSettings["Sessions:RunTicker"] = "true";
        Assert.Single(enabled.Services.GetServices<IHostedService>().OfType<SessionTicker>());
    }
}
