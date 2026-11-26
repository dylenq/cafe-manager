using System.Collections.Concurrent;
using Cafe.Server.Data;
using Cafe.Server.Devices;

namespace Cafe.Tests.Infrastructure;

/// <summary>ITvDriver stub that records calls in order, with scripted results, a throw hook and an optional gate.</summary>
public sealed class FakeTvDriver : ITvDriver
{
    private readonly ConcurrentQueue<(int StationId, string Action)> _calls = new();
    private int _gated;

    public IReadOnlyList<(int StationId, string Action)> Calls => _calls.ToArray();

    public string[] Actions(int stationId) => _calls.Where(c => c.StationId == stationId).Select(c => c.Action).ToArray();

    /// <summary>Blocks the first PowerOn/PowerOff call until the source completes.</summary>
    public TaskCompletionSource? Gate { get; set; }

    /// <summary>Completes when the gated call has reached the driver (and is now waiting on <see cref="Gate"/>).</summary>
    public TaskCompletionSource GateEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Called before each action; throw from it to simulate a driver exception.</summary>
    public Func<int, string, Task>? Hook { get; set; }

    public Func<int, TvActionResult>? OffResult { get; set; }

    public Func<int, TvActionResult>? OnResult { get; set; }

    public Func<int, TvPowerState> State { get; set; } = _ => TvPowerState.On;

    public Func<int, Task>? StateHook { get; set; }

    public Task<TvPairResult> PairAsync(Station station, CancellationToken cancellationToken) =>
        Task.FromResult(new TvPairResult(true, "T", null));

    public Task<TvActionResult> PowerOffAsync(Station station, CancellationToken cancellationToken) =>
        RunAsync(station, "PowerOff", OffResult ?? (_ => new TvActionResult(TvActionOutcome.PoweredOff, TvPowerState.Standby, "TV powered off")), cancellationToken);

    public Task<TvActionResult> PowerOnAsync(Station station, CancellationToken cancellationToken) =>
        RunAsync(station, "PowerOn", OnResult ?? (_ => new TvActionResult(TvActionOutcome.PoweredOn, TvPowerState.On, "TV powered on")), cancellationToken);

    public async Task<TvPowerState> GetStateAsync(Station station, CancellationToken cancellationToken)
    {
        if (StateHook is { } hook)
        {
            await hook(station.Id).WaitAsync(cancellationToken);
        }

        return State(station.Id);
    }

    private async Task<TvActionResult> RunAsync(Station station, string action, Func<int, TvActionResult> result, CancellationToken ct)
    {
        _calls.Enqueue((station.Id, action));
        if (Gate is { } gate && Interlocked.Exchange(ref _gated, 1) == 0)
        {
            GateEntered.TrySetResult();
            await gate.Task.WaitAsync(ct);
        }

        if (Hook is { } hook)
        {
            await hook(station.Id, action);
        }

        return result(station.Id);
    }
}
