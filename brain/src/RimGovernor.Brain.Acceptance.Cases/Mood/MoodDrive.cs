using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// One serve-driven mood case's service across native fixture steps (Go <c>drive</c>): a native step stops the service,
/// takes the harness back, pauses the game and changes it, and the next start serves again from the same game. The Go
/// service needed the game's sole bridge slot; here the stop also parks the in-process brain (see
/// <see cref="BrainRun.StopAsync"/>) so nothing drives the clock during the native step.
/// </summary>
internal sealed class MoodDrive(ISession session)
{
    private ServiceProcess? _service;
    private BrainRun? _run;
    private IWatchStore? _journal;

    /// <summary>The family under test plus the tend and rescue pair that keeps a food-poisoning down from freezing the clock (#2532).</summary>
    internal static IReadOnlyList<string> EpicFamilies(string family) => [family, "tend", "rescue"];

    /// <summary>The service of the latest <see cref="StartAsync"/>.</summary>
    internal ServiceProcess Service => _service ?? throw new AcceptanceException("the mood drive has no service yet");

    /// <summary>Serves the case's spec (the first time, or again after a native step) and acquires authority.</summary>
    internal async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_service is null)
        {
            _service = await session.ServeAsync(session.Spec, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            session.Release();
            _service = await _service.RestartAsync(cancellationToken).ConfigureAwait(false);
        }

        _ = await _service.AcquireAsync(cancellationToken).ConfigureAwait(false);
        _service.KeepAuthority(cancellationToken);
        _run = BrainRun.Wrap(session, _service, session.Spec.Prefix);
    }

    /// <summary>
    /// The durable-journal reader for this start (Go <c>service.Store</c>), opened on first use through
    /// <see cref="ObservedSession.StoreOpener"/> and closed at the next native step or stop.
    /// </summary>
    internal async Task<IWatchStore> JournalAsync(CancellationToken cancellationToken) =>
        _journal ??= await ObservedSession.OpenStoreAsync(session.Config, cancellationToken).ConfigureAwait(false);

    /// <summary>Stops the service, takes the harness back and pauses the game so the fixture op reads and writes a still world.</summary>
    internal async Task<Harness> NativeAsync(string label, CancellationToken cancellationToken)
    {
        await StopAsync(cancellationToken).ConfigureAwait(false);
        var h = session.Reattach();
        await NativeReads.PauseAsync(h, label + "-pause", cancellationToken).ConfigureAwait(false);
        return h;
    }

    /// <summary>Stops the service and parks the brain (idempotent).</summary>
    internal async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_journal is IDisposable disposable)
        {
            disposable.Dispose();
        }

        _journal = null;
        if (_run is not null)
        {
            _ = await _run.StopAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Polls <paramref name="probe"/> until it reports done, bounded by a ceiling and a stall.</summary>
    internal Task WaitAsync(TimeSpan ceiling, Probe probe, CancellationToken cancellationToken) =>
        new Wait { Ceiling = ceiling, Stall = TimeSpan.FromMinutes(2), Interval = TimeSpan.FromSeconds(1), Terminal = Service.Exited }.WaitProgressAsync(probe, cancellationToken);

    /// <summary>Runs a test op and requires its success flag (Go <c>fixtureCall</c>).</summary>
    internal static async Task<JsonObject> FixtureCallAsync(Harness h, string label, string op, JsonObject? args, CancellationToken cancellationToken)
    {
        var reply = await h.CallAsync(label, op, args, cancellationToken).ConfigureAwait(false);
        if (reply["success"].AsBool() != true)
        {
            throw new AcceptanceException($"{label}: {op} refused: {reply.ToJson()}");
        }

        return reply;
    }

    /// <summary>Whether two readings agree to within <paramref name="tolerance"/>.</summary>
    internal static bool Near(double a, double b, double tolerance) => Math.Abs(a - b) <= tolerance;
}
