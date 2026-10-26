using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Telemetry;
using Pol = RimGovernor.Brain.Policy;
using Decision = RimGovernor.Brain.Telemetry.Decision;

namespace RimGovernor.Brain.Executor;

/// <summary>Advances admitted actions, including holds, fair selection, retries and reconciliation.</summary>
public sealed partial class ActionAdvancement(ActionAdvancementConfig config)
{
    private static readonly TimeSpan FlushTimeout = TimeSpan.FromSeconds(5);
    private const int CancelledSettle = 3;
    private readonly ActionAdvancementConfig _config = config;
    private readonly Dictionary<ActionId, WorkerWait> _waits = [];
    private PlanId _cursor;
    private bool _cursorKnown;
    private readonly HashSet<ActionId> _focus = [];
    private bool _advanced;
    public bool NeedsAdvance => _advanced || _focus.Count > 0;
    private bool FocusNamed(ActionId id) => _focus.Contains(id);
    private int DispatchBudget() => _config.MaxDispatches > 0 ? _config.MaxDispatches : int.MaxValue;
    private static World PlayerWorld(GenerationSnapshot snapshot) => new(snapshot.Colony, snapshot.Load, snapshot.Map);
    /// <summary>
    /// Lists every action kind the worker (and, for a non-current plan, AuthorizeReviewPlan) is allowed to
    /// dispatch or observe. Growing this list is how a new action family joins live automatic execution; each
    /// kind here already carries its own CAS-admission-guarded executor/boundary pair, so this is an allowlist
    /// of what has that machinery, not a bypass of it.
    /// </summary>
    internal static bool ExecutableKind(ActionKind kind) => ExecutableKinds.Contains(kind);

    private static readonly HashSet<ActionKind> ExecutableKinds =
    [
        ActionKind.Building, ActionKind.OwnedDraft, ActionKind.Undraft, ActionKind.Subdue,
        ActionKind.SupplyAllow, ActionKind.SupplyForbid, ActionKind.WorkAssignment, ActionKind.PawnActivities, ActionKind.Acquisition, ActionKind.AcquisitionWithdraw, ActionKind.ZoneCreate,
        ActionKind.ProductionBill, ActionKind.Tend, ActionKind.Rescue, ActionKind.Capture, ActionKind.UseItem, ActionKind.Haul, ActionKind.Equip, ActionKind.DropEquipment,
        ActionKind.GearReplace, ActionKind.ApparelPolicy, ActionKind.RecoveryService, ActionKind.Movement, ActionKind.Husbandry,
        ActionKind.PrisonerInteraction, ActionKind.Repair, ActionKind.MineAcquisition,
        ActionKind.BuildingTemperature, ActionKind.BedUse, ActionKind.GrowerCrop, ActionKind.ClaimBuilding, ActionKind.AutoRefuel, ActionKind.Surgery, ActionKind.AutoHomeArea, ActionKind.PawnSettings, ActionKind.Area,
        ActionKind.PolicyPrune, ActionKind.ReadingPolicy, ActionKind.DrugPolicy, ActionKind.FoodPolicy, ActionKind.ZoneDelete, ActionKind.ZoneCellEdit, ActionKind.StockpilePatch, ActionKind.Assign,
        ActionKind.Conversion, ActionKind.Excavation, ActionKind.DialogAnswer, ActionKind.ResearchSelect, ActionKind.Trade, ActionKind.CommsTradeRequest, ActionKind.Deconstruction,
        ActionKind.RemoveRoof, ActionKind.BuildRoof, ActionKind.AreaPlantCut, ActionKind.CutPlant, ActionKind.Strip, ActionKind.MoveBuilding, ActionKind.UninstallBuilding, ActionKind.CoverClearance,
        ActionKind.WastepackHaul, ActionKind.FoundationRemoval, ActionKind.FloorRemoval, ActionKind.QuestAccept, ActionKind.QuestShuttle, ActionKind.HackDesignation, ActionKind.GiveItem, ActionKind.Ritual,
        ActionKind.IdeoligionReform, ActionKind.Ability, ActionKind.Gathering, ActionKind.RemoveProductionBill, ActionKind.DoorControl, ActionKind.WallRemoval, ActionKind.CaravanDeparture,
    ];

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
        var receipt = after.Receipt is ReceiptState.Received r ? Wire.Name(r.Kind) : "-";
        var effect = after.Effect is EffectState.Observed e ? Wire.Name(e.Kind) : "-";
        return string.Create(
            CultureInfo.InvariantCulture,
            $"stage={Wire.Name(after.Stage)} attempt={after.Attempt.Value} receipt={receipt} effect={effect} refused=[{string.Join(",", reasons)}] err={(err is null ? "<nil>" : err.Message)}"
        );
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

    public async Task AdvanceAsync(DateTimeOffset now, ControlState scope, World world, DateTimeOffset deadline,
        long cycleBegan, StepStages stages, Action current, CancellationToken call)
    {
        _advanced = false;
        var plans = await _config.Journal.LoadPlansAsync(call).ConfigureAwait(false);
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
        // One author: the root plan carries authority, and every other plan -- a routine method --
        // is dispatched under it once authorized. There is no priority arbitration between them.
        foreach (var plan in plans)
        {
            var planSnapshot = scope.Snapshot;
            if (scope.Enabled && scope.ObservationKnown && plan.Spec.Id != scope.Snapshot.Plan)
            {
                var target = scope.Snapshot with { Plan = plan.Spec.Id, Revision = plan.Spec.Revision };
                try
                {
                    await _config.ReviewScope.AuthorizeReviewPlanAsync(scope.Snapshot, target, call).ConfigureAwait(false);
                    planSnapshot = target;
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    // Not authorized: the plan stays under the root's snapshot and is judged ineligible below.
                    _config.Telemetry().Decide(new Decision { Kind = TelemetryKinds.DispatchKind, Component = "hands",
                        Target = plan.Spec.Id.Value, Verdict = "held", Reason = "plan_not_authorized",
                        Attrs = new Dictionary<string, object?> { ["error"] = error.Message } });
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
                var reviewObservation = _config.ReviewMethods && view.Unresolved && ExecutableKind(progress.Action.Kind) && PlayerWorld(view.Snapshot) == world;
                if (reviewObservation || WorkerEligible(plan, view, planScope, world))
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
        foreach (var id in _focus.Where(id => !live.Contains(id)).ToList())
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
        stages.Lap("read");
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
                current();
                Exception? err = null;
                try
                {
                    if (!scope.Enabled)
                    {
                        await _config.Session.ObserveTargetAsync(lead.View.Snapshot, call).ConfigureAwait(false);
                    }
                    current();
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
                var running = _config.ClockRunning is { } wr && wr();
                using var dispatch = Spans.StartSpan("buildingruntime", "hands.dispatch");
                dispatch?.SetTag("rimgovernor.action_kind", Wire.Name(lead.Kind));
                dispatch?.SetTag("rimgovernor.actions", group.Count);
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
                EndDispatchSpan(dispatch, results, resultErrs);
                for (var i = 0; i < group.Count; i++)
                {
                    _config.RecordOutcome(group[i].Kind, results[i], resultErrs[i]);
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
                        _config.Written?.Invoke(lead.Kind);
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
                    // every routine kind dispatches under the running clock. One retry per hold: a hold that
                    // survives it backs off as usual.
                    if (stale && !wait.Stale)
                    {
                        _focus.Add(v.Action);
                    }
                    // One line per change of outcome, in either log: a refusal that repeats verbatim on every
                    // retry (a CAS token that never matches, a native read refused for the same reason) would
                    // otherwise dominate the run's log without adding anything a reader can act on.
                    var outcome = WorkerOutcome(after, result, rerr);
                    var repeats = wait.Repeats;
                    var changed = outcome != wait.Outcome;
                    if (changed || result.NativeCalled)
                    {
                        WorkerDispatchRow(tally, v, after, result, running, stale, rerr, changed, repeats, dispatchStarted.Elapsed);
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
                            await _config.Journal.CancelAsync(v.Plan, v.Action, call).ConfigureAwait(false);
                            _config.Telemetry().Decide(new Decision
                            {
                                Kind = TelemetryKinds.DispatchKind,
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
                    _waits[v.Action] = new WorkerWait { View = after, Scope = WorkerScope(_config.Session.State()), Delay = delay, Until = now + delay, Outcome = outcome, Repeats = repeats, Stale = stale, Cancelled = cancelled };
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
        }
        finally
        {
            stages.Lap("dispatch");
            if (sent)
            {
                await Flush().ConfigureAwait(false);
                stages.Lap("flush");
            }
            if (dispatched > 0)
            {
                _config.Telemetry().Decide(WorkerStepDecision(Stopwatch.GetElapsedTime(cycleBegan), stages, candidates.Count, dispatched));
            }
        }
        var all = new List<Exception?>();
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
        catch (Exception error)
        {
            _config.Telemetry().Decide(new Decision { Kind = TelemetryKinds.DispatchKind, Component = "hands", Verdict = "failed", Reason = "snapshot_flush", Attrs = new Dictionary<string, object?> { ["error"] = error.Message } });
        }
    }

    /// <summary>
    /// The worker_step planner_step row of one Worker cycle that dispatched: dur the cycle's wall from its start to
    /// its flush, attrs the stages (gate, read, dispatch, flush), the candidates it saw and the dispatches it made.
    /// Filed only for a cycle that dispatched, so an idle Worker writes nothing.
    /// </summary>
    internal static Decision WorkerStepDecision(TimeSpan took, StepStages stages, int candidates, int dispatched)
    {
        var attrs = new Dictionary<string, object?> { ["candidates"] = (long)candidates, ["dispatched"] = (long)dispatched };
        stages.Publish(attrs);
        return new Decision
        {
            Kind = Decision.PlannerStepKind,
            Component = "worker",
            Target = "worker_step",
            Verdict = "ok",
            Reason = "dispatched",
            Dur = took,
            Attrs = attrs,
        };
    }

    /// <summary>Whether the candidate joins its plan's other plain intents in one batched Apply; other kinds each dispatch alone.</summary>
    private static bool WorkerBatched(WorkerCandidate candidate) => Executor.PlainIntent(candidate.Kind);

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
            IReadOnlyList<BatchItem> items = [];
            Exception? err = null;
            try
            {
                items = await _config.Session.RunBatchAsync(plan, ids, call).ConfigureAwait(false);
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
    /// dispatch), whether the scheduler's run was in progress when the run began and the read tally
    /// (`rimgovernor phases` sums those rows, Bridge.DispatchSample); an outcome change carries `changed` and
    /// how many unrecorded runs restated the previous outcome, so a refusal that repeats verbatim on every
    /// retry writes once. An action waiting on a prerequisite that has not completed is the plan sequencing
    /// itself, not a failure, so only another error is WARN. Stale marks a run held on stale facts so a run can
    /// count the holds the read bounds refused.
    /// </summary>
    internal void WorkerDispatchRow(ReadTally tally, ProgressView before, ProgressView after, ExecutorResult result, bool running, bool stale, Exception? err, bool changed, int repeats, TimeSpan took = default)
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
            Kind = TelemetryKinds.DispatchKind,
            Component = "worker",
            Target = before.Action.Value,
            Verdict = "waiting",
            Reason = Wire.Name(after.Stage),
            Dur = took,
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
            _config.Telemetry().Decide(d);
            return;
        }
        var receipt = "-";
        if (
            after.Receipt is ReceiptState.Received rv
            && (after.Attempt != before.Attempt || !WorkerSameReceipt(before, after))
        )
        {
            receipt = Wire.Name(rv.Kind);
        }
        attrs["receipt"] = receipt;
        attrs["running"] = running;
        if (result.SkippedWork.Count > 0)
        {
            attrs["skipped_work"] = string.Join(",", result.SkippedWork);
        }
        if (after.Refusal is { } refusal && receipt == Wire.Name(Domain.Receipt.Refused))
        {
            attrs["refusal_class"] = Wire.Name(refusal.Class);
            attrs["refusal_reason"] = refusal.Reason;
        }
        if (before.Stage is Stage.Pending or Stage.Prepared && after.Attempt.Value != 0 && after.Stage is not (Stage.Pending or Stage.Prepared))
        {
            attrs["dispatch_tick"] = after.Tick.Value;
        }
        tally.Publish(_config.Telemetry(), d);
    }

    private static bool WorkerSameReceipt(ProgressView a, ProgressView b)
    {
        return a.Receipt == b.Receipt;
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
    internal static TimeSpan WorkerBackoffCap(ActionAdvancementConfig config, ProgressView v)
    {
        if (v.Observes(Effect.Inconclusive) && v.Stage == Stage.AwaitingObservation)
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
        if (err is not HeldException || after.Unresolved || after.Stage is not (Stage.Pending or Stage.Prepared))
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
        if (after.HeldReason is not { } held)
        {
            return false;
        }
        return held.Reasons().Contains(HeldReason.StaleFacts);
    }

    internal static bool WorkerEligible(Store.PlanState plan, ProgressView v, ControlState scope, World world)
    {
        if (plan.Spec.Action(v.Action) is not { } action || !ExecutableKind(action.Kind))
        {
            return false;
        }
        if (scope.Enabled)
        {
            if (!scope.ObservationKnown || PlayerWorld(scope.Snapshot) != world || scope.Snapshot.Plan != v.Plan || scope.Snapshot.Revision != v.Revision)
            {
                return false;
            }
        }
        else
        {
            return v.Unresolved && PlayerWorld(v.Snapshot) == world;
        }
        if (v.Unresolved)
        {
            return PlayerWorld(v.Snapshot) == world;
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
        // Preparation records no native write. A cancelled reassertion is still ready even when its
        // previous movement attempt has an accepted receipt in the journal history.
        if (v.Stage == Stage.Prepared) return true;
        // A trusted refusal is a no-effect proof. Only a later resume (a newer native generation) authorizes
        // another attempt; an unknown receipt always stays reconciliation. A write the transport never issued
        // refused nothing, so the same generation may retry it.
        // An intent whose Apply receipt came back unknown is back at Pending for a resend. Like a refusal, only
        // a later resume (a newer native generation) resends it; native answers applied if the first write
        // landed, so the resend cannot duplicate the building.
        if (v.Received(Domain.Receipt.Unconfirmed) && v.Stage == Stage.Pending)
        {
            return PlayerWorld(v.Snapshot) == world && scope.Snapshot.Native > v.Snapshot.Native;
        }
        if (
            v.Receipt is not ReceiptState.Received
            || !v.Observes(Effect.Absent)
            || PlayerWorld(v.Snapshot) != world
        )
        {
            return false;
        }
        // A refused attempt already re-prepared carries the newer generation that authorized it, so it stays
        // eligible until it dispatches.
        return v.Received(Domain.Receipt.Unsent)
            || (
                v.Received(Domain.Receipt.Refused)
                && (v.Stage == Stage.Prepared || scope.Snapshot.Native > v.Snapshot.Native)
            );
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

}
