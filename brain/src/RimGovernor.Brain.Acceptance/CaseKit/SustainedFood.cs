using System.Diagnostics;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

// The watch mechanics behind the serve-driven registry cases and the colony generator (facility/*, farm/*, acceptance colony):
// on a session the runner opened, start the in-mod player service with the routine families
// under test, acquire player authority, and sample a goal's durable state over a tick-measured window (WatchAsync: a
// tick cadence, woken early by the service's own journal), then stop the service and audit against live native facts.

/// <summary>
/// The observation window <see cref="SustainedFood.WatchAsync"/> samples on a started service: the goal, how long and
/// how often.
/// </summary>
public sealed record WatchConfig
{
    /// <summary>
    /// The wall-clock length of the observation window. <see cref="Window"/>, when above zero, is the same window
    /// measured in game ticks: the watch ends as soon as the live game tick has advanced <see cref="Window"/> ticks
    /// past the first sampled tick, and <see cref="Watch"/> becomes the ceiling that ends it regardless (a paused or
    /// starved game never advances). A tick window ties the sample length to what the assertion needs instead of a
    /// flat diagnostic length: 2500 ticks is one in-game hour, 60000 one day.
    /// </summary>
    public TimeSpan Watch { get; init; }

    public ulong Window { get; init; }

    /// <summary>
    /// The sample cadence in game ticks: the next sample is taken as soon as the live tick has advanced this far past
    /// the previous one (probed every <see cref="SustainedFood.RunInterval"/>), so the cadence follows the game's
    /// speed instead of the wall clock (default <see cref="SustainedFood.DefaultPollTicks"/>).
    /// </summary>
    public ulong PollTicks { get; init; }

    /// <summary>The wall-clock ceiling between samples (default <see cref="SustainedFood.DefaultPoll"/>): a paused or starved game is still sampled, so its stall is visible in the timeline.</summary>
    public TimeSpan Poll { get; init; }

    /// <summary>
    /// When a service journal row satisfies it, takes a sample at once instead of waiting for the cadence: the
    /// service's OTLP log ring is tailed between samples. Default <see cref="RowKinds.WorkerOutcome"/>, the row an
    /// action's outcome changes on.
    /// </summary>
    public Func<LogRow, bool>? Wake { get; init; }

    /// <summary>The maintained Concern or Need the timeline samples (default the food/supply Need).</summary>
    public string Concern { get; init; } = "";

    /// <summary>Further goals each sample also reads, under the goal id.</summary>
    public IReadOnlyList<string> Extra { get; init; } = [];

    /// <summary>Live reads each sample also records under their key, such as the colony review's housing census (#3751).</summary>
    public IReadOnlyList<WatchProbe> Probes { get; init; } = [];

    /// <summary>
    /// Ends the window with the clock's hold reason once the live game tick advanced slower than this many ticks per
    /// second over a stall budget of watch time (<see cref="ClockRate"/>; checkpoint captures do not count). A held,
    /// starved or dead game (a relaunch sits at the main menu with no map) otherwise waits out the whole Watch ceiling.
    /// </summary>
    public double MinTicksPerSecond { get; init; } = ClockRate.DefaultFloor;
}

/// <summary>
/// One live read a watch sample records under <paramref name="Key"/>: Read's reply, or {error} when it throws an
/// <see cref="AcceptanceException"/>.
/// </summary>
public sealed record WatchProbe(string Key, Func<CancellationToken, Task<JsonObject>> Read);

/// <summary>
/// One serve-driven case's shape over the watch: Prepare runs against the loaded game before the service starts (the
/// place to record a live baseline), the service declared on the case is started and watched, then stopped, and Audit
/// runs against the reattached session so the durable journal can be compared with live native facts. Spec, when set,
/// edits the case's declared serve spec before launch.
/// </summary>
public sealed record SustainedObservation
{
    public WatchConfig Watch { get; init; } = new();

    public Func<ServeSpec, ServeSpec>? Spec { get; init; }

    public Func<Harness, Report, CancellationToken, Task>? Prepare { get; init; }

    public Func<Harness, Report, CancellationToken, Task>? Audit { get; init; }
}

public static class SustainedFood
{
    /// <summary>The probe interval of the live tick and the journal between two samples.</summary>
    public static readonly TimeSpan RunInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>The sample cadence of an unset <see cref="WatchConfig.PollTicks"/>: 600 ticks (a quarter of an in-game hour; about 1.7 s at Superfast, 0.7 s at Ultrafast, 0.3 s at Hyperfast).</summary>
    public const ulong DefaultPollTicks = 600;

    /// <summary>The wall-clock ceiling between samples of an unset <see cref="WatchConfig.Poll"/>.</summary>
    public static readonly TimeSpan DefaultPoll = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The serve-driven family's observation window on a service just started (not yet acquired): it acquires player
    /// authority and keeps it granted, confirms the scheduler reaches automate with a persisted rounds, then samples
    /// the goal's durable state at the cadence <paramref name="cfg"/> sets (PollTicks, Poll, Wake) for Watch with the
    /// step-stall check from the service's spec. Every sample also carries the service's live colony census
    /// (<see cref="SampleColonyAsync"/>) so starvation is visible in the timeline itself. It records timeline, events
    /// and timeline_samples on <paramref name="report"/> (also when it fails) and appends every sample to
    /// <paramref name="timeline"/>; the caller stops the service.
    /// </summary>
    internal static async Task WatchAsync(ServiceProcess service, Config config, WatchConfig cfg, Report report, ICollection<WatchSample> timeline, Func<CancellationToken, Task<string>> clock, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(cfg);
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(timeline);

        var poll = cfg.Poll > TimeSpan.Zero ? cfg.Poll : DefaultPoll;
        var pollTicks = cfg.PollTicks == 0 ? DefaultPollTicks : cfg.PollTicks;
        var wake = cfg.Wake ?? RowKinds.WorkerOutcome;
        ServiceApiCall apiCall = service.ApiAsync;

        // Resume needs no anchor plan: authority is the world's own root plan, created on first resume (SIMP02).
        await service.AcquireAsync(cancellationToken).ConfigureAwait(false);

        // The acquire reply never carried a resumed entry; the report keeps the key it always wrote.
        report["acquired"] = null;
        report["window_ms"] = (long)cfg.Watch.TotalMilliseconds;
        var window = new TickWindow(cfg.Window);
        report["window"] = window.ToJson();

        var verifyStore = await CaseStore.OpenAsync(config, cancellationToken).ConfigureAwait(false);

        // Keeps player authority granted for the full watch window.
        service.KeepAuthority(cancellationToken);

        // Confirm the scheduler actually reaches automate and a ReviewState pass gets persisted before starting the real
        // observation window.
        var diagDeadline = DateTimeOffset.UtcNow.AddSeconds(60);
        var sawAutomate = false;
        ulong revision = 0;
        using (var pace = Wait.Pace(RunInterval))
        {
            while (DateTimeOffset.UtcNow < diagDeadline)
            {
                if (await LiveStateAsync(apiCall, cancellationToken).ConfigureAwait(false) is { Mode: "automate" })
                {
                    sawAutomate = true;
                }

                try
                {
                    revision = await verifyStore.RevisionAsync(cancellationToken).ConfigureAwait(false);
                    if (revision > 0)
                    {
                        break;
                    }
                }
                catch (AcceptanceException)
                {
                    // Not persisted yet.
                }

                await pace.NextAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        if (!sawAutomate)
        {
            throw new AcceptanceException("service never reached automate mode after acquire");
        }

        if (revision == 0)
        {
            throw new AcceptanceException("service reached automate mode but the ReviewState pass was never persisted");
        }

        // The observation window: sample the goal's state and the stage of every one of its committed methods' plans,
        // at Poll intervals, for Watch wall-clock duration. Every sample is retained (report["timeline"]); "events"
        // additionally isolates the moments that actually changed (a method count change) so the failure mode is
        // readable without wading through every sample.
        var events = new List<WatchSample>();
        var lastMethodCount = -1;
        var concernId = cfg.Concern.Length > 0 ? cfg.Concern : "food/supply";
        var watchStarted = DateTimeOffset.UtcNow;
        var watchDeadline = watchStarted + cfg.Watch;
        var stepped = false;
        var tail = LogTail.Create(service.LogPath);
        var watchClock = Stopwatch.StartNew();
        var rate = cfg.MinTicksPerSecond > 0 ? new ClockRate(cfg.MinTicksPerSecond, Wait.StallBudget()) : null;
        var wakes = 0;
        var tickPolls = 0;
        var wallPolls = 0;
        try
        {
            while (DateTimeOffset.UtcNow < watchDeadline)
            {
                NeedSample? need = null;
                string? error = null;
                try
                {
                    need = await verifyStore.SampleNeedAsync(concernId, cancellationToken).ConfigureAwait(false);
                }
                catch (AcceptanceException e)
                {
                    error = e.Message;
                }

                var errorAt = WatchSample.Stamp(DateTimeOffset.UtcNow);
                if (!stepped)
                {
                    stepped = await service.StepAdmittedAsync(watchStarted, cancellationToken).ConfigureAwait(false);
                }

                var extras = new List<(string Goal, NeedSample? Sample, string? Error)>();
                foreach (var extra in cfg.Extra)
                {
                    try
                    {
                        extras.Add((extra, await verifyStore.SampleNeedAsync(extra, cancellationToken).ConfigureAwait(false), null));
                    }
                    catch (AcceptanceException e)
                    {
                        extras.Add((extra, null, e.Message));
                    }
                }

                var probes = new List<(string Key, JsonObject Value)>();
                foreach (var probe in cfg.Probes)
                {
                    try
                    {
                        probes.Add((probe.Key, await probe.Read(cancellationToken).ConfigureAwait(false)));
                    }
                    catch (AcceptanceException e)
                    {
                        probes.Add((probe.Key, new JsonObject { { "error", e.Message } }));
                    }
                }

                var liveTick = await LiveTickAsync(apiCall, cancellationToken).ConfigureAwait(false);
                if (liveTick is { } tick)
                {
                    window.Observe(tick);
                }

                var sample = new WatchSample
                {
                    Need = need,
                    Error = error,
                    ErrorAt = errorAt,
                    Concern = concernId,
                    Extras = extras,
                    Probes = probes,
                    Tick = liveTick,
                    Colony = await SustainedColonySample.SampleColonyAsync(apiCall, cancellationToken).ConfigureAwait(false),
                };
                timeline.Add(sample);
                if (rate?.Observe(liveTick, watchClock.Elapsed) is { } slow)
                {
                    var stalled = new AcceptanceException("clock too slow: " + slow + "; " + await clock(cancellationToken).ConfigureAwait(false));
                    report["fail_fast"] = new JsonObject { { "kind", "clock_slow" }, { "error", stalled.Message }, { "colony", sample.Colony.ToJson() } };
                    throw stalled;
                }

                if (sample.MethodCount != lastMethodCount)
                {
                    events.Add(sample);
                    lastMethodCount = sample.MethodCount;
                }

                if (window.HasReached())
                {
                    break;
                }

                // Between samples is a natural pause for the runner's checkpoint ring; a capture's own time does not
                // count against Watch.
                var took = await Checkpoints.PauseAsync(cancellationToken).ConfigureAwait(false);
                if (took > TimeSpan.Zero)
                {
                    watchDeadline += took;
                    rate?.Shift(took);
                }

                switch (await WaitNextSampleAsync(apiCall, tail, wake, pollTicks, poll, liveTick ?? 0, watchDeadline, cancellationToken).ConfigureAwait(false))
                {
                    case NextSample.Wake:
                        wakes++;
                        break;
                    case NextSample.TickPoll:
                        tickPolls++;
                        break;
                    default:
                        wallPolls++;
                        break;
                }
            }

            window.WatchWall = FormatWall(DateTimeOffset.UtcNow - watchStarted);
            window.Reached = window.HasReached();
        }
        finally
        {
            report["timeline"] = OutputJson.Array(timeline, sample => sample.ToJson());
            report["events"] = OutputJson.Array(events, sample => sample.ToJson());
            report["timeline_samples"] = timeline.Count;
            report["window"] = window.ToJson();
            report["cadence"] = new JsonObject
            {
                { "poll_ticks", pollTicks },
                { "poll_ms", (long)poll.TotalMilliseconds },
                { "wakes", wakes },
                { "tick_polls", tickPolls },
                { "wall_polls", wallPolls },
            };
        }
    }

    // Whole seconds as duration text: "1m30s".
    private static string FormatWall(TimeSpan elapsed)
    {
        var rounded = TimeSpan.FromSeconds(Math.Round(elapsed.TotalSeconds, MidpointRounding.AwayFromZero));
        return WaitError.FormatDuration(rounded);
    }

    // Which of the cadence's conditions ended a pause between two samples (the report's cadence counters).
    private enum NextSample
    {
        Wake,
        TickPoll,
        WallPoll,
    }

    // Pauses between two samples until one of the cadence's conditions holds and names it for the report: a journal row
    // satisfying Wake, PollTicks of game time past the previous sample's tick, or the Poll wall-clock ceiling. The live
    // tick and the journal are probed every RunInterval. The watch deadline ends the pause so the loop's own check sees it.
    private static async Task<NextSample> WaitNextSampleAsync(ServiceApiCall apiCall, LogTail tail, Func<LogRow, bool> wake, ulong pollTicks, TimeSpan poll, ulong sampledTick, DateTimeOffset watchDeadline, CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        using var pace = Wait.Pace(RunInterval);
        while (true)
        {
            if (watchDeadline <= DateTimeOffset.UtcNow)
            {
                return NextSample.WallPoll;
            }

            await pace.NextAsync(cancellationToken).ConfigureAwait(false);
            foreach (var row in tail.Next())
            {
                if (wake(row))
                {
                    return NextSample.Wake;
                }
            }

            if (started.Elapsed >= poll)
            {
                return NextSample.WallPoll;
            }

            if (await LiveTickAsync(apiCall, cancellationToken).ConfigureAwait(false) is { } tick && sampledTick > 0 && tick >= sampledTick + pollTicks)
            {
                return NextSample.TickPoll;
            }
        }
    }

    /// <summary>Reads the service's live game tick from /api/state; null when the snapshot has none (not connected yet, or a stale read).</summary>
    public static async Task<ulong?> LiveTickAsync(ServiceApiCall apiCall, CancellationToken cancellationToken) =>
        await LiveStateAsync(apiCall, cancellationToken).ConfigureAwait(false) is { Game.Tick: long tick and >= 0 } ? (ulong)tick : null;

    // The service's /api/state; null when it did not answer 200 or answered another shape.
    private static async Task<ServiceState?> LiveStateAsync(ServiceApiCall apiCall, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(apiCall);
        try
        {
            var reply = await apiCall("GET", "/api/state", null, "", cancellationToken).ConfigureAwait(false);
            return reply.Status == 200 ? ServiceJson.State(reply.Body) : null;
        }
        catch (AcceptanceException)
        {
            return null;
        }
    }

    /// <summary>
    /// Runs one <paramref name="o"/> on <paramref name="session"/>; the service is stopped and the session reattached
    /// before Audit, on every path.
    /// </summary>
    public static async Task ObserveAsync(ObservedSession session, SustainedObservation o, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(o);
        var report = session.Report;
        if (o.Prepare is not null)
        {
            try
            {
                await o.Prepare(session.Harness, report, cancellationToken).ConfigureAwait(false);
            }
            catch (AcceptanceException e)
            {
                throw new AcceptanceException($"prepare: {e.Message}", e);
            }
        }

        var spec = session.Spec;
        if (o.Spec is not null)
        {
            spec = o.Spec(spec);
        }

        var service = await session.ServeAsync(spec, cancellationToken).ConfigureAwait(false);
        var stopped = false;
        async Task StopServiceAsync()
        {
            if (stopped)
            {
                return;
            }

            stopped = true;
            if (await service.StopAsync().ConfigureAwait(false) is { } keep)
            {
                report["authority_reacquisitions"] = keep;
            }
        }

        var timeline = new List<WatchSample>();
        try
        {
            try
            {
                await WatchAsync(service, session.Config, o.Watch, report, timeline, ct => ClockRate.DescribeAsync(session.Harness, session.Identity, ct), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                report["metrics"] = SustainedMetricsDerivation.DeriveMetrics(timeline).ToJson();
                report["colony_outcome"] = SustainedColonySample.DeriveColonyOutcome(timeline).ToJson();
            }

            await StopServiceAsync().ConfigureAwait(false);
            var harness = await session.ReattachAsync(cancellationToken).ConfigureAwait(false);
            if (o.Audit is not null)
            {
                await o.Audit(harness, report, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await StopServiceAsync().ConfigureAwait(false);
        }
    }
}
