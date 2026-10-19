using System.Diagnostics;

namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>One typed service event, mirrored into the OTLP log ring as a row of the given kind.</summary>
internal static class ClockEvents
{
    public static void Event(string component, string kind, string message, params (string Key, object? Value)[] attrs) =>
        Tel.Event(TelemetryLevel.Info, component, kind, message, attrs);

    /// <summary>Logs that the controller dropped its own authority and why, as a WARN authority row.</summary>
    public static void AuthorityLost(string why, params (string Key, object? Value)[] attrs) =>
        Tel.Event(TelemetryLevel.Warn, "clock-scheduler", "authority", "authority lost: " + why, [("change", "lost"), ("reason", why), .. attrs]);
}

partial class ClockScheduler
{
    /// <summary>The catalog planner name of the routine defense planner.</summary>
    internal const string DefensePlanner = "defense";

    /// <summary>The shelter-held fact over the review's facts: unknown without an enabled review.</summary>
    internal static Fact<bool> ClockShelterHeld(RoundsResult? review) =>
        review?.Detection is { } detection ? Known(ShelterRules.ShelterHeld(detection.Facts)) : Unknown<bool>();
}

/// <summary>The native reads a scheduler step makes.</summary>
public interface IClockStepNative : IZonesNative, IPlanningWindowNative, IEntityNative
{
    Task<PbObservations.BundleSnapshot?> ReadStepAsync(StepRequest request, CancellationToken ct);

    /// <summary>The world progression census, read on demand by the incoming expedition handoff.</summary>
    Task<WorldProgressionRead> ReadWorldProgressionAsync(PbCommon.Identity identity, CancellationToken ct);
}

/// <summary>
/// Plans a step: it reads the world once, runs the rounder and the planners the due queue selects as a bounded
/// wave and arbitrates their proposals. Which planners run is the reason's selection. The clock itself stays in
/// the mod and time passes continuously: the planners never ask it for ticks.
/// </summary>
public sealed partial class ClockScheduler
{
    public static TimeSpan DefaultFullStepEvery => StepReasons.DefaultFullStepEvery;

    private readonly Player _player;
    private readonly IClockStepNative _native;
    private readonly IClock _clock;
    private readonly ClockFacts _facts;
    private readonly PlannerQueue _queue;
    private readonly List<PlannerEntry> _catalog;
    private readonly PlannerReasonLog _plannerReasons = new();
    private readonly Lock _mu = new();

    private long _lastTick;
    private bool _lastTickKnown;
    private DateTimeOffset _lastFull;

    // The rounds cadence (#3556): the status tick of the step that last ran the rounder, and its result, which the
    // steps between two rounds reviews report in place of their own (the shelter fact the run driver reads).
    private long _lastRoundsTick;
    private bool _roundsReviewed;
    private RoundsResult? _lastRounds;
    private ReadValidity? _validity;

    // The latest step's clock-status context: its identity and scope are what the planner wave's section refreshes read under.
    private PbCommon.ObservationContext? _stepContext;

    /// <summary>
    /// The observation scope this step read with its clock status (#3556): the family entries' reads validate
    /// against it instead of each hopping to the game thread for the tick. Null until the step has read its status.
    /// </summary>
    internal Observation.Identity? StepIdentity { get; private set; }
    private volatile bool _running;

    public ClockScheduler(Player player, IClockStepNative native, ClockSchedulerConfig config, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(native);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(clock);
        if (config.MaxAge <= TimeSpan.Zero || config.MaxAge > TimeSpan.FromMinutes(1))
        {
            throw new ControlException("NewClockScheduler: MaxAge outside (0, 1m]");
        }
        if (config.Rounds is not null && !ReferenceEquals(config.Rounds.Player, player))
        {
            throw new ControlException("NewClockScheduler: Rounds belongs to another player");
        }
        if (config.RoundsEveryTicks <= 0)
        {
            throw new ControlException("NewClockScheduler: the rounds cadence must be a positive number of ticks");
        }
        _player = player;
        _native = native;
        Config = config;
        _clock = clock;
        _facts = new ClockFacts(config.Store);
        _queue = new PlannerQueue();
        _catalog = ClockPlannerCatalog.WithFamilies(config.Families);
        _queue.Catalog = () => _catalog;
        _queue.Configured = entry => entry.Configured(Config);
        if (config.Rounds is { } rounds)
        {
            rounds.Store = _facts.Store;
            rounds.FoodGapZero = config.Faults.FoodGapZero;
            rounds.StockDemand = new Dictionary<Resource, long>(config.Faults.StockDemand);
        }
    }

    public ClockSchedulerConfig Config { get; }

    internal Player Player => _player;

    /// <summary>The due queue the planner selection reads.</summary>
    internal PlannerQueue Queue => _queue;

    /// <summary>The protection actions the immediate planners own that are still being watched.</summary>
    internal List<ActionId> Protection { get; } = [];

    /// <summary>The last rounds review a step of this scheduler ran; null before the first. A step that ran no rounder reports none of its own, and the run driver reads this one.</summary>
    internal RoundsResult? LastRounds => _lastRounds;

    /// <summary>Whether the latest step found the clock running.</summary>
    public bool ClockRunning => _running;

    /// <summary>The read validity of the latest step whose scope was fixed: the scope and tick its facts describe. Null before any step fixed one.</summary>
    public ReadValidity? Validity
    {
        get
        {
            lock (_mu)
            {
                return _validity;
            }
        }
    }

    /// <summary>Fixes the step's validity once its scope and tick are known and publishes it for the Worker.</summary>
    private ReadValidity FixValidity(GenerationSnapshot snapshot, long tick)
    {
        var v = ReadValidity.Of(snapshot, new Tick(tick));
        lock (_mu)
        {
            _validity = v;
        }
        return v;
    }

    /// <summary>Makes the next review or retry step a full one after a planner wave failed with its selection spent.</summary>
    internal void ReplanAfterFailure() => _lastFull = default;

    // A review or retry step is promoted to a full one once the safety-net interval has passed since the last full wave.
    private static bool Promotable(StepCause cause) => cause is StepCause.Review or StepCause.Retry;

    private bool FullStepDue()
    {
        var every = Config.FullStepEvery <= TimeSpan.Zero ? DefaultFullStepEvery : Config.FullStepEvery;
        return _lastFull == default || _clock.Now() - _lastFull >= every;
    }

    /// <summary>The store scope an observation context establishes: the load token, map and native generation.</summary>
    internal static FactScope FactScope(PbCommon.ObservationContext context) =>
        new(context.Identity.LoadToken, context.Identity.MapId, context.NativeGeneration.GetValueOrDefault());

    /// <summary>
    /// Why a step that runs planners runs the rounder first, or null when its planners read the last committed
    /// review instead (#3556): <c>all</c> when every planner runs (a full step, an authority change), <c>wake</c>
    /// on an early review (any hold but the review tick), <c>due</c> when no review has been committed since the
    /// scheduler started, the tick went back, or the last review at <paramref name="lastTick"/> has stood
    /// <paramref name="every"/> ticks at <paramref name="tick"/>. A review step between two due ticks that selects
    /// only dirty and due planners is the step the cadence makes cheap.
    /// </summary>
    internal static string? RoundsSelection(StepCause cause, bool everyPlanner, long tick, bool reviewed, long lastTick, long every)
    {
        if (everyPlanner)
        {
            return "all";
        }
        if (cause == StepCause.Wake)
        {
            return "wake";
        }
        if (!reviewed || tick < lastTick || tick - lastTick >= every)
        {
            return "due";
        }
        return null;
    }

    // The step's selection over the due queue at tick: a wake's evidence was folded when the step began. The waits
    // are checked against the journal only while any is held, so a dependency that settled without an outcome event
    // releases its planner at the next step.
    private async Task<PlannerSelectionResult> SelectPlannersAsync(StepReason reason, long tick, CancellationToken ct)
    {
        Func<ActionId, bool>? stillOpen = null;
        if (_queue.Waits.Count > 0)
        {
            stillOpen = PlannerQueue.OpenWorkIndex(await _player.Journal.LoadPlansAsync(ct).ConfigureAwait(false));
        }
        return reason.Cause switch
        {
            StepCause.Review or StepCause.Wake or StepCause.Retry => _queue.Selection(tick, false, stillOpen),
            _ => _queue.Selection(tick, true, stillOpen),
        };
    }

    /// <summary>
    /// Performs one planning step for <paramref name="reason"/>: reads the world once, fixes the step's read
    /// validity, and runs the planners the reason selects. It never acquires authority, sends Run or runs a
    /// background loop. The result carries what the planners did.
    /// </summary>
    public async Task<ClockSchedulerResult> StepWithReasonAsync(StepReason reason, CancellationToken ct)
    {
        var result = new ClockSchedulerResult();
        using var span = Tel.StartSpan("buildingruntime", "scheduler.step", ("rimgovernor.step_cause", Wire.Name(reason.Cause)));
        using var runtime = Tel.RuntimeTags(span);
        using var profile = Tel.ProfileTags(span);
        var entered = Stopwatch.GetTimestamp();
        var stepBegan = _clock.Now();
        using var entry = await _player.EnterAsync("clock_step", false, ct).ConfigureAwait(false);
        var call = entry.Call;
        var epoch = entry.Epoch;
        var gateWait = Stopwatch.GetElapsedTime(entered);
        result.Stages.Lap("gate");
        Exception? failure = null;
        PlannerSelectionResult? sel = null;
        string? rounds = null;
        // The round trips that still cross the bridge (frame misses and writes) are tallied by tool so the cost of
        // the composition is visible per step: as a clock_step row in the OTLP log ring, which `rimgovernor phases`
        // reports as reads/step.
        using var reads = ReadTallyScope.WithReadTally();
        try
        {
            // A wake's evidence lands on the due queue once, here, so it outlives a step that runs no planners.
            if (reason.Cause is StepCause.Wake or StepCause.Review)
            {
                _queue.Wake(reason);
            }
            var zones = new ZoneRefresher(_native, _facts.Store);
            using var zoneScope = ZonesReads.WithZones(zones);
            var window = new PlanningWindow(_native, _facts.Store);
            using var windowScope = PlanningWindowReads.WithPlanningWindow(window);
            var started = _clock.Now();
            var loaded = await _native.ReadStepAsync(new StepRequest { ClockStatus = true }, call).ConfigureAwait(false)
                ?? throw new HeldException("step read returned nothing");
            var loadedContext = loaded.Context ?? throw new BridgeContractException("step read carried no context");
            if (await _player.ExpeditionIncomingHandoffAsync(loaded, _native, call, epoch).ConfigureAwait(false))
            {
                result.Deferred = true;
                return result;
            }
            if (Config.WorldReady is { } ready && await ready(loadedContext, call).ConfigureAwait(false) && Config.Rounds is not null)
            {
                reason = reason with { Cause = StepCause.Full };
                ReplanAfterFailure();
            }
            var scope = FactScope(loadedContext);
            window.Scope = scope;
            (zones.Scope, zones.Tick) = (scope, (long)loadedContext.Tick);
            var id = loadedContext.Identity ?? throw new BridgeContractException("step read carried no identity");
            var tick = new Tick((long)loadedContext.Tick);
            var state = _player.State();
            var observed = new GenerationSnapshot { Colony = new ColonyId((id.ColonyId ?? "")), Load = new LoadId((id.LoadToken ?? "")), Map = new MapId(id.MapId) };
            if (!state.ObservationKnown || !Boundary.World(state.Snapshot, observed) || loadedContext.NativeGeneration.GetValueOrDefault() != (ulong)state.Snapshot.Native.Value)
            {
                ClockEvents.AuthorityLost("observed scope does not match the held authority");
                throw new AuthorityException("observed scope does not match the held authority");
            }
            window.Layout = async c =>
            {
                var world = _player.State().Snapshot;
                return world.Colony != observed.Colony || world.Map != observed.Map
                    ? null
                    : await _player.Journal.LayoutPlanAsync(world, tick, c).ConfigureAwait(false);
            };
            _stepContext = loaded.ClockStatus?.Context;
            StepIdentity = null;
            var status = loaded.ClockStatus ?? throw new InvalidOperationException("clock status required");
            var statusTick = (long)(status.Context ?? throw new BridgeContractException("clock status carried no context")).Tick;
            if (statusTick < (long)loadedContext.Tick)
            {
                ClockEvents.AuthorityLost("clock status is behind the scope tick");
                throw new EvidenceException("clock status is behind the scope tick");
            }
            StepIdentity = DecodeReads.ContextIdentity(status.Context);
            _running = status.State == PbClock.ClockState.Running;
            result.HoldSerial = status.HoldSerial;
            reason = reason with { TickAdvanced = !_lastTickKnown || statusTick != _lastTick };
            (_lastTick, _lastTickKnown) = (statusTick, true);
            LivePace(status, started);
            result.Pacing = StepPacing.Of(status);
            // The step's scope and tick are fixed: every read and decision below, and the Worker's dispatches under
            // the run this step may start, judge their facts against this validity.
            using var validity = ReadValidityScope.With(FixValidity(state.Snapshot, statusTick));
            Tel.ObserveTick(statusTick);
            if (Promotable(reason.Cause) && FullStepDue())
            {
                reason = reason with { Cause = StepCause.Full };
            }
            result.Reason = reason;
            if (!state.Enabled)
            {
                throw new AuthorityException("authority is not enabled");
            }
            _player.Current(call, epoch);
            result.Stages.Lap("read");
            sel = await SelectPlannersAsync(reason, statusTick, call).ConfigureAwait(false);
            result.Stages.Lap("select");
            result.Waiting = [.. sel.Waiting];
            if (sel.Planners && Config.Rounds is not null)
            {
                rounds = RoundsSelection(reason.Cause, sel.Pick is null, statusTick, _roundsReviewed, _lastRoundsTick, Config.RoundsEveryTicks);
                try
                {
                    result.Planners = await RunPlannerWaveAsync(result, sel, statusTick, false, rounds, call, epoch).ConfigureAwait(false);
                }
                catch
                {
                    ReplanAfterFailure();
                    throw;
                }
                if (result.HeldBy.Count > 0)
                {
                    // A critical planner is still evaluating past the wall budget: hold, naming the planners; the
                    // next step evaluates again.
                    throw new HeldException("critical planners still evaluating");
                }
                if (sel.Pick is null && !result.Immediate)
                {
                    _lastFull = _clock.Now();
                }
            }
            return result;
        }
        catch (Exception e)
        {
            failure = e;
            throw;
        }
        finally
        {
            var row = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["running"] = result.Running,
                ["gate_wait_ms"] = gateWait.TotalMilliseconds,
                ["budget"] = new Dictionary<string, object?> { ["wall_ms"] = Config.Budget.EffectiveWall.TotalMilliseconds },
            };
            if (result.Waiting.Count > 0)
            {
                row["waiting"] = result.Waiting;
            }
            if (failure is not null)
            {
                row["error"] = failure.Message;
            }
            // The step's budgets beside what it used: reads against the read budget, the planner waves against the
            // wall budget.
            if (Config.Budget.Reads > 0)
            {
                ((Dictionary<string, object?>)row["budget"]!)["reads"] = Config.Budget.Reads;
                if (reads.Tally.Total > Config.Budget.Reads)
                {
                    row["reads_over_budget"] = true;
                }
            }
            if (result.CriticalWave > TimeSpan.Zero)
            {
                row["critical_wave_ms"] = result.CriticalWave.TotalMilliseconds;
            }
            if (result.PlannerMs.Count > 0)
            {
                row["planner_ms"] = result.PlannerMs;
            }
            if (result.MissedCutoff.Count > 0)
            {
                row["missed_cutoff"] = result.MissedCutoff;
            }
            if (result.HeldBy.Count > 0)
            {
                row["held_by"] = result.HeldBy;
            }
            if (reason.Stopped)
            {
                row["stop"] = true;
                if (reason.StopAt is { } stopAt)
                {
                    row["stop_latency_ms"] = (stepBegan - stopAt).TotalMilliseconds;
                }
            }
            result.Pacing.Publish(row);
            result.Stages.Publish(row);
            if (sel?.Why is { Count: > 0 } why)
            {
                // How many planners each selection word chose: the dirty-only cadence question (#3556).
                var selection = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var word in why.Values)
                {
                    selection[word] = (long)(selection.TryGetValue(word, out var n) ? (long)n! : 0) + 1;
                }
                row["selection"] = selection;
            }
            if (sel is { Planners: true } && Config.Rounds is not null)
            {
                // Whether the step ran the rounder and why, or `last`: its planners read the last committed review (#3556).
                row["rounds"] = rounds ?? "last";
            }
            var kept = result.Families.Values.Sum(f => f.Kept.Count);
            if (kept > 0)
            {
                // How many ready Needs kept their last wait or refusal instead of working (#3556).
                row["kept"] = (long)kept;
            }
            var cause = result.Reason.Cause == default ? reason.Cause : result.Reason.Cause;
            var elapsed = Stopwatch.GetElapsedTime(entered);
            Metrics.RecordStepMetrics(cause, elapsed, result.CriticalWave, row);
            reads.Tally.Publish(Tel.Emitter, StepDecision(result, failure, cause, elapsed, row));
        }
    }

    /// <summary>
    /// Runs the rounder and the selected planner wave as an admission cycle and an optional wave. The cycle joins
    /// the routine review and the critical planners under the wall budget: past it, the critical planners still
    /// pending are named on HeldBy and the step admits nothing. The optional planners run on the same snapshot and
    /// are joined up to the wall budget; those
    /// still evaluating at that cutoff are cancelled, recorded on MissedCutoff and their results discarded. The
    /// coordinator then arbitrates the proposals that made the cutoff, and any carried from an earlier step.
    /// <paramref name="rounds"/> is why the ordinary wave runs the rounder first (<see cref="RoundsSelection"/>); null
    /// runs the planners against the last committed review. The immediate wave always runs the immediate review.
    /// </summary>
    internal async Task<List<string>> RunPlannerWaveAsync(ClockSchedulerResult output, PlannerSelectionResult sel, long tick, bool immediate, string? rounds, CancellationToken call, CancellationToken epoch)
    {
        using var immediateScope = ReviewScope.BeginImmediate(immediate);
        var arbiter = new StepArbiter();
        using var wave = new PlannerWave(call);
        var began = Stopwatch.GetTimestamp();
        try
        {
            if (!immediate)
            {
                // Ordinary section refreshes belong after protective execution too. Failed reads retain the
                // existing explicit stale/unknown evidence.
                var stepContext = _stepContext ?? throw new HeldException("planner wave ran before the step read its status");
                var stepIdentity = stepContext.Identity ?? throw new HeldException("planner wave ran before the step read its identity");
                await ClockEntities.RefreshEntitySectionsAsync(_native, _facts, stepIdentity, FactScope(stepContext), call).ConfigureAwait(false);
                try
                {
                    _ = await ZonesReads.ReadZoneSection(_native, stepIdentity, call).ConfigureAwait(false);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // A failed zone read keeps the held census: planners ask again and judge its staleness.
                }
            }
            output.Stages.Lap("refresh");
            List<string> planners;
            try
            {
                planners = await StepPlannersAsync(output, wave, arbiter, sel.Pick, sel.Why, tick, immediate, rounds, call, epoch).ConfigureAwait(false);
            }
            catch
            {
                output.CriticalWave += Stopwatch.GetElapsedTime(began);
                throw;
            }
            var wall = PlannerWave.After(Config.Budget.EffectiveWall);
            var criticalWait = await wave.Group.WaitCriticalAsync(wall).ConfigureAwait(false);
            output.Stages.Lap("critical");
            if (criticalWait == CriticalWait.Cutoff)
            {
                var stuck = wave.Close();
                output.CriticalWave += Stopwatch.GetElapsedTime(began);
                foreach (var name in stuck)
                {
                    (wave.QueuedCritical(name) ? output.HeldBy : output.MissedCutoff).Add(name);
                }
                return planners;
            }
            var criticalWall = Stopwatch.GetElapsedTime(began);
            output.CriticalWave += criticalWall;
            var cutoff = wall;
            var pending = await wave.Group.WaitUntilAsync(cutoff).ConfigureAwait(false);
            output.Stages.Lap("optional");
            output.MissedCutoff.AddRange(pending);
            foreach (var (name, ms) in wave.PlannerMs())
            {
                output.PlannerMs[name] = output.PlannerMs.GetValueOrDefault(name) + ms;
            }
            wave.Close();
            wave.Merge(output);
            // A failed planner is reported, not fatal: the step still reports what the other planners committed,
            // and the failed planner retries next step.
            output.PlannerFailures.AddRange(wave.Group.Failures());
            return planners;
        }
        finally
        {
            wave.Close();
            await RecordWaveAsync(sel, wave, tick, call).ConfigureAwait(false);
            output.Stages.Lap("record");
        }
    }

    // Files a wave on the due queue: the planners that returned before the cutoff are due again at their cadence
    // (the family entries when the last committed rounds review has stood RoundsEveryTicks, #3556), and one that
    // reported existing work of its kinds waits on the open attempts of those kinds.
    private async Task RecordWaveAsync(PlannerSelectionResult sel, PlannerWave wave, long tick, CancellationToken ct)
    {
        await RecordPlannerReasonsAsync(wave, tick, ct).ConfigureAwait(false);
        IReadOnlyList<PlanState>? plans = null;
        try
        {
            // The plans are read once, only when some planner reported existing work.
            var needed = wave.FinishedNames().Any(n => wave.Reason(n) == BuildingReasonExistingWork);
            if (needed)
            {
                plans = await _player.Journal.LoadPlansAsync(ct).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            PlannerReasons.PlannerBookkeepingFailed("waits", e);
        }
        _queue.Ran(sel, wave.FinishedNames(), wave.Reason, tick,
            kinds => plans is null ? [] : PlannerQueue.OpenWorkOfKinds(plans, kinds),
            _roundsReviewed ? _lastRoundsTick + Config.RoundsEveryTicks : null);
    }

    // Runs the rounder synchronously first when the step's rounds selection or the immediate wave asks for it (every
    // other planner's dispatch depends on being able to load the review it commits; between two rounds reviews the
    // planners load the last one, #3556), then queues the configured catalog planners the pick selects (null: all)
    // onto the wave sharing the arbiter, returning the queued names without waiting. The rounder's own error aborts
    // before anything is queued; an error from a queued planner is isolated by the wave.
    private async Task<List<string>> StepPlannersAsync(ClockSchedulerResult output, PlannerWave wave, StepArbiter arbiter, Func<PlannerEntry, bool>? pick, IReadOnlyDictionary<string, string>? why, long tick, bool immediate, string? rounds, CancellationToken call, CancellationToken epoch)
    {
        if (Config.Rounds is { } rounder && (immediate || rounds is not null))
        {
            RoundsResult review;
            try
            {
                review = immediate
                    ? await rounder.ImmediateStepAsync(call, epoch).ConfigureAwait(false)
                    : await rounder.StepAsync(arbiter, pick is not null, call, epoch).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                throw new InvalidOperationException("routine: " + e.Message, e);
            }
            if (!review.Review.Enabled)
            {
                // Authority lapsed between this step's state read and the review: the reviewer recorded a disabled
                // review that ranks nothing. Fail the step instead, so the next step reviews with authority.
                throw new AuthorityException("routine: authority lapsed");
            }
            output.Rounds = review;
            output.Stages.Lap("rounds");
            if (!immediate)
            {
                (_lastRoundsTick, _roundsReviewed, _lastRounds) = (tick, true, review);
                await EstablishExtentAsync(review.Review.Tick, call).ConfigureAwait(false);
                output.Stages.Lap("extent");
            }
        }
        return QueuePlanners(wave, arbiter, pick, why, rounds, call, epoch);
    }
}
