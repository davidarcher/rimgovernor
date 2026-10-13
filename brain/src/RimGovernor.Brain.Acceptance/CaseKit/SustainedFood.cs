using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

// The watch mechanics behind the serve-driven registry cases (sustained/food, sustained/matrix-*, facility/*, farm/*,
// medical/stable-patient): on a session the runner opened, start the in-mod player service with the routine families
// under test, acquire player authority, and sample a goal's durable state over a tick-measured window (WatchAsync: a
// tick cadence, woken early by the service's own journal), then stop the service and audit against live native facts
// (Go package sustainedfood).

/// <summary>
/// A phase boundary the watch checkpoints at: the first sample satisfying <see cref="When"/> saves the live game as
/// <see cref="Name"/> (pause, save, resume through the service), copies the save into root/profile/Saves for the
/// commit flow (tools/*-checkpoint) and, under the runner's checkpoint ring, takes a full bundle labelled
/// <see cref="Name"/> a rerun can resume from (Go <c>sustainedfood.Checkpoint</c>).
/// </summary>
public sealed record WatchCheckpoint(string Name, Func<JsonObject, bool> When);

/// <summary>
/// The observation window <see cref="SustainedFood.WatchAsync"/> samples on a started service: the goal, how long and
/// how often, an early exit and a checkpoint (Go <c>sustainedfood.WatchConfig</c>).
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
    /// action's outcome changes on. <see cref="Until"/>, <see cref="FailFast"/> and the checkpoints all see the woken sample.
    /// </summary>
    public Func<LogRow, bool>? Wake { get; init; }

    /// <summary>The maintained Concern the timeline samples (default EnsureFoodSupply).</summary>
    public string Concern { get; init; } = "";

    /// <summary>Further goals each sample also reads, under the goal id.</summary>
    public IReadOnlyList<string> Extra { get; init; } = [];

    /// <summary>Ends the window early once a sample satisfies it.</summary>
    public Func<JsonObject, bool>? Until { get; init; }

    /// <summary>Saves the live game the first time a sample satisfies When (see <see cref="WatchCheckpoint"/>); <see cref="Checkpoints"/> are further phase boundaries, each taken once. The report's checkpoint row is the first's; checkpoints lists them all.</summary>
    public WatchCheckpoint? Checkpoint { get; init; }

    public IReadOnlyList<WatchCheckpoint> Checkpoints { get; init; } = [];

    /// <summary>Ends the window early with the journal's refusal text once the case cannot pass (see <see cref="RimGovernor.Brain.Acceptance.FailFast"/>); on by default.</summary>
    public FailFast FailFast { get; init; } = new();

    /// <summary>
    /// Ends the window with an error once the live game tick has not advanced for this long of watch time (default
    /// <see cref="SustainedFood.DefaultTickStall"/>; checkpoint saves do not count). A game that died and was
    /// relaunched sits at the main menu with no map, so without this the case waits out its whole Watch ceiling.
    /// </summary>
    public TimeSpan TickStall { get; init; }

    /// <summary>
    /// Ends the window (without error) once the scheduler has refused a clock window for no_work and the tick has not
    /// moved for this long (default <see cref="SustainedFood.DefaultIdleGrace"/>; negative disables): a parked game
    /// cannot change, so waiting out Watch only burns wall time.
    /// </summary>
    public TimeSpan IdleGrace { get; init; }
}

/// <summary>
/// The durable-state reads a watch makes of the service it observes (Go <c>*store.Store</c>: LoadRounds, LoadStandard
/// and the plan loaders). The brain's store lives with the in-mod service, so the slice that owns the service launch
/// supplies the reader; this project declares only the narrow surface the watch needs.
/// </summary>
public interface IWatchStore
{
    /// <summary>The revision of the persisted Rounds record; zero when none was persisted yet. Throws <see cref="AcceptanceException"/> when it cannot be read.</summary>
    Task<ulong> RoundsRevisionAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Reads one maintained goal's current binding (if any) and its committed methods' plan stages, mirroring exactly
    /// what a routine planner's step itself reads (Go <c>SampleStandard</c>): <c>at</c>, <c>method_count</c>,
    /// <c>review_revision</c>, <c>review_tick</c>, <c>blocked</c>, <c>latch_food</c>, <c>emergency</c> (array of
    /// strings), <c>concern_bound</c>, <c>status</c>, <c>need</c>, <c>priority</c>, <c>vetoed</c>, <c>epoch</c>,
    /// <c>plans</c> and <c>retired_plans</c> (each <c>{plan, method, actions, stages, kinds, unsuccessful}</c>, an
    /// unsuccessful row <c>{action, kind, reason}</c>), or the incident shape for an incident kind. Throws
    /// <see cref="AcceptanceException"/> on a read failure.
    /// </summary>
    Task<JsonObject> SampleStandardAsync(string concern, CancellationToken cancellationToken);
}

/// <summary>
/// What a watch needs from the running service (Go <c>*na.ServiceProcess</c>), on top of what a checkpoint capture
/// needs. The brain is in-process in the mod, so there is no child process: an implementation talks to the player API
/// over HTTP.
/// </summary>
public interface IWatchedService : ICheckpointService
{
    /// <summary>The service's OTLP log ring (otlp/logs.jsonl), tailed for wakes and read for the step failures.</summary>
    string LogPath { get; }

    /// <summary>Acquires player authority; the reply describes the entry (its <c>resumed</c> member goes to the report).</summary>
    Task<JsonObject> AcquireAsync(CancellationToken cancellationToken);

    /// <summary>The store the watch samples.</summary>
    Task<IWatchStore> StoreAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Keeps player authority granted for the whole watch: native authority is a bounded generation that legitimately
    /// lapses (tick budget exhaustion, an unrecognized native clock event), and nothing re-acquires it automatically.
    /// </summary>
    void KeepAuthority(CancellationToken cancellationToken);

    /// <summary>
    /// Whether a scheduler step has admitted a clock window since <paramref name="since"/>; throws
    /// <see cref="StepStallException"/> once the service's step-stall budget elapsed without one.
    /// </summary>
    Task<bool> StepAdmittedAsync(DateTimeOffset since, CancellationToken cancellationToken);

    /// <summary>Stops the service; returns the authority re-acquisition record, null when there is none to report.</summary>
    Task<JsonObject?> StopAsync();
}

/// <summary>The slice of a registry case's session <see cref="SustainedFood.ObserveAsync"/> drives: the loaded, paused, frozen game the shared runner opened (Go <c>sustainedfood.Observed</c>).</summary>
public interface IObservedSession
{
    Harness Harness { get; }

    Config Config { get; }

    Report Report { get; }

    ServeSpec Spec { get; }

    /// <summary>Starts the in-mod service under <paramref name="spec"/>.</summary>
    Task<IWatchedService> ServeAsync(ServeSpec spec, CancellationToken cancellationToken);

    /// <summary>Re-opens the harness after the service stopped (trivial in the in-process brain: there is no sole bridge slot to give back).</summary>
    Task<Harness> ReattachAsync(CancellationToken cancellationToken);
}

/// <summary>
/// One serve-driven case's shape over the watch: Prepare runs against the loaded game before the service starts (the
/// place to record a live baseline), the service declared on the case is started and watched, then stopped, and Audit
/// runs against the reattached session so the durable journal can be compared with live native facts. Spec, when set,
/// edits the case's declared serve spec before launch (Go <c>sustainedfood.Observation</c>).
/// </summary>
public sealed record SustainedObservation
{
    public WatchConfig Watch { get; init; } = new();

    public Func<ServeSpec, ServeSpec>? Spec { get; init; }

    public Func<Harness, Report, CancellationToken, Task>? Prepare { get; init; }

    public Func<Harness, Report, CancellationToken, Task>? Audit { get; init; }
}

public static partial class SustainedFood
{
    /// <summary>The probe interval of the live tick and the journal between two samples (Go <c>na.RunInterval</c>).</summary>
    public static readonly TimeSpan RunInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>The sample cadence of an unset <see cref="WatchConfig.PollTicks"/>: 600 ticks (a quarter of an in-game hour; about 1.7 s at Superfast, 0.7 s at Ultrafast, the probe interval once boosted).</summary>
    public const ulong DefaultPollTicks = 600;

    /// <summary>The wall-clock ceiling between samples of an unset <see cref="WatchConfig.Poll"/>.</summary>
    public static readonly TimeSpan DefaultPoll = TimeSpan.FromSeconds(5);

    public static readonly TimeSpan DefaultTickStall = TimeSpan.FromMinutes(3);

    public static readonly TimeSpan DefaultIdleGrace = TimeSpan.FromSeconds(30);

    private static string Now() => SampleJson.Stamp(DateTimeOffset.UtcNow);

    /// <summary>
    /// The serve-driven family's observation window on a service just started (not yet acquired): it acquires player
    /// authority and keeps it granted, confirms the scheduler reaches automate with a persisted rounds, then samples
    /// the goal's durable state at the cadence <paramref name="cfg"/> sets (PollTicks, Poll, Wake) for Watch (or until
    /// Until) with the step-stall check from the service's spec. Every sample also carries the service's live colony
    /// census (<see cref="SampleColonyAsync"/>) so starvation is visible in the timeline itself. It records timeline,
    /// events and timeline_samples on <paramref name="report"/> (also when it fails) and appends every sample to
    /// <paramref name="timeline"/>; the caller stops the service.
    /// </summary>
    public static async Task WatchAsync(Config naCfg, IWatchedService service, WatchConfig cfg, Report report, ICollection<JsonObject> timeline, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(naCfg);
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(cfg);
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(timeline);

        var poll = cfg.Poll > TimeSpan.Zero ? cfg.Poll : DefaultPoll;
        var pollTicks = cfg.PollTicks == 0 ? DefaultPollTicks : cfg.PollTicks;
        var tickStallLimit = cfg.TickStall > TimeSpan.Zero ? cfg.TickStall : DefaultTickStall;
        var idleGrace = cfg.IdleGrace == TimeSpan.Zero ? DefaultIdleGrace : cfg.IdleGrace;
        var wake = cfg.Wake ?? RowKinds.WorkerOutcome;
        ServiceApiCall apiCall = service.ApiAsync;

        // Resume needs no anchor plan: authority is the world's own root plan, created on first resume (SIMP02).
        var entry = await service.AcquireAsync(cancellationToken).ConfigureAwait(false);
        report["acquired"] = entry["resumed"]?.DeepClone();
        report["window_ms"] = (long)cfg.Watch.TotalMilliseconds;
        var window = new TickWindow(cfg.Window);
        report["window"] = window.ToJson();

        var verifyStore = await service.StoreAsync(cancellationToken).ConfigureAwait(false);

        // Keeps player authority granted for the full watch window.
        service.KeepAuthority(cancellationToken);

        // Confirm the scheduler actually reaches automate and a Rounds pass gets persisted before starting the real
        // observation window.
        var diagDeadline = DateTimeOffset.UtcNow.AddSeconds(60);
        var sawAutomate = false;
        ulong revision = 0;
        while (DateTimeOffset.UtcNow < diagDeadline)
        {
            try
            {
                var state = await apiCall("GET", "/api/state", null, "", cancellationToken).ConfigureAwait(false);
                if (state.Body.Member("mode").AsString() == "automate")
                {
                    sawAutomate = true;
                }
            }
            catch (AcceptanceException)
            {
                // Polled again until the deadline.
            }

            try
            {
                revision = await verifyStore.RoundsRevisionAsync(cancellationToken).ConfigureAwait(false);
                if (revision > 0)
                {
                    break;
                }
            }
            catch (AcceptanceException)
            {
                // Not persisted yet.
            }

            await Task.Delay(RunInterval, cancellationToken).ConfigureAwait(false);
        }

        if (!sawAutomate)
        {
            throw new AcceptanceException("service never reached automate mode after acquire");
        }

        if (revision == 0)
        {
            throw new AcceptanceException("service reached automate mode but the Rounds pass was never persisted");
        }

        // The observation window: sample the goal's state and the stage of every one of its committed methods' plans,
        // at Poll intervals, for Watch wall-clock duration. Every sample is retained (report["timeline"]); "events"
        // additionally isolates the moments that actually changed (a method count change or a Need transition) so the
        // failure mode is readable without wading through every sample.
        var events = new List<JsonObject>();
        var lastMethodCount = -1;
        var lastNeed = "";
        var concernId = cfg.Concern.Length > 0 ? cfg.Concern : "EnsureFoodSupply";
        var watchStarted = DateTimeOffset.UtcNow;
        var watchDeadline = watchStarted + cfg.Watch;
        var pending = new List<WatchCheckpoint>();
        if (cfg.Checkpoint is not null)
        {
            pending.Add(cfg.Checkpoint);
        }

        pending.AddRange(cfg.Checkpoints);
        var taken = new JsonArray();
        var stepped = false;
        var failFast = new FailFastState(cfg.FailFast, concernId, service.LogPath);
        var tail = LogTail.Create(service.LogPath);
        var stall = new TickStall(tickStallLimit, DateTimeOffset.UtcNow);
        var cadence = new Dictionary<string, int>(StringComparer.Ordinal) { ["wakes"] = 0, ["tick_polls"] = 0, ["wall_polls"] = 0 };
        try
        {
            while (DateTimeOffset.UtcNow < watchDeadline)
            {
                JsonObject sample;
                var sampled = true;
                try
                {
                    sample = await verifyStore.SampleStandardAsync(concernId, cancellationToken).ConfigureAwait(false);
                }
                catch (AcceptanceException e)
                {
                    sampled = false;
                    sample = new JsonObject { ["error"] = e.Message, ["at"] = Now() };
                }

                if (!stepped)
                {
                    stepped = await service.StepAdmittedAsync(watchStarted, cancellationToken).ConfigureAwait(false);
                }

                foreach (var extra in cfg.Extra)
                {
                    JsonObject also;
                    try
                    {
                        also = await verifyStore.SampleStandardAsync(extra, cancellationToken).ConfigureAwait(false);
                    }
                    catch (AcceptanceException e)
                    {
                        also = new JsonObject { ["error"] = e.Message };
                    }

                    sample[extra] = also;
                }

                if (await LiveTickAsync(apiCall, cancellationToken).ConfigureAwait(false) is { } liveTick)
                {
                    sample["tick"] = liveTick;
                    window.Observe(liveTick);
                    stall.Observe(liveTick, DateTimeOffset.UtcNow);
                }

                sample["colony"] = await SampleColonyAsync(apiCall, cancellationToken).ConfigureAwait(false);
                if (stall.Check(DateTimeOffset.UtcNow) is { } stalled)
                {
                    timeline.Add(sample);
                    report["fail_fast"] = new JsonObject { ["kind"] = "tick_stall", ["error"] = stalled.Message, ["colony"] = sample["colony"]?.DeepClone() };
                    throw stalled;
                }

                timeline.Add(sample);
                var methodCount = SampleJson.Count(sample, "method_count");
                var need = sample["need"].AsString();
                if (methodCount != lastMethodCount || need != lastNeed)
                {
                    events.Add(sample);
                    lastMethodCount = methodCount;
                    lastNeed = need;
                }

                if (cfg.Until is not null && sampled && cfg.Until(sample))
                {
                    report["watch_ended_early"] = true;
                    break;
                }

                if (window.HasReached())
                {
                    break;
                }

                if (stall.Idle(DateTimeOffset.UtcNow, idleGrace))
                {
                    report["watch_ended_idle"] = sample["tick"]?.DeepClone();
                    break;
                }

                // The failed checkpoint bundle is still taken: the runner captures it on the error path like any
                // other watch failure.
                if (sampled)
                {
                    if (failFast.Check(sample) is { } verdict)
                    {
                        report["fail_fast"] = verdict.ToJson();
                        throw verdict;
                    }

                    for (var i = 0; i < pending.Count; i++)
                    {
                        var cp = pending[i];
                        if (!cp.When(sample))
                        {
                            continue;
                        }

                        pending.RemoveAt(i);
                        i--;
                        JsonObject saved;
                        try
                        {
                            saved = await CheckpointAsync(naCfg, cp, service, cancellationToken).ConfigureAwait(false);
                        }
                        catch (AcceptanceException e)
                        {
                            stall.Reset(DateTimeOffset.UtcNow);
                            report["checkpoint"] = new JsonObject { ["name"] = cp.Name, ["error"] = e.Message };
                            throw new AcceptanceException($"checkpoint {cp.Name}: {e.Message}", e);
                        }

                        stall.Reset(DateTimeOffset.UtcNow);
                        try
                        {
                            if (await Checkpoints.CaptureCheckpointAsync(cp.Name, cancellationToken).ConfigureAwait(false) is { } bundle)
                            {
                                saved["bundle"] = bundle.Path;
                            }
                        }
                        catch (AcceptanceException e)
                        {
                            saved["bundle_error"] = e.Message;
                        }

                        taken.Add(saved.DeepClone());
                        if (!report.Has("checkpoint"))
                        {
                            report["checkpoint"] = saved;
                        }

                        report["checkpoints"] = taken.DeepClone();
                    }
                }

                // Between samples is a natural pause for the runner's checkpoint ring; a capture's own time does not
                // count against Watch.
                var took = await Checkpoints.PauseAsync(cancellationToken).ConfigureAwait(false);
                if (took > TimeSpan.Zero)
                {
                    watchDeadline += took;
                    stall.Reset(DateTimeOffset.UtcNow);
                }

                var sampledTick = SampleJson.Tick(sample["tick"]) ?? 0;
                var reason = await WaitNextSampleAsync(apiCall, tail, wake, pollTicks, poll, stall, sampledTick, watchDeadline, cancellationToken).ConfigureAwait(false);
                cadence[reason]++;
            }

            window.WatchWall = FormatWall(DateTimeOffset.UtcNow - watchStarted);
            window.Reached = window.HasReached();
        }
        finally
        {
            report["timeline"] = new JsonArray([.. timeline.Select(sample => (JsonNode?)sample.DeepClone())]);
            report["events"] = new JsonArray([.. events.Select(sample => (JsonNode?)sample.DeepClone())]);
            report["timeline_samples"] = timeline.Count;
            report["window"] = window.ToJson();
            report["cadence"] = new JsonObject
            {
                ["poll_ticks"] = pollTicks,
                ["poll_ms"] = (long)poll.TotalMilliseconds,
                ["wakes"] = cadence["wakes"],
                ["tick_polls"] = cadence["tick_polls"],
                ["wall_polls"] = cadence["wall_polls"],
            };
        }
    }

    // Go's time.Duration.Round(time.Second).String(): "1m30s".
    private static string FormatWall(TimeSpan elapsed)
    {
        var rounded = TimeSpan.FromSeconds(Math.Round(elapsed.TotalSeconds, MidpointRounding.AwayFromZero));
        return WaitError.FormatDuration(rounded);
    }

    /// <summary>
    /// Reports a scheduler admission row refusing the window with no_work among its refusals: the governor declined to
    /// run the clock because nothing needs game time.
    /// </summary>
    public static bool NoWorkRefusal(LogRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.Kind != "admission" || RowValue.Text(row.Payload.GetValueOrDefault("target")) != "window")
        {
            return false;
        }

        if (row.Payload.GetValueOrDefault("attrs") is not IReadOnlyDictionary<string, object?> attrs
            || attrs.GetValueOrDefault("refused") is not IEnumerable<object?> refused)
        {
            return false;
        }

        return refused.Any(reason => RowValue.Text(reason) == "no_work");
    }

    // Pauses between two samples until one of the cadence's conditions holds and names it for the report: a journal row
    // satisfying Wake ("wakes"), PollTicks of game time past the previous sample's tick ("tick_polls"), or the Poll
    // wall-clock ceiling ("wall_polls"). The live tick and the journal are probed every RunInterval. The watch deadline
    // ends the pause early so the loop's own check sees it.
    private static async Task<string> WaitNextSampleAsync(ServiceApiCall apiCall, LogTail tail, Func<LogRow, bool> wake, ulong pollTicks, TimeSpan poll, TickStall stall, ulong sampledTick, DateTimeOffset watchDeadline, CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        while (true)
        {
            var wait = RunInterval;
            var remaining = watchDeadline - DateTimeOffset.UtcNow;
            if (remaining < wait)
            {
                wait = remaining;
            }

            if (wait <= TimeSpan.Zero)
            {
                return "wall_polls";
            }

            await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            foreach (var row in tail.Next())
            {
                if (NoWorkRefusal(row))
                {
                    stall.RefusedNoWork();
                }

                if (wake(row))
                {
                    return "wakes";
                }
            }

            if (started.Elapsed >= poll)
            {
                return "wall_polls";
            }

            if (await LiveTickAsync(apiCall, cancellationToken).ConfigureAwait(false) is { } tick && sampledTick > 0 && tick >= sampledTick + pollTicks)
            {
                return "tick_polls";
            }
        }
    }

    /// <summary>Reads the service's live game tick from /api/state; null when the snapshot has none (not connected yet, or a stale read).</summary>
    public static async Task<ulong?> LiveTickAsync(ServiceApiCall apiCall, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(apiCall);
        ServiceApiReply reply;
        try
        {
            reply = await apiCall("GET", "/api/state", null, "", cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException)
        {
            return null;
        }

        return reply.Status == 200 ? SampleJson.Tick(reply.Body.Member("game").Member("tick")) : null;
    }

    // Saves the live game as cp.Name through the service (pause to manual control, save, resume, the keep-alive held
    // throughout) and copies the save into root/profile/Saves. The result is the report's checkpoint record.
    private static async Task<JsonObject> CheckpointAsync(Config naCfg, WatchCheckpoint cp, IWatchedService service, CancellationToken cancellationToken)
    {
        var prefix = service.Label.Length > 0 ? service.Label : "serve";
        var src = await CheckpointRing.ServiceSaveAsync(naCfg, cp.Name, service.HoldAuthority, service.ApiAsync, service.Identity, service.Token, prefix + "-checkpoint", cancellationToken).ConfigureAwait(false);
        var dst = Path.Combine(naCfg.Root, "profile", "Saves", cp.Name + ".rws");
        if (src != dst)
        {
            try
            {
                Checkpoints.CopyFile(src, dst);
            }
            catch (IOException e)
            {
                throw new AcceptanceException(e.Message, e);
            }
        }

        return new JsonObject { ["name"] = cp.Name, ["path"] = dst, ["at"] = Now() };
    }

    /// <summary>
    /// Runs one <paramref name="o"/> on <paramref name="session"/> and returns the timeline; the service is stopped and
    /// the session reattached before Audit, on every path.
    /// </summary>
    public static async Task<IReadOnlyList<JsonObject>> ObserveAsync(IObservedSession session, SustainedObservation o, CancellationToken cancellationToken)
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

        var timeline = new List<JsonObject>();
        try
        {
            try
            {
                await WatchAsync(session.Config, service, o.Watch, report, timeline, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                report["metrics"] = DeriveMetrics(timeline).ToJson();
                report["colony_outcome"] = DeriveColonyOutcome(timeline).ToJson();
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

        return timeline;
    }
}
