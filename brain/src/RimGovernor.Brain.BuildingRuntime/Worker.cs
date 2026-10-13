using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;

namespace RimGovernor.Brain.BuildingRuntime;

public sealed class WorkerConfig
{
    public IBreakResponseSource? BreakSource { get; set; }

    /// <summary>
    /// Pawns and Moves drive the arrival hold (ArrivalHolds): an action that depends on a completed move waits
    /// until its pawn stands on the destination, and an idle drafted mover's order is sent again. Null Pawns
    /// disables the hold; null Moves never resends.
    /// </summary>
    public IArrivalPawns? Pawns { get; set; }

    public IActionsWriter? Moves { get; set; }
    public bool RoundsMethods { get; set; }
    public TimeSpan StepInterval { get; set; }
    public TimeSpan MaxBackoff { get; set; }
    public TimeSpan StepTimeout { get; set; }
    public TimeSpan RenewInterval { get; set; }
    public TimeSpan RenewTimeout { get; set; }

    /// <summary>
    /// Bounds the actions one step runs before it yields the player gate; zero means no count bound: the step
    /// budget (StepTimeout, and the longest dispatch so far) ends a step, not a number. Every eligible
    /// independent action is dispatched in the step that finds it. A dependent action still waits for its
    /// prerequisite's outcome, which Hands enforces.
    /// </summary>
    public int MaxDispatches { get; set; }

    /// <summary>Names attempts whose outcome the native clock latched; the next step reconciles them first and ignores their backoff. Null keeps the ticker cadence.</summary>
    public WakeSignal? Wake { get; set; }

    /// <summary>Called after a step that moved an action to a new stage, so the clock step loop reviews at once.</summary>
    public Action? Advanced { get; set; }

    /// <summary>
    /// The scheduler's decoded state store. A dispatch native refuses for a map-consuming kind (a placement or
    /// zone whose CAS token no longer matches) asks it to resync the planning window in full on its next
    /// refresh instead of trusting a delta; null asks nothing.
    /// </summary>
    public FactStore? Store { get; set; }

    /// <summary>
    /// Reports the scheduler's hint that its window is running; each native call the worker issues is recorded
    /// with it as a "dispatch" row, so a run can count the dispatches made live and the fraction native refused.
    /// </summary>
    public Func<bool>? WindowRunning { get; set; }

    /// <summary>
    /// The span context of the scheduler's latest step (ClockWorker.Trace). Each worker step is a span under it
    /// and each dispatch a span under the step. Null starts a trace per worker step.
    /// </summary>
    public Func<ActivityContext>? Trace { get; set; }

    /// <summary>
    /// The read validity of the scheduler's latest step (ClockScheduler.Validity): each dispatch runs under it,
    /// so its boundaries judge their reads by age class instead of the global drift. Null leaves the dispatches
    /// on the compatibility shim.
    /// </summary>
    public Func<(ReadValidity Validity, bool Ok)>? Validity { get; set; }

    /// <summary>
    /// Asks native to capture the snapshot frame the step's writes deferred (Bridge.FlushSnapshot): a step's
    /// dispatches send defer_snapshot and the step flushes once at its end. Null leaves every dispatch
    /// capturing its own frame.
    /// </summary>
    public Func<CancellationToken, Task>? Flush { get; set; }

    /// <summary>Told each dispatched action's progress after its step (the Rounder's placement index for the ledger's Attempt column).</summary>
    public Action<PlanId, DomainAction, ProgressView>? Placement { get; set; }
}

/// <summary>What a worker needs of a session beyond the player's: batch dispatch and read-only target attachment.</summary>
internal interface IWorkerSession : IPlayerSession
{
    Task<IReadOnlyList<Executor.BatchItem>> RunBatchAsync(PlanId plan, IReadOnlyList<ActionId> actions, CancellationToken ct);

    Task ObserveTargetAsync(GenerationSnapshot requested, CancellationToken ct);
}

/// <summary>
/// Owns Player's lifecycle, but its caller retains bridge and store handles until Close succeeds. Background
/// work cannot acquire player authority.
/// </summary>
public sealed partial class Worker
{
    /// <summary>Bounds the steps one wake or advance runs back to back.</summary>
    private const int BurstMax = 16;

    /// <summary>Bounds the woken set a single step carries forward.</summary>
    private const int FocusMax = 64;

    /// <summary>The end-of-step flush runs even when the step's own budget is spent.</summary>
    private static readonly TimeSpan FlushTimeout = TimeSpan.FromSeconds(5);

    /// <summary>How many consecutive steps an undispatched action's dispatch may be cancelled by its own context before the worker settles it cancelled.</summary>
    private const int CancelledSettle = 3;

    private readonly Player _player;
    private readonly IWorkerSession _session;
    private readonly WorkerConfig _config;
    private readonly CancellationTokenSource _lifetime;
    private readonly CancellationTokenRegistration _stopPlayer;
    private readonly CancellationTokenRegistration _stopContext;
    private readonly Task _done;

    // Only the step loop accesses scheduling state. The catalog bounds this map.
    private readonly Dictionary<ActionId, WorkerWait> _waits = [];

    // The plan of the last rotation-selected action: the next step starts at the first candidate of the plan
    // after it in the catalog, even once that plan has left the catalog (retired), so neither the catalog's
    // head nor one long plan can monopolise steps.
    private PlanId _cursor;
    private bool _cursorKnown;

    // Woken actions not yet reconciled since the wake.
    private readonly Dictionary<ActionId, WakeOutcome> _focus = [];

    // The last step moved an action to another stage, so a successor may have become dispatchable.
    private bool _advanced;

    private Worker(Player player, IWorkerSession session, WorkerConfig config, CancellationTokenSource lifetime)
    {
        _player = player;
        _session = session;
        _config = config;
        _lifetime = lifetime;
        _stopPlayer = player.Lifetime.Register(() => lifetime.Cancel());
        _stopContext = lifetime.Token.Register(() => { try { Stop(); } catch (Exception) { /* the Go AfterFunc discards the error */ } });
        _done = Task.Run(async () =>
        {
            await Steps().ConfigureAwait(false);
            try { Stop(); } catch (Exception) { /* discarded as in Go */ }
        });
    }

    private int DispatchBudget() => _config.MaxDispatches > 0 ? _config.MaxDispatches : int.MaxValue;

    /// <summary>
    /// Lists every action kind the worker (and, for a non-current plan, AuthorizeRoundsPlan) is allowed to
    /// dispatch or observe. Growing this list is how a new action family joins live automatic execution; each
    /// kind here already carries its own CAS-admission-guarded executor/boundary pair, so this is an allowlist
    /// of what has that machinery, not a bypass of it.
    /// </summary>
    internal static bool RoundsExecutableKind(ActionKind kind) => kind switch
    {
        ActionKind.Building or ActionKind.OwnedDraft or ActionKind.Subdue
            or ActionKind.SupplyAllow or ActionKind.SupplyForbid or ActionKind.WorkAssignment or ActionKind.Acquisition or ActionKind.AcquisitionWithdraw or ActionKind.ZoneCreate
            or ActionKind.ProductionBill or ActionKind.Tend or ActionKind.Rescue or ActionKind.Capture or ActionKind.UseItem or ActionKind.Haul or ActionKind.Equip or ActionKind.DropEquipment
            or ActionKind.GearReplace or ActionKind.ApparelPolicy or ActionKind.RecoveryService or ActionKind.Movement or ActionKind.Husbandry
            or ActionKind.PrisonerInteraction or ActionKind.Repair or ActionKind.Clean or ActionKind.MineAcquisition or ActionKind.OpenCasket
            or ActionKind.BuildingTemperature or ActionKind.BedUse or ActionKind.GrowerCrop or ActionKind.ClaimBuilding or ActionKind.AutoRefuel or ActionKind.Surgery or ActionKind.AutoHomeArea or ActionKind.PawnSettings or ActionKind.Area or ActionKind.PolicyPrune or ActionKind.ReadingPolicy or ActionKind.DrugPolicy or ActionKind.FoodPolicy or ActionKind.ZoneDelete or ActionKind.ZoneCellEdit or ActionKind.StockpilePatch or ActionKind.Assign or ActionKind.Excavation or ActionKind.DialogAnswer or ActionKind.ResearchSelect or ActionKind.Trade or ActionKind.CommsTradeRequest or ActionKind.Deconstruction or ActionKind.RemoveRoof or ActionKind.BuildRoof or ActionKind.AreaPlantCut or ActionKind.CutPlant or ActionKind.Strip or ActionKind.RulesAttach or ActionKind.MoveBuilding or ActionKind.UninstallBuilding or ActionKind.CoverClearance or ActionKind.WastepackHaul or ActionKind.FoundationRemoval or ActionKind.FloorRemoval or ActionKind.QuestAccept or ActionKind.QuestShuttle or ActionKind.HackDesignation or ActionKind.GiveItem or ActionKind.Ritual or ActionKind.IdeoligionReform or ActionKind.Ability or ActionKind.Ignite or ActionKind.Gathering or ActionKind.RemoveProductionBill or ActionKind.DoorControl or ActionKind.WallRemoval or ActionKind.CaravanDeparture => true,
        _ => false,
    };

    private sealed record WorkerCandidate(
        ProgressView View,
        PlanId Plan,
        ActionKind Kind,
        // what a dispatch inspects: the action's spec and the authority snapshot the plan dispatches under
        DomainAction Action,
        GenerationSnapshot Snapshot);

    private sealed record WorkerWait
    {
        public ProgressView? View { get; init; }
        public ControlState Scope { get; init; }
        public TimeSpan Delay { get; init; }
        public DateTimeOffset Until { get; init; }

        // The last surfaced "stage/refusals/error" of this action, so a sustained hold logs once instead of
        // every step (see issue #70); Repeats counts the unlogged runs that restated it, reported when the
        // outcome next changes so a reader can see how long the hold lasted.
        public string Outcome { get; init; } = "";
        public int Repeats { get; init; }

        // The run was held on stale facts (HeldStale): the next stop is a new observation, so it is retried there at once.
        public bool Stale { get; init; }

        // Consecutive steps whose dispatch of this undispatched action lost its own call context while the step's was live.
        public int Cancelled { get; init; }
    }

    /// <summary>
    /// Keys one action run by what a reader of the log needs to diagnose a stuck action: its stage, the
    /// executor's refusal reasons and the error. Refusal reasons matter most -- an owned draft held before
    /// prepare otherwise leaves no trace but a bare "building execution held".
    /// </summary>
    internal static string WorkerOutcome(ProgressView after, ExecutorResult result, Exception? err)
    {
        var reasons = WorkerRefusedReasons(result);
        // The receipt and effect name why an attempt is unresolved: an unknown receipt is a native call that
        // timed out on the controller side, and with the attempt number it identifies the native ledger entry.
        var receipt = after.Receipt.TryGetValue(out var r) ? Wire.Name(r) : "-";
        var effect = after.Effect.TryGetValue(out var e) ? Wire.Name(e) : "-";
        return string.Create(CultureInfo.InvariantCulture, $"stage={Wire.Name(after.Stage)} attempt={after.Attempt.Value} receipt={receipt} effect={effect} refused=[{string.Join(",", reasons)}] err={(err is null ? "<nil>" : err.Message)}");
    }

    /// <summary>Lists the refusal reasons of a run's result: the outcome text's refused=[...] and the dispatch row's refused attr.</summary>
    internal static List<string> WorkerRefusedReasons(ExecutorResult result)
    {
        var reasons = new List<string>(result.Refused.Count);
        foreach (var r in result.Refused)
        {
            reasons.Add(r.Reason.Value);
        }
        return reasons;
    }

    public static Worker Create(WorkerConfig config, Player? player, Session? session, CancellationToken ct)
    {
        if (session is null || player is null || !ReferenceEquals(player.Session, session) || !ReferenceEquals(player.Journal, session.Journal))
        {
            throw new ControlException("NewWorker: session == nil || player == nil || player.session != session || player.journal != session.journal");
        }
        if (config.RoundsMethods && !session.RoundsMethodsEnabled())
        {
            throw new ControlException("NewWorker: config.RoundsMethods && !session.roundsMethods");
        }
        return CreateInternal(config, player, session, ct);
    }

    internal static Worker CreateInternal(WorkerConfig config, Player? player, IWorkerSession? session, CancellationToken ct)
    {
        if (player is null || session is null || !ReferenceEquals(player.Session, session) || config.StepInterval <= TimeSpan.Zero || config.MaxBackoff < config.StepInterval || config.MaxBackoff > TimeSpan.FromMinutes(1) || config.StepTimeout <= TimeSpan.Zero || config.StepTimeout > player.Config.CallTimeout)
        {
            throw new ControlException("newWorker: player == nil || session == nil || player.session != session || config.StepInterval <= 0 || config.MaxBacko");
        }
        lock (player.Mu)
        {
            if (player.WorkerAttached || player.Closing || player.Closed || player.Lifetime.IsCancellationRequested)
            {
                throw new ControlException("newWorker: player.workerAttached || player.closing || player.closed || player.lifetime.Err() != nil");
            }
            ct.ThrowIfCancellationRequested();
            var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
            player.WorkerAttached = true;
            return new Worker(player, session, config, lifetime);
        }
    }

    /// <summary>Rejects new player requests before waiting for a possibly slow native call.</summary>
    private void Stop()
    {
        var p = _player;
        lock (p.Mu)
        {
            if (p.Closed || p.Closing)
            {
                return;
            }
            p.Closing = true;
            p.CancelEpoch();
            p.Session.Disable();
        }
    }

    public async Task Close(CancellationToken ct)
    {
        _lifetime.Cancel();
        Exception? disableErr = null;
        try
        {
            Stop();
        }
        catch (Exception e)
        {
            disableErr = e;
        }
        try
        {
            await _done.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException oce)
        {
            throw Join(disableErr, oce)!;
        }
        // Player.Close already serializes concurrent closers through its gate.
        _stopPlayer.Dispose();
        _stopContext.Dispose();
        Exception? closeErr = null;
        try
        {
            await _player.CloseAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            closeErr = e;
        }
        var joined = Join(disableErr, closeErr);
        if (joined is not null)
        {
            ExceptionDispatchInfo.Capture(joined).Throw();
        }
    }

    private static Exception? Join(params Exception?[] errors)
    {
        var present = errors.Where(e => e is not null).Select(e => e!).ToList();
        return present.Count switch
        {
            0 => null,
            1 => present[0],
            _ => new AggregateException(present),
        };
    }

    private async Task Steps()
    {
        var ct = _lifetime.Token;
        var burst = 0;
        while (true)
        {
            if (ct.IsCancellationRequested)
            {
                return;
            }
            try
            {
                await Step(DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The Go step loop discards the step's error; the dispatch rows and the telemetry carry it.
            }
            burst++;
            // A wake steps at once; while focused actions remain, keep stepping so each of them is reconciled
            // without waiting a StepInterval, and a step that advanced an action steps again so the successor
            // it unblocked dispatches before the clock readmits a window rather than a StepInterval or its own
            // backoff later.
            if ((_focus.Count > 0 || _advanced) && burst < BurstMax)
            {
                continue;
            }
            burst = 0;
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            try
            {
                await Task.WhenAny(Task.Delay(_config.StepInterval, wait.Token), WakeSignal.Wait(_config.Wake, wait.Token)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            wait.Cancel();
            if (ct.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private void TakeWake()
    {
        if (_config.Wake is not { } wake)
        {
            return;
        }
        var (woken, _) = wake.Take();
        if (woken.Count == 0)
        {
            return;
        }
        // Latched outcomes change what every other action in their plans is waiting on: drop the backoffs so
        // successors are reconsidered at once.
        _waits.Clear();
        foreach (var (id, outcome) in woken)
        {
            if (_focus.Count >= FocusMax)
            {
                break;
            }
            _focus[id] = outcome;
            _waits.Remove(id);
        }
    }

    private bool FocusNamed(ActionId id) => _focus.ContainsKey(id);

    /// <summary>One Worker step as a span under the scheduler's latest step (a trace root of its own when there is none).</summary>
    internal async Task Step(DateTimeOffset now, CancellationToken ct)
    {
        var parent = _config.Trace is { } trace ? trace() : default;
        using var span = Tel.StartSpan("buildingruntime", "worker.step", parent);
        try
        {
            await RunStep(now, ct).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Tel.EndSpan(span, e);
            throw;
        }
        Tel.EndSpan(span, null);
    }

    private async Task RunStep(DateTimeOffset now, CancellationToken ct)
    {
        _advanced = false;
        TakeWake();
        // StepTimeout budgets the dispatches, not the wait for the player gate: a scheduler step holds the gate
        // for its whole planner wave, and a budget that started before the wait left the step 0-3 s for its
        // actions and killed the last one at the deadline.
        using var gate = await _player.EnterAsync("worker", false, ct).ConfigureAwait(false);
        var epoch = gate.Epoch;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(gate.Call);
        budget.CancelAfter(_config.StepTimeout);
        var call = budget.Token;
        var deadline = DateTimeOffset.UtcNow + _config.StepTimeout;
        IDisposable? validityScope = null;
        if (_config.Validity is { } validityFn && validityFn() is { Ok: true } validityRead)
        {
            validityScope = ReadValidityScope.With(validityRead.Validity);
        }
        using var _validity = validityScope;
        World world = default;
        Exception? worldErr = null;
        try
        {
            world = await _player.Worlds.ReadWorldAsync(call).ConfigureAwait(false);
            world.Validate();
        }
        catch (Exception e)
        {
            worldErr = e;
        }
        if (worldErr is not null)
        {
            // A read that ran out of the step's budget (a scheduler step held the gate for most of it) or
            // failed in transport says nothing about the world: the step fails and the next one reads again.
            // Only a world that reads back unavailable or different is evidence against the authority.
            if (call.IsCancellationRequested || worldErr is OperationCanceledException or TimeoutException or System.Net.Http.HttpRequestException or System.IO.IOException)
            {
                ExceptionDispatchInfo.Capture(worldErr).Throw();
            }
            try
            {
                _session.Disable();
            }
            catch (Exception e)
            {
                throw Join(worldErr, e)!;
            }
        }
        _player.Current(call, epoch);
        var scope = _session.State();
        // Identity is a prerequisite for retaining permission, not just dispatch. Keep the private cleanup
        // scope, but stop renewal of an unavailable or different world before considering any read-only
        // reconciliation target.
        if ((scope.ObservationKnown && Player.PlayerWorld(scope.Snapshot) != world) || (scope.Enabled && !scope.ObservationKnown))
        {
            _session.Disable();
            scope = _session.State();
        }
        var plans = await _player.Journal.LoadPlansAsync(call).ConfigureAwait(false);
        var breakHeld = new HashSet<ActionId>();
        if (scope.Enabled && scope.ObservationKnown)
        {
            breakHeld = await BreakDispatchHoldsAsync(scope.Snapshot, plans, call).ConfigureAwait(false);
            if (_config.Pawns is not null)
            {
                foreach (var id in await ArrivalHoldsAsync(scope.Snapshot, plans, call).ConfigureAwait(false))
                {
                    breakHeld.Add(id);
                }
            }
        }
        var live = new HashSet<ActionId>();
        var candidates = new List<WorkerCandidate>();
        // One author: the root plan carries authority, and every other plan -- routine method or player
        // submission -- is dispatched under it once authorized. There is no priority arbitration between them.
        foreach (var plan in plans)
        {
            var planSnapshot = scope.Snapshot;
            if (scope.Enabled && scope.ObservationKnown && plan.Spec.Id != scope.Snapshot.Plan)
            {
                var target = scope.Snapshot with { Plan = plan.Spec.Id, Revision = plan.Spec.Revision };
                try
                {
                    await new PlanAuthorizer(_player.Journal, _config.RoundsMethods).AuthorizeRoundsPlanAsync(scope.Snapshot, target, call).ConfigureAwait(false);
                    planSnapshot = target;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // Not authorized: the plan stays under the root's snapshot and is judged ineligible below.
                }
            }
            var planScope = scope with { Snapshot = planSnapshot };
            foreach (var progress in plan.Progress)
            {
                var view = progress.View;
                if (!view.Unresolved && breakHeld.Contains(view.Action))
                {
                    continue;
                }
                var roundsObservation = _config.RoundsMethods && view.Unresolved && RoundsExecutableKind(progress.Action.Kind) && Player.PlayerWorld(view.Snapshot) == world;
                if (worldErr is null && (roundsObservation || WorkerEligible(plan, view, planScope, world)))
                {
                    live.Add(view.Action);
                    candidates.Add(new WorkerCandidate(view, plan.Spec.Id, progress.Action.Kind, progress.Action, planScope.Snapshot));
                }
            }
        }
        foreach (var id in _waits.Keys.Where(id => !live.Contains(id)).ToList())
        {
            _waits.Remove(id);
        }
        foreach (var id in _focus.Keys.Where(id => !live.Contains(id)).ToList())
        {
            _focus.Remove(id);
        }
        // Catalog order is stable; rotate by plan, including on failed reads, so an unavailable attempt cannot
        // monopolize reconciliation. The rotation resumes at the first candidate of the plan after the last
        // selection's, whether or not that plan still lists one: rotating by action gave a forty-action shell
        // plan every step until its last action and restarted at the catalog's head whenever the selected
        // action completed, so the work-assignment plan sorted after the shell never had a turn. Within a plan
        // the first candidate is its earliest action, so a sequential plan still progresses in order. Woken
        // actions come first: the native clock latched their outcome, so reconciling them is the reason this
        // step runs; they leave the rotation where it was.
        var start = 0;
        if (_cursorKnown)
        {
            start = candidates.Count;
            for (var i = 0; i < candidates.Count; i++)
            {
                if (candidates[i].Plan.CompareTo(_cursor) > 0)
                {
                    start = i;
                    break;
                }
            }
            if (candidates.Count > 0)
            {
                start %= candidates.Count;
            }
        }
        var ordered = new List<WorkerCandidate>(candidates.Count);
        foreach (var focused in new[] { true, false })
        {
            for (var n = 0; n < candidates.Count; n++)
            {
                var candidate = candidates[(start + n) % candidates.Count];
                if (FocusNamed(candidate.View.Action) == focused)
                {
                    ordered.Add(candidate);
                }
            }
        }
        // Every candidate off its backoff dispatches in this step, up to the dispatch budget, so a plan's
        // independent actions reach native in one gate hold rather than one per scheduler round. A dependent
        // action Hands holds until its prerequisite's outcome stays a held result here and backs off like any other.
        var dispatched = 0;
        var errs = new List<Exception>();
        // One flush per step that sent a deferred write, on every exit: a yield, an error, or the end of the candidates.
        var sent = false;
        try
        {
            // A dispatch that starts with less of the step's budget left than the longest dispatch so far took
            // would lose its native call to the deadline with the receipt unknown (a reconcile round trip
            // later); the step yields instead and the next one starts it whole.
            var longest = TimeSpan.Zero;
            // A plan's building candidates go out together as one batched Apply: the batch is one dispatch
            // against the budget and the longest estimate, and takes every eligible building candidate of its plan.
            var taken = new HashSet<ActionId>();
            for (var at = 0; at < ordered.Count; at++)
            {
                var lead = ordered[at];
                if (dispatched >= DispatchBudget())
                {
                    break;
                }
                if (dispatched > 0 && deadline - DateTimeOffset.UtcNow < longest)
                {
                    break;
                }
                if (taken.Contains(lead.View.Action) || BackedOff(lead, scope, now))
                {
                    continue;
                }
                var group = new List<WorkerCandidate> { lead };
                if (WorkerBatched(lead))
                {
                    foreach (var next in ordered.Skip(at + 1))
                    {
                        if (next.Plan == lead.Plan && WorkerBatched(next) && !taken.Contains(next.View.Action) && !BackedOff(next, scope, now))
                        {
                            group.Add(next);
                        }
                    }
                }
                dispatched++;
                var focusedAt = new bool[group.Count];
                for (var i = 0; i < group.Count; i++)
                {
                    var member = group[i];
                    taken.Add(member.View.Action);
                    focusedAt[i] = FocusNamed(member.View.Action);
                    _focus.Remove(member.View.Action);
                    if (!focusedAt[i])
                    {
                        _cursor = lead.Plan;
                        _cursorKnown = true;
                    }
                }
                _player.Current(call, epoch);
                Exception? err = null;
                try
                {
                    if (!scope.Enabled)
                    {
                        await _session.ObserveTargetAsync(lead.View.Snapshot, call).ConfigureAwait(false);
                    }
                    _player.Current(call, epoch);
                }
                catch (Exception e) when (e is not OperationCanceledException || !call.IsCancellationRequested)
                {
                    err = e;
                }
                var results = new ExecutorResult[group.Count];
                for (var i = 0; i < results.Length; i++)
                {
                    results[i] = new ExecutorResult();
                }
                var resultErrs = new Exception?[group.Count];
                var dispatchStarted = Stopwatch.StartNew();
                var running = _config.WindowRunning is { } wr && wr();
                using var dispatch = Tel.StartSpan("buildingruntime", "hands.dispatch",
                    ("rimgovernor.action_kind", Wire.Name(lead.Kind)), ("rimgovernor.actions", group.Count));
                var tally = new ReadTally();
                using var tallyScope = tally.Begin();
                if (err is not null)
                {
                    for (var i = 0; i < group.Count; i++)
                    {
                        resultErrs[i] = err;
                    }
                }
                else
                {
                    IDisposable? deferred = null;
                    if (_config.Flush is not null)
                    {
                        deferred = DeferredSnapshotScope.Begin();
                        sent = true;
                    }
                    using var _deferred = deferred;
                    (results, resultErrs) = await RunBatch(lead.View.Plan, group, call).ConfigureAwait(false);
                }
                DispatchSpans.EndDispatchSpan(dispatch, results, resultErrs);
                for (var i = 0; i < group.Count; i++)
                {
                    Metrics.RecordDispatchOutcome(group[i].Kind, results[i], resultErrs[i]);
                }
                if (dispatchStarted.Elapsed > longest)
                {
                    longest = dispatchStarted.Elapsed;
                }
                foreach (var result in results)
                {
                    if (result.NativeCalled)
                    {
                        // The decoded store drops what the dispatch changed: only a clock events page would
                        // drop it otherwise, and a next decision replans from the rows before this write.
                        var (everything, families) = BuildingRuntimePackage.OperationFamilies(lead.Kind, true);
                        if (everything)
                        {
                            _config.Store?.InvalidateAll();
                        }
                        else
                        {
                            _config.Store?.InvalidateFamily([.. families]);
                        }
                        break;
                    }
                }
                var stop = false;
                for (var i = 0; i < group.Count; i++)
                {
                    var candidate = group[i];
                    var v = candidate.View;
                    _waits.TryGetValue(v.Action, out var wait);
                    wait ??= new WorkerWait();
                    var result = results[i];
                    var rerr = resultErrs[i];
                    var after = v;
                    if (result.Progress is { } resultProgress && resultProgress.View.Action == v.Action)
                    {
                        after = resultProgress.View;
                    }
                    _config.Placement?.Invoke(candidate.Plan, candidate.Action, after);
                    var stale = WorkerHeldStale(after, result, rerr);
                    var delay = _config.StepInterval;
                    if (WorkerSameView(after, v) && wait.View is { } wv && WorkerSameView(wv, v) && wait.Scope == WorkerScope(scope))
                    {
                        var doubled = wait.Delay * 2;
                        var cap = WorkerBackoffCap(_config, after);
                        delay = doubled < cap ? doubled : cap;
                    }
                    if (rerr is null && after.Stage != v.Stage)
                    {
                        _advanced = true;
                    }
                    // A dispatch held on stale facts (the authority or tick moved under its inspection)
                    // clears on the next observation, so it is retried at once, off its backoff, before the
                    // game's own work scanner takes the order's target; the clock is not held for it, since
                    // every routine kind dispatches under the running window. One retry per hold: a hold that
                    // survives it backs off as usual.
                    if (stale && !wait.Stale)
                    {
                        _focus[v.Action] = new WakeOutcome(v.Action, after.Attempt);
                    }
                    // One line per change of outcome, in either log: a refusal that repeats verbatim on every
                    // retry (a CAS token that never matches, a native read refused for the same reason) would
                    // otherwise dominate the run's log without adding anything a reader can act on.
                    var outcome = WorkerOutcome(after, result, rerr);
                    var repeats = wait.Repeats;
                    var changed = outcome != wait.Outcome;
                    if (changed || result.NativeCalled)
                    {
                        WorkerDispatchRow(tally, v, after, result, running, stale, rerr, changed, repeats);
                    }
                    repeats = changed ? 0 : repeats + 1;
                    var cancelled = 0;
                    var own = WorkerOwnCancel(rerr, call) && after.Attempt == v.Attempt && after.Stage is Stage.Pending or Stage.Prepared;
                    if (own)
                    {
                        cancelled = 1;
                        if (wait.View is { } cv && WorkerSameView(cv, v))
                        {
                            cancelled = wait.Cancelled + 1;
                        }
                    }
                    // An undispatched action whose dispatch is cancelled step after step never reaches native;
                    // parked at attempt 0 it would hold its goal for ever. It settles cancelled, so the goal re-plans.
                    if (cancelled >= CancelledSettle)
                    {
                        try
                        {
                            await _player.Journal.CancelAsync(v.Plan, v.Action, call).ConfigureAwait(false);
                            Tel.Decide(new Decision
                            {
                                Kind = "dispatch",
                                Component = "worker",
                                Level = TelemetryLevel.Warn,
                                Target = v.Action.Value,
                                Verdict = "failed",
                                Reason = "cancelled_settled",
                                Attrs = new Dictionary<string, object?> { ["stage"] = Wire.Name(v.Stage), ["attempts"] = cancelled, ["error"] = rerr },
                            });
                            _waits.Remove(v.Action);
                            _advanced = true;
                            continue;
                        }
                        catch (Exception cancelErr) when (cancelErr is not OperationCanceledException || !call.IsCancellationRequested)
                        {
                            errs.Add(cancelErr);
                        }
                    }
                    _waits[v.Action] = new WorkerWait { View = after, Scope = WorkerScope(_session.State()), Delay = delay, Until = now + delay, Outcome = outcome, Repeats = repeats, Stale = stale, Cancelled = cancelled };
                    if (rerr is not null)
                    {
                        errs.Add(rerr);
                        // A step that ran out of budget or lost its transport says nothing about the next
                        // candidate either; one dispatch cancelled by its own context says nothing about its siblings.
                        if (!own && (call.IsCancellationRequested || rerr is OperationCanceledException or TimeoutException or System.Net.Http.HttpRequestException or System.IO.IOException))
                        {
                            stop = true;
                        }
                    }
                }
                if (stop)
                {
                    break;
                }
            }
            if (_advanced)
            {
                _config.Advanced?.Invoke();
            }
        }
        finally
        {
            if (sent)
            {
                await Flush().ConfigureAwait(false);
            }
        }
        var all = new List<Exception?> { worldErr };
        all.AddRange(errs);
        var joined = Join([.. all]);
        if (joined is not null)
        {
            ExceptionDispatchInfo.Capture(joined).Throw();
        }
    }

    /// <summary>Asks native to capture the step's deferred writes. A failure is ignored (the native call records it): native captures them anyway within its 1 s safety net.</summary>
    private async Task Flush()
    {
        using var cts = new CancellationTokenSource(FlushTimeout);
        try
        {
            await _config.Flush!(cts.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Whether the candidate joins its plan's other plain intents in one batched Apply; other kinds each dispatch alone.</summary>
    private static bool WorkerBatched(WorkerCandidate candidate) => Executor.Executor.PlainIntent(candidate.Kind);

    /// <summary>
    /// Sends the group, one plan's candidates, as one RunBatch and returns each member's result and error in
    /// group order. A batch cancelled by its own context while the step's is live retries once, whole.
    /// </summary>
    private async Task<(ExecutorResult[] Results, Exception?[] Errors)> RunBatch(PlanId plan, List<WorkerCandidate> group, CancellationToken call)
    {
        var ids = group.Select(m => m.View.Action).ToList();
        var results = new ExecutorResult[group.Count];
        var errs = new Exception?[group.Count];
        for (var attempt = 0; attempt < 2; attempt++)
        {
            IReadOnlyList<Executor.BatchItem> items = [];
            Exception? err = null;
            try
            {
                items = await _session.RunBatchAsync(plan, ids, call).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                err = e;
            }
            var ownCancel = WorkerOwnCancel(err, call);
            for (var i = 0; i < group.Count; i++)
            {
                results[i] = new ExecutorResult();
                errs[i] = err;
                if (i < items.Count)
                {
                    results[i] = items[i].Result;
                    if (items[i].Error is { } itemErr)
                    {
                        errs[i] = itemErr;
                    }
                }
                ownCancel = ownCancel || WorkerOwnCancel(errs[i], call);
            }
            if (!ownCancel)
            {
                break;
            }
        }
        return (results, errs);
    }

    /// <summary>Whether err is the dispatch's own cancellation while the step's context is still live.</summary>
    private static bool WorkerOwnCancel(Exception? err, CancellationToken call) =>
        err is OperationCanceledException && !call.IsCancellationRequested;

    /// <summary>Whether the candidate waits out a backoff from an earlier step's identical outcome; a woken action never does.</summary>
    private bool BackedOff(WorkerCandidate candidate, ControlState scope, DateTimeOffset now)
    {
        var v = candidate.View;
        _waits.TryGetValue(v.Action, out var wait);
        return !FocusNamed(v.Action) && wait?.View is { } wv && WorkerSameView(wv, v) && wait.Scope == WorkerScope(scope) && now < wait.Until;
    }

    /// <summary>
    /// Publishes the action's dispatch row: one per run that reached native or whose outcome changed. A native
    /// run carries the receipt the run left (accepted, refused, unknown; "-" when the write was not a
    /// dispatch), whether the scheduler's window was running when the run began and the read tally
    /// (`rimgovernor phases` sums those rows, Bridge.DispatchSample); an outcome change carries `changed` and
    /// how many unrecorded runs restated the previous outcome, so a refusal that repeats verbatim on every
    /// retry writes once. An action waiting on a prerequisite that has not completed is the plan sequencing
    /// itself, not a failure, so only another error is WARN. Stale marks a run held on stale facts so a run can
    /// count the holds the read bounds refused.
    /// </summary>
    internal static void WorkerDispatchRow(ReadTally tally, ProgressView before, ProgressView after, ExecutorResult result, bool running, bool stale, Exception? err, bool changed, int repeats)
    {
        var refused = WorkerRefusedReasons(result);
        var attrs = new Dictionary<string, object?>
        {
            ["attempt"] = (long)after.Attempt.Value,
            ["stage"] = Wire.Name(before.Stage),
            ["stage_after"] = Wire.Name(after.Stage),
            ["refused"] = refused,
            ["stale"] = stale,
            ["changed"] = changed,
            ["repeated"] = repeats,
        };
        var d = new Decision
        {
            Kind = "dispatch",
            Component = "worker",
            Target = before.Action.Value,
            Verdict = "waiting",
            Reason = Wire.Name(after.Stage),
            Attrs = attrs,
        };
        if (err is DependencyException)
        {
            d = d with { Reason = "dependency" };
        }
        else if (err is not null && stale)
        {
            d = d with { Verdict = "held" };
            d = d with { Reason = "stale" };
        }
        else if (err is not null)
        {
            d = d with { Verdict = "failed" };
            d = d with { Reason = "error" };
        }
        else if (refused.Count > 0)
        {
            d = d with { Verdict = "refused" };
            d = d with { Reason = refused[0] };
        }
        else if (after.Stage == Stage.Completed)
        {
            d = d with { Verdict = "completed" };
            d = d with { Reason = "completed" };
        }
        if (err is not null)
        {
            attrs["error"] = err.Message;
            if (err is not DependencyException)
            {
                d = d with { Level = TelemetryLevel.Warn };
            }
        }
        if (!result.NativeCalled)
        {
            Tel.Decide(d);
            return;
        }
        var receipt = "-";
        if (after.Receipt.TryGetValue(out var rv) && (after.Attempt != before.Attempt || !WorkerSameReceipt(before, after)))
        {
            receipt = Wire.Name(rv);
        }
        attrs["receipt"] = receipt;
        attrs["running"] = running;
        if (after.Refusal.TryGetValue(out var refusal) && receipt == Wire.Name(Domain.Receipt.Refused))
        {
            attrs["refusal_class"] = Wire.Name(refusal.Class);
            attrs["refusal_reason"] = refusal.Reason;
        }
        if (before.Stage is Stage.Pending or Stage.Prepared && after.Attempt.Value != 0 && after.Stage is not (Stage.Pending or Stage.Prepared))
        {
            attrs["dispatch_tick"] = after.Tick.Value;
        }
        tally.Publish(Tel.Emitter, d);
    }

    private static bool WorkerSameReceipt(ProgressView a, ProgressView b)
    {
        var xk = a.Receipt.TryGetValue(out var x);
        var yk = b.Receipt.TryGetValue(out var y);
        return xk == yk && x == y;
    }

    /// <summary>
    /// Whether two views of one action describe the same reconciliation state. The observed tick advances with
    /// every read of a running game, so it is not a change; a read that only restated an unresolved effect must
    /// back off like any other unchanged attempt, or a handful of unresolvable observations monopolise the
    /// one-action-per-step rotation and starve the plans that could progress.
    /// </summary>
    internal static bool WorkerSameView(ProgressView a, ProgressView b) => (a with { Tick = default }) == (b with { Tick = default });

    /// <summary>
    /// Bounds the retry delay of an unchanged attempt. An observation the native side could not classify
    /// (EffectUnknown: the source or output is gone, the pawn is unobservable) rarely resolves within the
    /// ordinary cap and is rechecked at most once a minute; everything else keeps the configured cap so pending
    /// work is noticed promptly.
    /// </summary>
    internal static TimeSpan WorkerBackoffCap(WorkerConfig config, ProgressView v)
    {
        if (v.Effect.TryGetValue(out var effect) && effect == Effect.Unknown && v.Stage == Stage.AwaitingObservation)
        {
            var scaled = config.MaxBackoff * 6;
            var minute = TimeSpan.FromMinutes(1);
            var capped = scaled < minute ? scaled : minute;
            return config.MaxBackoff > capped ? config.MaxBackoff : capped;
        }
        return config.MaxBackoff;
    }

    /// <summary>
    /// A run held before dispatch on facts from another world or generation, or a tick behind what was
    /// admitted: the executor's stale_facts refusal, or an emergency hold recorded for the same reason. Such a
    /// hold is expected to clear on the next observation, unlike a refusal that names a world condition (stock,
    /// geometry, an unavailable pawn).
    /// </summary>
    internal static bool WorkerHeldStale(ProgressView after, ExecutorResult result, Exception? err)
    {
        if (err is not Executor.HeldException || after.Unresolved || after.Stage is not (Stage.Pending or Stage.Prepared))
        {
            return false;
        }
        foreach (var refusal in result.Refused)
        {
            if (refusal.Reason == Pol.Reason.StaleFacts)
            {
                return true;
            }
        }
        if (result.Refused.Count > 0)
        {
            return false;
        }
        if (!after.HeldReason.TryGetValue(out var held))
        {
            return false;
        }
        return held.Reasons().Contains(HeldReason.StaleFacts);
    }

    internal static bool WorkerEligible(Store.PlanState plan, ProgressView v, ControlState scope, World world)
    {
        if (plan.Spec.Action(v.Action) is not { } action || !RoundsExecutableKind(action.Kind))
        {
            return false;
        }
        if (scope.Enabled)
        {
            if (!scope.ObservationKnown || Player.PlayerWorld(scope.Snapshot) != world || scope.Snapshot.Plan != v.Plan || scope.Snapshot.Revision != v.Revision)
            {
                return false;
            }
        }
        else
        {
            return v.Unresolved && Player.PlayerWorld(v.Snapshot) == world;
        }
        if (v.Unresolved)
        {
            return Player.PlayerWorld(v.Snapshot) == world;
        }
        if (v.Stage is not (Stage.Pending or Stage.Prepared))
        {
            return false;
        }
        // A Prepared action has no write outstanding (dispatch is journaled before the native call), so one
        // whose snapshot went stale -- the step budget cancelled its dispatch and the native generation then
        // moved -- is re-prepared under the current scope by the next run rather than stranded behind an
        // authority it can never dispatch against.
        if (v.Attempt.Value == 0)
        {
            return true;
        }
        // A trusted refusal is a no-effect proof. Only a later resume (a newer native generation) authorizes
        // another attempt; an unknown receipt always stays reconciliation. A write the transport never issued
        // refused nothing, so the same generation may retry it.
        var known = v.Receipt.TryGetValue(out var receipt);
        // An intent whose Apply receipt came back unknown is back at Pending for a resend. Like a refusal, only
        // a later resume (a newer native generation) resends it; native answers applied if the first write
        // landed, so the resend cannot duplicate the building.
        if (known && receipt == Domain.Receipt.Unknown && v.Stage == Stage.Pending)
        {
            return Player.PlayerWorld(v.Snapshot) == world && scope.Snapshot.Native > v.Snapshot.Native;
        }
        var effectKnown = v.Effect.TryGetValue(out var effect);
        if (!known || !effectKnown || effect != Effect.Absent || Player.PlayerWorld(v.Snapshot) != world)
        {
            return false;
        }
        // A refused attempt already re-prepared carries the newer generation that authorized it, so it stays
        // eligible until it dispatches.
        return receipt == Domain.Receipt.Unsent || (receipt == Domain.Receipt.Refused && (v.Stage == Stage.Prepared || scope.Snapshot.Native > v.Snapshot.Native));
    }

    /// <summary>Read reconciliation may rotate plan targets without changing world authority. That rotation must not reset a paused attempt's cooldown.</summary>
    internal static ControlState WorkerScope(ControlState scope)
    {
        if (!scope.Enabled)
        {
            return scope with { Snapshot = scope.Snapshot with { Plan = default, Revision = default } };
        }
        return scope;
    }

    /// <summary>
    /// Whether the plan still holds the draft v, either completed or still in flight (dispatched, receipt
    /// accepted, awaiting the observation a hold interrupted): no other action of the plan has failed or been
    /// cancelled, and at least one is still unfinished. An order riding on another draft (a move or a subdue
    /// naming a different draft action) says nothing about this one: a timed-out move for one defender must not
    /// undraft the others mid-order. An order riding on this draft that native is still executing holds it
    /// outright: a recovered goal cancels the plan's unissued orders (settleUnissuedWork) and leaves the
    /// dispatched ones to close on their own, which they cannot once the pawn is undrafted under them. A
    /// completed subdue order stands on its draft the same way: its intent receipt is terminal while native
    /// keeps fighting, so the draft is held until the planner cancels the order (releaseBreakWork, once the
    /// victim is downed or out of the break).
    /// </summary>
    internal static bool WorkerPlanHoldsDraft(Store.PlanState plan, ProgressView v)
    {
        if (v.Stage is not (Stage.Completed or Stage.Dispatched or Stage.AwaitingObservation))
        {
            return false;
        }
        var unfinished = false;
        var settled = false;
        foreach (var p in plan.Progress)
        {
            var other = p.View;
            if (other.Action == v.Action)
            {
                continue;
            }
            var rides = WorkerOrderDraft(p.Action);
            if (rides is { } r && r != v.Action)
            {
                continue;
            }
            switch (other.Stage)
            {
                case Stage.Cancelled or Stage.Unsuccessful:
                    settled = true;
                    break;
                case Stage.Dispatched or Stage.AwaitingObservation:
                    if (rides == v.Action)
                    {
                        return true;
                    }
                    unfinished = true;
                    break;
                case Stage.Pending or Stage.Prepared:
                    unfinished = true;
                    break;
                case Stage.Completed:
                    if (rides == v.Action && p.Action.Kind == ActionKind.Subdue)
                    {
                        return true;
                    }
                    break;
            }
        }
        return unfinished && !settled;
    }

    /// <summary>Names the owned draft an order rides on, or null for an action that is not a drafted order.</summary>
    internal static ActionId? WorkerOrderDraft(DomainAction action)
    {
        if (action.Movement is { } m)
        {
            return m.DraftAction;
        }
        if (action.Subdue is { } s)
        {
            return s.DraftAction;
        }
        return null;
    }
}
