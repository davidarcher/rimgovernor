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

internal static partial class BuildingRuntimePackage
{
    /// <summary>The catalog planner name of the routine defense planner.</summary>
    internal const string DefensePlanner = "defense";

    /// <summary>The shelter-held fact over the review's facts: unknown without an enabled review.</summary>
    internal static Fact<bool> ClockShelterHeld(RoundsResult? review) =>
        review?.Detection is { } detection ? Known(PolicyFunctions.ShelterHeld(detection.Facts)) : Unknown<bool>();
}

/// <summary>The native reads a scheduler step makes.</summary>
public interface IClockWindowNative : IZonesNative, IPlanningWindowNative, IEntityNative
{
    Task<PbObservations.BundleSnapshot?> ReadStepAsync(BridgeApi.StepRequest request, CancellationToken ct);
}

/// <summary>
/// Plans a step: it reads the world once, runs the rounder and the planners the due queue selects as a bounded
/// wave, arbitrates their proposals, and reports the native-work window the planners ask for. Which planners run is
/// the reason's selection. The clock itself stays in the mod, so the Go scheduler's window admission, lease,
/// epoch, pace and event-poll machinery is not carried: the host (the in-process loop) decides and starts windows
/// from <see cref="ClockSchedulerResult.NativeWorkTicks"/> and the planners' verdicts.
/// </summary>
public sealed partial class ClockScheduler
{
    public static TimeSpan DefaultFullStepEvery => StepReasons.DefaultFullStepEvery;

    private readonly Player _player;
    private readonly IClockWindowNative _native;
    private readonly IClock _clock;
    private readonly ClockFacts _facts;
    private readonly PlannerQueue _queue;
    private readonly List<PlannerEntry> _catalog;
    private readonly PlannerReasonLog _plannerReasons = new();
    private readonly Lock _mu = new();

    private long _lastTick;
    private bool _lastTickKnown;
    private DateTimeOffset _lastFull;
    private ReadValidity? _validity;

    // The latest step's clock-status context: its identity and scope are what the planner wave's section refreshes read under.
    private PbCommon.ObservationContext? _stepContext;
    private volatile bool _running;

    public ClockScheduler(Player player, IClockWindowNative native, ClockSchedulerConfig config, IClock clock)
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
        ValidateReviewers(config);
        _player = player;
        _native = native;
        Config = config;
        _clock = clock;
        _facts = new ClockFacts(config.Store);
        _queue = new PlannerQueue();
        _catalog = [.. ClockPlannerCatalog.Entries];
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

    // Every planner must review through the scheduler's rounder, and the building planners must be built for the
    // concern (and phase) the catalog files them under.
    private static void ValidateReviewers(ClockSchedulerConfig c)
    {
        (object? Planner, Rounder? Reviewer, ConcernId? Concern, ConcernId? Expected)[] planners =
        [
            (c.Butcher, c.Butcher?.Reviewer, c.Butcher?.Concern, EnsureFoodSupply),
            (c.Fields, c.Fields?.Reviewer, null, null),
            (c.FoodAcquisition, c.FoodAcquisition?.Reviewer, null, null),
            (c.PestAcquisition, c.PestAcquisition?.Reviewer, null, null),
            (c.ResourceAcquisition, c.ResourceAcquisition?.Reviewer, null, null),
            (c.Work, c.Work?.Reviewer, null, null),
            (c.Supplies, c.Supplies?.Reviewer, null, null),
            (c.Clearance, c.Clearance?.Reviewer, null, null),
            (c.Shrine, c.Shrine?.Reviewer, null, null),
            (c.Armory, c.Armory?.Reviewer, null, null),
            (c.Blight, c.Blight?.Reviewer, null, null),
            (c.Pollution, c.Pollution?.Reviewer, null, null),
            (c.MechCharger, c.MechCharger?.Reviewer, null, null),
            (c.GeneBank, c.GeneBank?.Reviewer, null, null),
            (c.Ledger, c.Ledger?.Reviewer, null, null),
            (c.Sleeping, c.Sleeping?.Reviewer, c.Sleeping?.Concern, MaintainHousing),
            (c.Cooking, c.Cooking?.Reviewer, c.Cooking?.Concern, EnsureCooking),
            (c.Comfort, c.Comfort?.Reviewer, c.Comfort?.Concern, EnsureComfort),
            (c.BasicComfort, c.BasicComfort?.Reviewer, c.BasicComfort?.Concern, EnsureComfort),
            (c.Workshop, c.Workshop?.Reviewer, c.Workshop?.Concern, MaintainResource),
            (c.Hospital, c.Hospital?.Reviewer, null, null),
            (c.SleepingUpkeep, c.SleepingUpkeep?.Reviewer, null, null),
            (c.Expansion, c.Expansion?.Reviewer, c.Expansion?.Concern, MaintainHousing),
            (c.Power, c.Power?.Reviewer, c.Power?.Concern, EnsureBasicPower),
            (c.Temperature, c.Temperature?.Reviewer, c.Temperature?.Concern, EnsureTemperatureSafety),
            (c.Refrigeration, c.Refrigeration?.Reviewer, c.Refrigeration?.Concern, MaintainRefrigeration),
            (c.Lighting, c.Lighting?.Reviewer, c.Lighting?.Concern, MaintainLighting),
            (c.Flooring, c.Flooring?.Reviewer, c.Flooring?.Concern, MaintainFlooring),
            (c.Routes, c.Routes?.Reviewer, c.Routes?.Concern, MaintainRoutes),
        ];
        foreach (var (planner, reviewer, concern, expected) in planners)
        {
            if (planner is null)
            {
                continue;
            }
            if (c.Rounds is null || !ReferenceEquals(reviewer, c.Rounds) || expected is { } want && concern != want)
            {
                throw new ControlException($"NewClockScheduler: {planner.GetType().Name} does not review through the scheduler's rounder or its concern");
            }
        }
        // The sleeping, comfort and expansion building planners are also checked for their phase; every other
        // planner (defense, tend, ...) validates its own reviewer when it is built.
        if (c.Sleeping is { } s && s.Phase != Policy.Phase.HousingShelter
            || c.Expansion is { } e && e.Phase != Policy.Phase.HousingExpansion
            || c.Comfort is { } k && k.Phase != Policy.Phase.ComfortRanked
            || c.BasicComfort is { } b && b.Phase != Policy.Phase.ComfortBasic)
        {
            throw new ControlException("NewClockScheduler: a building planner is built for another phase");
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

    /// <summary>The most ticks a step lends as a native-work window: the configured bound, else the window's own maximum.</summary>
    internal uint NativeWorkBudget() => Config.Budget.NativeWork > 0 ? Config.Budget.NativeWork : Config.MaxTicks;

    /// <summary>Makes the next timer step a full one after a planner wave failed with its selection spent.</summary>
    internal void ReplanAfterFailure() => _lastFull = default;

    private bool FullStepDue()
    {
        var every = Config.FullStepEvery <= TimeSpan.Zero ? DefaultFullStepEvery : Config.FullStepEvery;
        return _lastFull == default || _clock.Now() - _lastFull >= every;
    }

    /// <summary>The store scope an observation context establishes: the load token, map and native generation.</summary>
    internal static FactScope FactScope(PbCommon.ObservationContext context) =>
        new(context.Identity.LoadToken, context.Identity.MapId, context.NativeGeneration.GetValueOrDefault());

    /// <summary>The ticks to wait when any isolated planner failure of the step is a native refusal; zero otherwise.</summary>
    internal static uint PlannerRefusalWait(IEnumerable<Exception> failures) =>
        failures.Any(f => f is BridgeRefusedException) ? StockWaitTicks : 0;

    private bool StepReviews(StepReason reason) =>
        Config.Rounds is not null && PreviewSelection(reason).Planners;

    // The selection a step for reason is expected to make once its status is read, judged at the tick the step
    // expects to observe over a copy of the queue.
    private PlannerSelectionResult PreviewSelection(StepReason reason)
    {
        if (reason.Cause == StepCause.Timer && FullStepDue())
        {
            reason = reason with { Cause = StepCause.Full };
        }
        return PlannerSelections.Of(reason, _facts.KindOf, _queue.Clone(), _lastTick + Drift());
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
            StepCause.Settled => _queue.Selection(tick, false, true, stillOpen),
            StepCause.Timer or StepCause.Wake or StepCause.Live => _queue.Selection(tick, false, false, stillOpen),
            _ => _queue.Selection(tick, true, false, stillOpen),
        };
    }

    /// <summary>
    /// Performs one planning step for <paramref name="reason"/>: reads the world once, fixes the step's read
    /// validity, and runs the planners the reason selects. It never acquires authority, starts a window or runs a
    /// background loop. The result carries what the planners did and the native-work window they ask for.
    /// </summary>
    public async Task<ClockSchedulerResult> StepWithReasonAsync(StepReason reason, CancellationToken ct)
    {
        var result = new ClockSchedulerResult();
        using var span = Tel.StartSpan("buildingruntime", "scheduler.step", ("rimgovernor.step_cause", Wire.Name(reason.Cause)));
        var entered = Stopwatch.GetTimestamp();
        var stepBegan = _clock.Now();
        result.StartedAt = stepBegan;
        using var entry = await _player.EnterAsync("clock_step", false, ct).ConfigureAwait(false);
        var call = entry.Call;
        var epoch = entry.Epoch;
        var gateWait = Stopwatch.GetElapsedTime(entered);
        Exception? failure = null;
        // The round trips that still cross the bridge (frame misses and writes) are tallied by tool so the cost of
        // the composition is visible per step: as a clock_step row in the OTLP log ring, which `rimgovernor phases`
        // reports as reads/step.
        using var reads = BridgeApi.WithReadTally();
        try
        {
            // A wake's evidence lands on the due queue once, here, so it outlives a step that runs no planners.
            if (reason.Cause == StepCause.Wake)
            {
                _queue.Wake(reason, _facts.KindOf);
            }
            var reviews = StepReviews(reason);
            var zones = new ZoneRefresher(_native, _facts.Store);
            using var zoneScope = ObservationApi.WithZones(zones);
            var window = new PlanningWindow(_native, _facts.Store);
            using var windowScope = ObservationApi.WithPlanningWindow(window);
            var started = _clock.Now();
            var loaded = await _native.ReadStepAsync(new BridgeApi.StepRequest { ClockStatus = true, Emergency = true }, call).ConfigureAwait(false)
                ?? throw new HeldException("step read returned nothing");
            var loadedContext = loaded.Context ?? throw new BridgeContractException("step read carried no context");
            if (await _player.ExpeditionIncomingHandoffAsync(loaded, call, epoch).ConfigureAwait(false))
            {
                result.Deferred = true;
                return result;
            }
            if (Config.WorldReady is { } ready && await ready(loadedContext, call).ConfigureAwait(false) && Config.Rounds is not null)
            {
                reason = reason with { Cause = StepCause.Full };
                ReplanAfterFailure();
                reviews = StepReviews(reason);
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
            BridgeApi.ValidateClockStatus(loaded.ClockStatus, id);
            var status = loaded.ClockStatus ?? throw new InvalidOperationException("clock status required");
            var statusTick = (long)(status.Context ?? throw new BridgeContractException("clock status carried no context")).Tick;
            if (statusTick < (long)loadedContext.Tick)
            {
                ClockEvents.AuthorityLost("clock status is behind the scope tick");
                throw new EvidenceException("clock status is behind the scope tick");
            }
            _running = status.State == PbClock.ClockState.Running;
            StepStatus = status;
            reason = reason with { TickAdvanced = !_lastTickKnown || statusTick != _lastTick };
            (_lastTick, _lastTickKnown) = (statusTick, true);
            LivePace(status, started);
            result.Pacing = StepPacing.Of(status);
            // The step's scope and tick are fixed: every read and decision below, and the Worker's dispatches under
            // the window this step may admit, judge their facts against this validity.
            using var validity = ReadValidityScope.With(FixValidity(state.Snapshot, statusTick));
            Tel.ObserveTick(statusTick);
            if (reason.Cause == StepCause.Timer && FullStepDue())
            {
                reason = reason with { Cause = StepCause.Full };
            }
            result.Reason = reason;
            if (!state.Enabled)
            {
                throw new AuthorityException("authority is not enabled");
            }
            _player.Current(call, epoch);
            var sel = await SelectPlannersAsync(reason, statusTick, call).ConfigureAwait(false);
            result.Waiting = [.. sel.Waiting];
            if (sel.Planners && Config.Rounds is not null)
            {
                try
                {
                    result.Planners = await RunPlannerWaveAsync(result, sel, statusTick, false, call, epoch).ConfigureAwait(false);
                }
                catch (Exception e) when (e is not OperationCanceledException)
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
            result.NativeWorkTicks = NativeWork(result);
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
                ["budget"] = new Dictionary<string, object?> { ["wall_ms"] = Config.Budget.EffectiveWall.TotalMilliseconds, ["native_ticks"] = NativeWorkBudget() },
            };
            if (result.Waiting.Count > 0)
            {
                row["waiting"] = result.Waiting;
            }
            if (result.NativeWorkTicks > 0)
            {
                row["native_work_ticks"] = result.NativeWorkTicks;
            }
            if (failure is not null)
            {
                row["error"] = failure.Message;
            }
            // The step's budgets beside what it used: reads against the read budget, the planner waves against the
            // wall budget, the native-work window against its bound.
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
            var cause = result.Reason.Cause == default ? reason.Cause : result.Reason.Cause;
            var elapsed = Stopwatch.GetElapsedTime(entered);
            Metrics.RecordStepMetrics(cause, elapsed, result.CriticalWave, row);
            reads.Tally.Publish(Tel.Emitter, StepDecision(result, failure, cause, elapsed, row));
        }
    }

    // The largest window any planner asks for: native work (a sculpture, a research project, a caravan walking, a
    // bounded home fire) finishes on game time, not on another method.
    private uint NativeWork(ClockSchedulerResult r)
    {
        uint ticks = 0;
        foreach (var asked in new uint?[]
        {
            r.Shrine?.NativeWorkTicks, r.Fields?.NativeWorkTicks, r.Clearance?.NativeWorkTicks,
            r.Sleeping?.NativeWorkTicks, r.Cooking?.NativeWorkTicks, r.Butcher?.NativeWorkTicks, r.Comfort?.NativeWorkTicks,
            r.BasicComfort?.NativeWorkTicks, r.Workshop?.NativeWorkTicks, r.Hospital?.NativeWorkTicks,
            r.SleepingUpkeep?.NativeWorkTicks, r.Expansion?.NativeWorkTicks, r.Power?.NativeWorkTicks,
            r.Temperature?.NativeWorkTicks, r.Refrigeration?.NativeWorkTicks, r.Lighting?.NativeWorkTicks,
            r.Flooring?.NativeWorkTicks, r.Routes?.NativeWorkTicks, r.MechCharger?.NativeWorkTicks, r.GeneBank?.NativeWorkTicks,
            r.FireSafety?.NativeWorkTicks, r.DefenseLayout?.NativeWorkTicks, r.Research?.NativeWorkTicks,
            r.Husbandry?.NativeWorkTicks, r.Trade?.NativeWorkTicks, r.Ledger?.NativeWorkTicks,
            r.PopulationJoiner?.NativeWorkTicks, r.Resource?.NativeWorkTicks, r.Tend?.NativeWorkTicks,
            r.Rescue?.NativeWorkTicks, r.Surgery?.NativeWorkTicks,
        })
        {
            ticks = Math.Max(ticks, asked ?? 0);
        }
        // A planner that failed on a native refusal produced neither work nor a wait; one window lets the world move.
        ticks = Math.Max(ticks, PlannerRefusalWait(r.PlannerFailures));
        return ticks == 0 ? 0 : Math.Min(ticks, Math.Max(NativeWorkBudget(), ticks));
    }

    /// <summary>
    /// Runs the rounder and the selected planner wave as an admission cycle and an optional wave. The cycle joins
    /// the routine review and the critical planners under the wall budget: past it, the critical planners still
    /// pending are named on HeldBy and the step admits nothing. The optional planners run on the same snapshot and
    /// are joined up to the wall budget; those
    /// still evaluating at that cutoff are cancelled, recorded on MissedCutoff and their results discarded. The
    /// coordinator then arbitrates the proposals that made the cutoff, and any carried from an earlier step.
    /// </summary>
    internal async Task<List<string>> RunPlannerWaveAsync(ClockSchedulerResult output, PlannerSelectionResult sel, long tick, bool immediate, CancellationToken call, CancellationToken epoch)
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
                    _ = await ObservationApi.ReadZoneSection(_native, stepIdentity, call).ConfigureAwait(false);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // A failed zone read keeps the held census: planners ask again and judge its staleness.
                }
            }
            List<string> planners;
            try
            {
                planners = await StepPlannersAsync(output, wave, arbiter, sel.Pick, immediate, call, epoch).ConfigureAwait(false);
            }
            catch
            {
                output.CriticalWave += Stopwatch.GetElapsedTime(began);
                throw;
            }
            var wall = PlannerWave.After(Config.Budget.EffectiveWall);
            if (await wave.Group.WaitCriticalAsync(wall).ConfigureAwait(false) == CriticalWait.Cutoff)
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
        }
    }

    // Files a wave on the due queue: the planners that returned before the cutoff are due again at their cadence,
    // and one that reported existing work of its kinds waits on the open attempts of those kinds.
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
            kinds => plans is null ? [] : PlannerQueue.OpenWorkOfKinds(plans, kinds));
    }

    // Runs the rounder synchronously first (every other planner's dispatch depends on being able to load the review
    // it commits), then queues the configured catalog planners the pick selects (null: all) onto the wave sharing
    // the arbiter, returning the queued names without waiting. The rounder's own error aborts before anything is
    // queued; an error from a queued planner is isolated by the wave.
    private async Task<List<string>> StepPlannersAsync(ClockSchedulerResult output, PlannerWave wave, StepArbiter arbiter, Func<PlannerEntry, bool>? pick, bool immediate, CancellationToken call, CancellationToken epoch)
    {
        if (Config.Rounds is { } rounds)
        {
            RoundsResult review;
            try
            {
                review = immediate
                    ? await rounds.ImmediateStepAsync(call, epoch).ConfigureAwait(false)
                    : await rounds.StepAsync(arbiter, pick is not null, call, epoch).ConfigureAwait(false);
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
            if (!immediate)
            {
                await EstablishExtentAsync(review.Review.Tick, call).ConfigureAwait(false);
            }
        }
        return QueuePlanners(wave, arbiter, pick, call, epoch);
    }
}
