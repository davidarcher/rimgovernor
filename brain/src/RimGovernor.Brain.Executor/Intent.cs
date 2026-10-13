using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using System.Collections.Frozen;
using RimGovernor.Brain.Store;

namespace RimGovernor.Brain.Executor;

// Port of internal/executor/intent.go.

/// <summary>
/// A batch-level failure of <see cref="Executor.RunBatchAsync"/> (stopped, cancelled, plan load): <see cref="Cause"/>
/// is the error and <see cref="Items"/> what the batch had already settled, in input order (Go returned both).
/// </summary>
public sealed class BatchFailureException(IReadOnlyList<BatchItem> items, Exception cause)
    : DomainException("building batch failed: " + cause.Message)
{
    public IReadOnlyList<BatchItem> Items { get; } = items;

    public Exception Cause { get; } = cause;
}

public sealed partial class Executor
{
    // The intent-mode kinds the plain-intent flow dispatches: no prerequisite action and no journal admission.
    // Native validates each against live state and treats a setting that already holds as applied.
    private static readonly FrozenSet<ActionKind> s_plainIntents = new[]
    {
        ActionKind.Building,
        ActionKind.ApparelPolicy,
        ActionKind.ResearchSelect,
        ActionKind.DialogAnswer,
        ActionKind.PrisonerInteraction,
        ActionKind.QuestAccept,
        ActionKind.QuestShuttle,
        ActionKind.Ritual,
        ActionKind.IdeoligionReform,
        ActionKind.Ability,
        ActionKind.Ignite,
        ActionKind.Gathering,
        ActionKind.RemoveProductionBill,
        ActionKind.DoorControl,
        ActionKind.CaravanDeparture,
        ActionKind.Assign,
        ActionKind.WorkAssignment,
        ActionKind.Husbandry,
        ActionKind.ProductionBill,
        ActionKind.ZoneCreate,
        ActionKind.ZoneDelete,
        ActionKind.ZoneCellEdit,
        ActionKind.StockpilePatch,
        ActionKind.FoundationRemoval,
        ActionKind.FloorRemoval,
        ActionKind.CoverClearance,
        ActionKind.WastepackHaul,
        ActionKind.CutPlant,
        ActionKind.SupplyAllow,
        ActionKind.SupplyForbid,
        ActionKind.Deconstruction,
        ActionKind.Excavation,
        ActionKind.WallRemoval,
        ActionKind.RecoveryService,
        ActionKind.MoveBuilding,
        ActionKind.UninstallBuilding,
        ActionKind.Acquisition,
        ActionKind.MineAcquisition,
        ActionKind.AcquisitionWithdraw,
        ActionKind.BuildingTemperature,
        ActionKind.BedUse,
        ActionKind.GrowerCrop,
        ActionKind.ClaimBuilding,
        ActionKind.AutoRefuel,
        ActionKind.Surgery,
        ActionKind.Area,
        ActionKind.PolicyPrune,
        ActionKind.RemoveRoof,
        ActionKind.BuildRoof,
        ActionKind.AreaPlantCut,
        ActionKind.ReadingPolicy,
        ActionKind.DrugPolicy,
        ActionKind.FoodPolicy,
        ActionKind.PawnSettings,
        ActionKind.AutoHomeArea,
        ActionKind.Repair,
        ActionKind.Clean,
        ActionKind.OpenCasket,
        ActionKind.Tend,
        ActionKind.OwnedDraft,
        ActionKind.Subdue,
        ActionKind.Equip,
        ActionKind.DropEquipment,
        ActionKind.Rescue,
        ActionKind.Capture,
        ActionKind.MoodRelief,
        ActionKind.GearReplace,
        ActionKind.UseItem,
        ActionKind.Strip,
        ActionKind.RulesAttach,
    }.ToFrozenSet();

    /// <summary>Whether <paramref name="kind"/> dispatches with its plan's other plain intents in one native Apply; other kinds run their own flow per action.</summary>
    public static bool PlainIntent(ActionKind kind) => s_plainIntents.Contains(kind);

    private readonly record struct IntentItem(DomainAction Action, Progress Progress);

    /// <summary>
    /// The executor's one entry point. It dispatches a plan's plain intents in one native Actions/Apply call: one
    /// writer slot, one plan load, one inspection whose snapshot and tick anchor every action, one journal
    /// transaction per stage. An unresolved attempt is settled from the journal. Other kinds run their own flow
    /// per action, in input order, before the plain intents dispatch. A thrown <see cref="BatchFailureException"/>
    /// is the batch's own failure (stopped, cancelled, plan load); per-action errors are in the items, in input
    /// order.
    /// </summary>
    public async Task<IReadOnlyList<BatchItem>> RunBatchAsync(PlanId plan, IReadOnlyList<ActionId> actions, CancellationToken ct)
    {
        var results = new ExecutorResult[actions.Count];
        var errors = new Exception?[actions.Count];
        for (var i = 0; i < results.Length; i++)
        {
            results[i] = new ExecutorResult();
        }
        BatchItem[] Items()
        {
            var output = new BatchItem[actions.Count];
            for (var i = 0; i < output.Length; i++)
            {
                output[i] = new BatchItem(actions[i], results[i], errors[i]);
            }
            return output;
        }
        try
        {
            await WithPlanAsync(
                plan,
                actions,
                async (loaded, authority, generation, run) =>
                {
                    var state = loaded;
                    var items = new List<IntentItem>();
                    var at = new List<int>();
                    var flowed = false;
                    for (var i = 0; i < actions.Count; i++)
                    {
                        var id = actions[i];
                        var (action, progress) = Find(state, id);
                        if (action is not null && progress is not null)
                        {
                            if (action.Kind.IntentMode() && progress.View.Unresolved)
                            {
                                (results[i], errors[i]) = await SettleIntentAsync(progress).ConfigureAwait(false);
                                continue;
                            }
                            if (action.Kind == ActionKind.CombatBatch && progress.View.Unresolved)
                            {
                                results[i] = new ExecutorResult { Progress = progress };
                                continue;
                            }
                            if (s_plainIntents.Contains(action.Kind) || action.Kind == ActionKind.CombatBatch)
                            {
                                items.Add(new IntentItem(action, progress));
                                at.Add(i);
                                continue;
                            }
                        }
                        // A kind with its own flow (admission, prerequisites) runs alone, in input order, on durable
                        // state reloaded after any earlier flow wrote to it.
                        if (flowed)
                        {
                            state = await _journal.LoadPlanAsync(plan, run).ConfigureAwait(false);
                        }
                        (results[i], errors[i]) = await RunLoadedAsync(state, id, authority, generation, run).ConfigureAwait(false);
                        flowed = true;
                    }
                    var dispatched = await RunIntentsAsync(items, authority, generation, run).ConfigureAwait(false);
                    for (var j = 0; j < dispatched.Length; j++)
                    {
                        (results[at[j]], errors[at[j]]) = dispatched[j];
                    }
                },
                ct
            ).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            throw new BatchFailureException(Items(), e);
        }
        return Items();
    }

    // Dispatches plain intents of one plan. Each receipt is terminal: applied (the blueprint is placed, or the
    // setting holds) is done, refused is over and the owning routine replans, and a lost reply is sent again under
    // a new attempt.
    private async Task<(ExecutorResult Result, Exception? Error)[]> RunIntentsAsync(List<IntentItem> items, Authority authority, CancellationToken generation, CancellationToken ct)
    {
        var results = new ExecutorResult[items.Count];
        var errors = new Exception?[items.Count];
        (ExecutorResult, Exception?)[] Output()
        {
            var output = new (ExecutorResult, Exception?)[items.Count];
            for (var i = 0; i < output.Length; i++)
            {
                output[i] = (results[i], errors[i]);
            }
            return output;
        }
        void Fail(IEnumerable<int> at, Exception error)
        {
            foreach (var i in at)
            {
                errors[i] = error;
            }
        }
        var live = new List<int>();
        var expected = default(GenerationSnapshot);
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            results[i] = new ExecutorResult { Progress = item.Progress };
            if (((!s_plainIntents.Contains(item.Action.Kind) || !item.Action.Kind.IntentMode()) && item.Action.Kind != ActionKind.CombatBatch) || item.Progress.Action != item.Action)
            {
                errors[i] = new EvidenceException();
                continue;
            }
            var v = item.Progress.View;
            switch (v.Stage)
            {
                case Stage.Completed or Stage.Cancelled or Stage.Unsuccessful:
                    continue;
                case Stage.Pending or Stage.Prepared:
                    break;
                default:
                    errors[i] = new EvidenceException();
                    continue;
            }
            var want = authority.Snapshot;
            if (want.Plan != v.Plan || want.Revision != v.Revision)
            {
                if (_roundsScope is null)
                {
                    errors[i] = new AuthorityException();
                    continue;
                }
                want = want with { Plan = v.Plan, Revision = v.Revision };
            }
            if (live.Count > 0 && want != expected)
            {
                errors[i] = new AuthorityException();
                continue;
            }
            expected = want;
            live.Add(i);
        }
        if (live.Count == 0)
        {
            return Output();
        }
        IntentInspection inspection;
        try
        {
            await GuardAsync(expected, generation, ct).ConfigureAwait(false);
            // One bounds read anchors the whole batch.
            inspection = await _boundary.InspectIntentAsync(new Target(items[live[0]].Action, expected), ct).ConfigureAwait(false);
            await GuardAsync(expected, generation, ct).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Fail(live, e);
            return Output();
        }
        if (!inspection.Current.Matches(expected) || !Fresh(inspection.StartedAt, inspection.ObservedAt))
        {
            Fail(live, new HeldException());
            return Output();
        }
        var attempts = new BatchAttempt[live.Count];
        for (var j = 0; j < live.Count; j++)
        {
            var v = items[live[j]].Progress.View;
            attempts[j] = new BatchAttempt { Plan = v.Plan, Action = v.Action, Snapshot = expected, Tick = v.Tick > inspection.Tick ? v.Tick : inspection.Tick };
        }
        IReadOnlyList<BatchResult> prepared;
        try
        {
            prepared = await _journal.PrepareBatchAsync(attempts, ct).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Fail(live, e);
            return Output();
        }
        var ready = new List<int>();
        var readyAttempts = new List<BatchAttempt>();
        for (var j = 0; j < prepared.Count; j++)
        {
            var i = live[j];
            if (prepared[j].Error is { } preparedError)
            {
                errors[i] = preparedError;
                continue;
            }
            results[i] = results[i] with { Progress = prepared[j].Progress };
            ready.Add(i);
            readyAttempts.Add(attempts[j]);
        }
        IReadOnlyList<BatchResult> dispatched;
        try
        {
            dispatched = await _journal.DispatchBatchAsync(readyAttempts, ct).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Fail(ready, e);
            return Output();
        }
        var sent = new List<int>();
        var placements = new List<Placement>();
        var nexts = new List<Progress>();
        for (var j = 0; j < dispatched.Count; j++)
        {
            var (i, a, r) = (ready[j], readyAttempts[j], dispatched[j]);
            if (r.Error is ActionVetoedException vetoed)
            {
                // An action Safeguard refused only this action; record why.
                try
                {
                    var held = await _journal.HoldAsync(a.Plan, a.Action, [HeldReason.UnsafeItem], a.Tick, ct).ConfigureAwait(false);
                    results[i] = results[i] with { Progress = held };
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // Recording the reason is best effort: the veto is reported either way.
                }
                errors[i] = Errors.Join(new HeldException(), vetoed);
                continue;
            }
            if (r.Error is { } dispatchError)
            {
                errors[i] = dispatchError;
                continue;
            }
            results[i] = results[i] with { Progress = r.Progress };
            sent.Add(i);
            nexts.Add(r.Progress);
            placements.Add(new Placement(items[i].Action, r.Progress.View.Attempt, expected, a.Tick));
        }
        if (sent.Count == 0)
        {
            return Output();
        }
        var kinds = new Domain.Receipt[sent.Count];
        var zones = new string[sent.Count];
        var stockpiles = new IReadOnlyList<CreatedZone>[sent.Count];
        var bills = new string[sent.Count];
        var combat = new IReadOnlyList<CombatResult>[sent.Count];
        var refusals = new NativeRefusal?[sent.Count];
        var causes = new Exception?[sent.Count];
        for (var j = 0; j < sent.Count; j++)
        {
            (zones[j], bills[j], stockpiles[j], combat[j]) = ("", "", [], []);
        }
        Exception? guardError = null;
        try
        {
            await GuardAsync(expected, generation, ct).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            guardError = e;
        }
        if (guardError is not null)
        {
            for (var j = 0; j < sent.Count; j++)
            {
                (kinds[j], causes[j]) = (Domain.Receipt.Unknown, guardError);
            }
        }
        else
        {
            foreach (var i in sent)
            {
                results[i] = results[i] with { NativeCalled = true };
            }
            IReadOnlyList<Receipt>? receipts = null;
            Exception? writeError = null;
            try
            {
                receipts = await _boundary.WriteIntentsAsync(placements, ct).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                writeError = e;
            }
            for (var j = 0; j < placements.Count; j++)
            {
                var p = placements[j];
                if (writeError is not null)
                {
                    (kinds[j], causes[j]) = (ReceiptAfterCallError(writeError), writeError);
                }
                else if (receipts is null || receipts.Count != placements.Count)
                {
                    (kinds[j], causes[j]) = (Domain.Receipt.Unknown, new EvidenceException());
                }
                else if (receipts[j].Action != p.Action.Id || receipts[j].Attempt != p.Attempt || receipts[j].Snapshot != expected)
                {
                    (kinds[j], causes[j]) = (Domain.Receipt.Unknown, new EvidenceException());
                }
                else
                {
                    kinds[j] = receipts[j].Kind;
                    zones[j] = receipts[j].Zone;
                    bills[j] = receipts[j].Bill;
                    stockpiles[j] = receipts[j].Stockpiles;
                    combat[j] = receipts[j].Combat;
                    refusals[j] = receipts[j].Refusal;
                }
                Exception? check = null;
                try
                {
                    if (p.Action.Kind == ActionKind.CombatBatch)
                    {
                        _ = nexts[j].RecordCombatReceipt(p.Attempt, kinds[j], combat[j]);
                    }
                    else if (refusals[j] is { } refusal && kinds[j] == Domain.Receipt.Refused)
                    {
                        _ = nexts[j].RecordRefusal(p.Attempt, refusal);
                    }
                    else
                    {
                        _ = nexts[j].RecordReceipt(p.Attempt, kinds[j]);
                    }
                }
                catch (DomainException e)
                {
                    check = e;
                }
                if (check is not null)
                {
                    kinds[j] = Domain.Receipt.Unknown;
                    causes[j] = Errors.Join(causes[j], new EvidenceException(check));
                    combat[j] = [];
                }
                causes[j] = Errors.Join(causes[j], CancelledBy(ct));
            }
        }
        await RecordReceiptsAsync(results, errors, sent, placements, kinds, zones, stockpiles, bills, combat, refusals, causes).ConfigureAwait(false);
        return Output();
    }

    // Writes the receipts of the sent items in one journal transaction; an applied zone_create or bill placement
    // records its zone or bill id on its own. Like RecordZoneAsync, it uses a fresh bounded token so cancellation
    // never erases an attempt.
    private async Task RecordReceiptsAsync(
        ExecutorResult[] results,
        Exception?[] errors,
        List<int> sent,
        List<Placement> placements,
        Domain.Receipt[] kinds,
        string[] zones,
        IReadOnlyList<CreatedZone>[] stockpiles,
        string[] bills,
        IReadOnlyList<CombatResult>[] combat,
        NativeRefusal?[] refusals,
        Exception?[] causes
    )
    {
        using var bound = new CancellationTokenSource(_limits.JournalTimeout);
        var ct = bound.Token;
        var receipts = new List<BatchReceipt>();
        var at = new List<int>();
        for (var j = 0; j < sent.Count; j++)
        {
            var i = sent[j];
            var p = placements[j];
            var plan = results[i].Progress!.View.Plan;
            errors[i] = causes[j];
            if (stockpiles[j].Count > 0 && kinds[j] == Domain.Receipt.Accepted)
            {
                await RecordOwnAsync(i, () => _journal.RecordStockpileReceiptAsync(plan, p.Action.Id, p.Attempt, stockpiles[j], ct)).ConfigureAwait(false);
                continue;
            }
            if (zones[j].Length != 0 && kinds[j] == Domain.Receipt.Accepted)
            {
                await RecordOwnAsync(i, () => _journal.RecordZoneReceiptAsync(plan, p.Action.Id, p.Attempt, zones[j], ct)).ConfigureAwait(false);
                continue;
            }
            if (bills[j].Length != 0 && kinds[j] == Domain.Receipt.Accepted)
            {
                await RecordOwnAsync(i, () => _journal.RecordBillReceiptAsync(plan, p.Action.Id, p.Attempt, bills[j], ct)).ConfigureAwait(false);
                continue;
            }
            receipts.Add(new BatchReceipt { Plan = plan, Action = p.Action.Id, Attempt = p.Attempt, Receipt = kinds[j], Combat = combat[j], Refusal = refusals[j] });
            at.Add(i);
        }
        IReadOnlyList<BatchResult>? recorded = null;
        Exception? batchError = null;
        try
        {
            recorded = await _journal.RecordReceiptsAsync(receipts, ct).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            batchError = e;
        }
        for (var k = 0; k < at.Count; k++)
        {
            var i = at[k];
            if (batchError is not null)
            {
                errors[i] = Errors.Join(errors[i], batchError);
            }
            else if (recorded![k].Error is { } itemError)
            {
                errors[i] = Errors.Join(errors[i], itemError);
            }
            else
            {
                results[i] = results[i] with { Progress = recorded[k].Progress };
            }
        }

        async Task RecordOwnAsync(int i, Func<Task<Progress>> write)
        {
            try
            {
                results[i] = results[i] with { Progress = await write().ConfigureAwait(false) };
            }
            catch (Exception e)
            {
                errors[i] = Errors.Join(errors[i], e);
            }
        }
    }
}
