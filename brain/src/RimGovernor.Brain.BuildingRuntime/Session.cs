namespace RimGovernor.Brain.BuildingRuntime;

public sealed class SessionConfig
{
    public bool RoundsMethods { get; set; }
    public required ControlConfig Control { get; set; }
    public Executor.Limits Executor { get; set; } = new();
    public ClockCapabilities? Clock { get; set; }
    public HaulCapabilities? Haul { get; set; }
    public MovementCapabilities? Movement { get; set; }
    public TradeCapabilities? Trade { get; set; }

    /// <summary>
    /// The emitter the planners' static helpers write through (Go's process-wide telemetry sink). The host installs
    /// the one over its OTLP writer; left null, rows keep going to whatever was installed before (the null sink).
    /// </summary>
    public TelemetryEmitter? Telemetry { get; set; }
}

/// <summary>
/// Binds the single profile owner to one journal and executor. Its caller owns bridge/database handles and may
/// close them only after Close succeeds. Only an explicit trusted player path may call Acquire or create
/// submitted plans.
/// </summary>
public sealed partial class Session : IPlayerSession, IWorkerSession
{
    private readonly bool _roundsMethods;
    private readonly Control _control;
    private readonly Executor.Executor _executor;
    private readonly StoreJournal _journal;
    private readonly ClockCoordinator? _clock;
    private readonly ClockWorkerSlot _clockWorkers;

    private Session(bool roundsMethods, Control control, Executor.Executor executor, StoreJournal journal, ClockCoordinator? clock, ClockWorkerSlot clockWorkers)
    {
        _roundsMethods = roundsMethods;
        _control = control;
        _executor = executor;
        _journal = journal;
        _clock = clock;
        _clockWorkers = clockWorkers;
    }

    public static async Task<Session> CreateAsync(
        SessionConfig config,
        StoreJournal journal,
        IBoundaryNative native,
        INativeAuthority authority,
        IActionsWriter writer,
        Executor.IClock clock,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(native);
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(clock);
        if (config.Movement is { Writer: null })
        {
            throw new ArgumentException("complete movement capabilities required");
        }
        if (config.Clock is { } cc && (cc.Native is null || cc.Writer is null))
        {
            throw new ArgumentException("complete clock capabilities required");
        }
        if (config.Clock is null)
        {
            await BuildingRuntimePackage.RequireNoClockObligationsAsync(journal, ct).ConfigureAwait(false);
        }
        if (config.Telemetry is { } telemetry)
        {
            Tel.Emitter = telemetry;
        }
        var sink = new SessionSink { ClockWorkers = new ClockWorkerSlot() };
        var ns = await journal.IdentityAsync(ct).ConfigureAwait(false);
        var controlConfig = config.Control with
        {
            StopWrites = sink.Stop,
            CleanupWrites = sink.Cleanup,
            ResumeWrites = sink.Resume,
            // The world source is the host's (a facade ReadTick), never derived from the clock capability: the clock
            // native client reads a clock status, not the loaded world's identity.
            Worlds = config.Control.Worlds,
        };
        var control = await Control.CreateAsync(controlConfig, new StoreSessionIdentity(journal), authority, sink, ct).ConfigureAwait(false);

        async Task Cleanup()
        {
            using var shutdown = new CancellationTokenSource(config.Control.CallTimeout);
            await control.CloseAsync(shutdown.Token).ConfigureAwait(false);
        }

        try
        {
            lock (sink.Mu)
            {
                sink.Control = control;
            }
            var place = new BuildingBoundary(native, writer, clock, ns.Value);
            MovementBoundary? moves = null;
            if (config.Movement is not null)
            {
                moves = new MovementBoundary(config.Movement.Writer);
            }
            if (config.Haul is { } haulCaps && (haulCaps.Native is null || haulCaps.Writer is null))
            {
                throw new ControlException("NewSession: config.Haul != nil && (config.Haul.Native == nil || config.Haul.Writer == nil)");
            }
            if (config.Trade is { } tradeCaps && (tradeCaps.Native is null || tradeCaps.Writer is null))
            {
                throw new ControlException("NewSession: config.Trade != nil && (config.Trade.Native == nil || config.Trade.Writer == nil)");
            }
            var routine = new PlanAuthorizer(journal, config.RoundsMethods);
            var executorJournal = new ExecutorJournal(journal);
            var worker = moves is not null
                ? Executor.Executor.NewWithMovement(executorJournal, place, moves, clock, config.Executor, routine)
                : new Executor.Executor(executorJournal, place, clock, config.Executor, routine);
            // Each optional capability is wired directly onto worker with its own typed boundary value, never
            // discovered by a type test on a composed boundary.
            if (config.Trade is { } trade)
            {
                var tradeBoundary = new TradeBoundary(clock, trade);
                if (trade.Requests is not null)
                {
                    worker.EnableCommsTradeRequests(tradeBoundary);
                }
                worker.EnableTrade(tradeBoundary);
            }
            if (config.Haul is { } haul)
            {
                worker.EnableHaul(new HaulBoundary(haul.Native, haul.Writer, clock));
            }
            ClockCoordinator? coordinator = null;
            if (config.Clock is { } clockCaps)
            {
                coordinator = ClockCoordinator.Create(journal, clockCaps.Native, clockCaps.Writer, sink, clock, new ClockCoordinatorConfig { CallTimeout = config.Control.CallTimeout, JournalTimeout = config.Executor.JournalTimeout });
            }
            try
            {
                sink.Attach(worker, coordinator, ct);
            }
            catch
            {
                if (coordinator is not null)
                {
                    await coordinator.Stop(CancellationToken.None).ConfigureAwait(false);
                }
                throw;
            }
            return new Session(config.RoundsMethods, control, worker, journal, coordinator, sink.ClockWorkers);
        }
        catch
        {
            await Cleanup().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Binds explicit intent to the exact durable plan revision. A proposal without a stored plan cannot obtain a write lease.</summary>
    public async Task<GenerationSnapshot> AcquireAsync(GenerationSnapshot requested, CancellationToken ct, NativeGeneration? handoff = null)
    {
        var state = await _journal.LoadPlanAsync(requested.Plan, ct).ConfigureAwait(false);
        if (state.Spec.Revision != requested.Revision)
        {
            throw new Executor.AuthorityException();
        }
        return await _control.AcquireAsync(requested, ct, handoff).ConfigureAwait(false);
    }

    internal StoreJournal Journal => _journal;

    public ControlState State() => _control.State();

    public bool HoldsGrant(GenerationSnapshot scope) => _control.HoldsGrant(scope);

    public void Disable() => _control.Disable();

    /// <summary>Attaches only read reconciliation to a durable plan. It cannot obtain a lease, even if native status reports an active owner for this namespace.</summary>
    public async Task ObserveTargetAsync(GenerationSnapshot requested, CancellationToken ct)
    {
        var state = await _journal.LoadPlanAsync(requested.Plan, ct).ConfigureAwait(false);
        if (state.Spec.Revision != requested.Revision)
        {
            throw new Executor.AuthorityException();
        }
        await _control.ObserveTargetAsync(requested, ct).ConfigureAwait(false);
    }

    public Task RefreshAsync(CancellationToken ct) => _control.RefreshAsync(ct);

    public bool TargetsWorld(World world) => _control.TargetsWorld(world);

    public Task ManualAsync(CancellationToken ct) => _control.ManualAsync(ct);

    public Task ManualForResumeAsync(CancellationToken ct) => _control.ManualForResumeAsync(ct);

    /// <summary>Dispatches a plan's actions in one native Apply.</summary>
    public async Task<IReadOnlyList<Executor.BatchItem>> RunBatchAsync(PlanId plan, IReadOnlyList<ActionId> actions, CancellationToken ct)
    {
        // The plan's method intent rides with the writes the dispatch issues (Go withPlanIntent).
        using var intent = OperationIntentScope.Begin(await BuildingRuntimePackage.PlanIntentAsync(_journal, plan, ct).ConfigureAwait(false));
        return await _executor.RunBatchAsync(plan, actions, ct).ConfigureAwait(false);
    }

    public Task CloseAsync(CancellationToken ct) => _control.CloseAsync(ct);

    public bool RoundsMethodsEnabled() => _roundsMethods;
}

/// <summary>Relays authority changes between the control and the executor/clock it is wired to.</summary>
internal sealed partial class SessionSink : IAuthoritySink
{
    internal readonly object Mu = new();
    internal Executor.Executor? Executor;
    internal Control? Control;
    internal ClockCoordinator? Clock;
    internal ClockWorkerSlot ClockWorkers = null!;

    public void UpdateAuthority(Executor.Authority value)
    {
        lock (Mu)
        {
            if (Executor is null)
            {
                if (value.Enabled)
                {
                    throw new ControlException("UpdateAuthority: value.Enabled");
                }
                return;
            }
            var errors = new List<Exception>();
            try
            {
                Executor.UpdateAuthority(value);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                errors.Add(e);
            }
            if (Clock is not null)
            {
                try
                {
                    Clock.UpdateAuthority(value);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    errors.Add(e);
                }
            }
            if (errors.Count == 1)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(errors[0]).Throw();
            }
            if (errors.Count > 1)
            {
                throw new AggregateException(errors);
            }
        }
    }

    internal async Task Stop(CancellationToken ct)
    {
        var workerStopped = await ClockWorkers.Stop(ct).ConfigureAwait(false);
        Executor.Executor? e;
        ClockCoordinator? clock;
        lock (Mu)
        {
            e = Executor;
            clock = Clock;
        }
        if (e is null)
        {
            return;
        }
        var errors = new List<Exception>();
        try
        {
            await e.StopAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            errors.Add(ex);
        }
        if (clock is not null)
        {
            try
            {
                await clock.Stop(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add(ex);
            }
        }
        if (errors.Count == 1)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(errors[0]).Throw();
        }
        if (errors.Count > 1)
        {
            throw new AggregateException(errors);
        }
        if (clock is not null && !workerStopped)
        {
            await clock.Cleanup(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Joins the owed clock commands: an explicit Manual, a checkpoint save and shutdown. Drafted pawns stay drafted;
    /// with authority inactive the game's own auto-undraft applies again (Go sessionSink.cleanup).
    /// </summary>
    internal Task Cleanup(CancellationToken ct) => DrainAsync(ct);

    /// <summary>Cleanup before a resume in the same world (Go sessionSink.resume).</summary>
    internal Task Resume(CancellationToken ct) => DrainAsync(ct);

    private async Task DrainAsync(CancellationToken ct)
    {
        ClockCoordinator? clock;
        lock (Mu)
        {
            clock = Clock;
        }
        if (clock is not null)
        {
            await clock.Cleanup(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Publish only after the final fallible construction check. Until publication, failure cleanup releases the
    /// owner without draining unrelated durable work.
    /// </summary>
    internal void Attach(Executor.Executor worker, ClockCoordinator? clock, CancellationToken ct)
    {
        lock (Mu)
        {
            ct.ThrowIfCancellationRequested();
            Executor = worker;
            Clock = clock;
        }
    }
}

/// <summary>Adapts the journal's controller session id read to the control's identity source.</summary>
internal sealed class StoreSessionIdentity(StoreJournal store) : ISessionIdentity
{
    public async Task<ControllerSessionId> IdentityAsync(CancellationToken ct) => new((await store.IdentityAsync(ct).ConfigureAwait(false)).Value);
}

/// <summary>
/// The intent text the writes of one dispatch carry (Go <c>bridge.WithOperationIntent</c>): it is stamped as the
/// purpose of every action that has none. The scope follows the async flow like a Go context value.
/// </summary>
internal static class OperationIntentScope
{
    private static readonly AsyncLocal<string?> s_current = new();

    /// <summary>The intent of the dispatch in flight, empty when none was set (Go <c>OperationIntentFrom</c>).</summary>
    internal static string Current => s_current.Value ?? "";

    /// <summary>Names why the writes under the scope are issued; an empty intent leaves the scope as it was.</summary>
    internal static IDisposable Begin(string intent)
    {
        var previous = s_current.Value;
        if (intent.Length != 0)
        {
            s_current.Value = intent;
        }
        return new Restore(previous);
    }

    private sealed class Restore(string? previous) : IDisposable
    {
        public void Dispose() => s_current.Value = previous;
    }
}

internal static partial class BuildingRuntimePackage
{
    /// <summary>
    /// The method intent of a plan's writes; a plan no goal admitted, or a failed lookup, dispatches without one
    /// (Go <c>withPlanIntent</c>).
    /// </summary>
    internal static async Task<string> PlanIntentAsync(StoreJournal journal, PlanId plan, CancellationToken ct)
    {
        try
        {
            var method = await journal.PlanMethodAsync(plan, ct).ConfigureAwait(false);
            return method is null ? "" : OperationIntent.Of(method);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return "";
        }
    }

    /// <summary>Refuses a session without the clock capability while the journal still owes a clock command or cleanup (Go <c>requireNoClockObligations</c>).</summary>
    internal static async Task RequireNoClockObligationsAsync(StoreJournal journal, CancellationToken ct)
    {
        var attempts = await journal.LoadClockAttemptsAsync(4096, ct).ConfigureAwait(false);
        foreach (var v in attempts)
        {
            if (v.SupersededAt is null && v.Intent.Command.Start is not null && v.Phase is ClockPhase.Dispatched or ClockPhase.Uncertain)
            {
                throw new InvalidOperationException("clock recovery capability required for pending start");
            }
        }
        var epochs = await journal.LoadClockEpochsAsync(4096, ct).ConfigureAwait(false);
        foreach (var v in epochs)
        {
            if (v.Stage is not (ClockEpochStage.Paused or ClockEpochStage.Retired or ClockEpochStage.Superseded))
            {
                throw new InvalidOperationException("clock cleanup capability required for owned epoch");
            }
        }
    }
}
