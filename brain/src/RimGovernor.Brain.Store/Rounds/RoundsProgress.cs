using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Store;

public sealed partial class Store
{
    private static readonly WorkType[] ConstructionLabor = [WorkType.Construction];

    private static readonly WorkType[] HaulingLabor = [WorkType.Hauling];

    private static readonly WorkType[] PlantCuttingLabor = [WorkType.PlantCutting];

    private static readonly WorkType[] MiningLabor = [WorkType.Mining];

    private static readonly WorkType[] CleaningLabor = [WorkType.Cleaning];

    private static readonly WorkType[] HuntingLabor = [WorkType.Hunting];

    /// <summary>
    /// Records every active goal's progress from the evidence this review holds: the goal's open plans (a settled
    /// effect or observed construction since the last record is native progress; a dispatched order with no
    /// capable available pawn is blocked), the labor census, the foothold gates (the food ladder's prerequisite)
    /// and the deficit the review measured. Records are keyed by need, the id the launcher names concerns by; a
    /// concern that is recovered, cancelled or invalidated drops its record. <paramref name="states"/> holds each
    /// assessment's row in assessment order (Go <c>roundsProgress</c>).
    /// </summary>
    internal static EquatableArray<ConcernProgress> RoundsProgress(
        StoreTransaction tx,
        RoundsRequest request,
        Rounds previous,
        bool reset,
        RoundsFindings needs,
        IReadOnlyList<StandardState> states
    )
    {
        var old = new Dictionary<ConcernId, ConcernProgress>();
        if (!reset)
        {
            foreach (var p in previous.Progress)
            {
                old[p.Concern] = p;
            }
        }
        var deficits = new Dictionary<ConcernId, Fact<double>>();
        foreach (var g in needs.Concerns)
        {
            deficits[g.Id] = g.Deficit;
        }
        var storageOpen = false;
        for (var i = 0; i < needs.Assessments.Count; i++)
        {
            if (needs.Assessments[i].Id != PolicyFunctions.MaintainFoodStorage || !states[i].OwnerActive)
            {
                continue;
            }
            var open = StandardOpenWork(tx, states[i]);
            storageOpen = storageOpen || open;
        }
        var output = new List<ConcernProgress>();
        for (var i = 0; i < needs.Assessments.Count; i++)
        {
            var n = needs.Assessments[i];
            var g = states[i];
            if (!g.OwnerActive)
            {
                continue;
            }
            var last = old.GetValueOrDefault(n.Id) ?? new ConcernProgress();
            _ = deficits.TryGetValue(n.Id, out var observed);
            bool advanced = false,
                open = false,
                dispatched = false,
                unresolved = false,
                nativeIneligible = false;
            var method = default(MethodId);
            var labor = PolicyFunctions.ConcernLabor(n.Id);
            foreach (var m in g.Methods)
            {
                if (m.Episode != g.Standard.Episode)
                {
                    continue;
                }
                var plan = LoadIn(tx, m.Plan);
                foreach (var p in plan.Progress)
                {
                    var v = p.View;
                    if (v.Effect.TryGetValue(out var effect) && (effect == Effect.Completed || effect == Effect.Absent) && v.Tick > last.LastProgress && last.Concern == n.Id)
                    {
                        advanced = true;
                    }
                    var building = !plan.Retired && PolicyFunctions.AppliedBuildingOpen(p, request.Facts.CurrentConstruction);
                    if (!building && !Standard.WorkOpen([p]))
                    {
                        continue;
                    }
                    method = m.MethodId;
                    open = true;
                    if (building || v.Stage is Stage.Dispatched or Stage.AwaitingObservation || v.Unresolved)
                    {
                        dispatched = true;
                    }
                    if (v.Stage == Stage.Dispatched && v.Unresolved && (!v.Receipt.TryGetValue(out var receipt) || receipt == Receipt.Unknown))
                    {
                        unresolved = true;
                    }
                    if (v.TryGetFreshHold(out var hold))
                    {
                        foreach (var reason in hold.Reasons())
                        {
                            nativeIneligible = nativeIneligible || reason == HeldReason.NativeIneligible;
                        }
                    }
                    if (labor.Count == 0)
                    {
                        labor = ActionLabor(p.Action);
                    }
                }
            }
            var workerAvailable = default(Fact<bool>);
            if (request.Facts.Labor.TryGetValue(out var census) && labor.Count > 0)
            {
                var available = false;
                foreach (var w in labor)
                {
                    available = available || (census.TryGetValue(w, out var count) && count > 0);
                }
                workerAvailable = Known(available);
            }
            var contract = PolicyFunctions.ConcernProgressContract(MethodLabel(method), request.Policy);
            var prerequisite = default(ConcernId);
            if (n.Id == PolicyFunctions.EnsureFoodSupply)
            {
                (contract, prerequisite, observed) = PolicyFunctions.FoodProgress(request.Facts, request.Policy, storageOpen);
            }
            var evidence = new ProgressEvidence
            {
                Advanced = advanced,
                Open = open,
                Dispatched = dispatched,
                Unresolved = unresolved,
                WorkerAvailable = workerAvailable,
                NativeIneligible = nativeIneligible,
                Prerequisite = prerequisite,
                Observed = observed,
            };
            var record = PolicyFunctions.ReviewConcernProgress(last, n.Id, contract, evidence, request.Tick);
            if (!advanced)
            {
                // A deadline that passed without native progress keys the failed situation out for a bounded
                // cooldown; the planners rotate the method or target (stall cancels, attempt counts).
                record = PolicyFunctions.ExpireConcernProgress(record, contract, request.Tick, PolicyFunctions.CooldownKey(record.Method, record.Blocked.Text), []).Progress;
            }
            try
            {
                PolicyFunctions.ValidateConcernProgress(record, request.Tick);
            }
            catch (DomainException e)
            {
                throw new DomainException($"{n.Id.Value}: {e.Message}");
            }
            output.Add(record);
        }
        return EquatableArray.From(output);
    }

    /// <summary>
    /// The work type an open action's order waits on when its goal declares no profile (a startup goal): who could
    /// take it. Empty when the action kind has none (Go <c>actionLabor</c>).
    /// </summary>
    internal static IReadOnlyList<WorkType> ActionLabor(DomainAction action) =>
        action.Kind switch
        {
            ActionKind.Building
            or ActionKind.Deconstruction
            or ActionKind.RemoveRoof
            or ActionKind.BuildRoof
            or ActionKind.WallRemoval
            or ActionKind.Repair
            or ActionKind.MoveBuilding
            or ActionKind.UninstallBuilding => ConstructionLabor,
            ActionKind.Haul or ActionKind.Strip => HaulingLabor,
            ActionKind.CutPlant or ActionKind.AreaPlantCut => PlantCuttingLabor,
            ActionKind.MineAcquisition or ActionKind.Excavation => MiningLabor,
            ActionKind.Clean => CleaningLabor,
            ActionKind.Acquisition => action.Acquisition is { Hunt: true } ? HuntingLabor : PlantCuttingLabor,
            _ => [],
        };

    /// <summary>
    /// A method id without its content hash or admission salt ("acquire-3f9a..." reads "acquire"), so the record
    /// names the method family rather than one plan (Go <c>methodLabel</c>).
    /// </summary>
    internal static string MethodLabel(MethodId id)
    {
        var s = id.Value;
        var i = s.LastIndexOf('-');
        if (i > 0)
        {
            var suffix = s[(i + 1)..];
            if (suffix.Length >= 8 && suffix.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                s = s[..i];
            }
        }
        return s.Length > 64 ? s[..64] : s;
    }

    /// <summary>
    /// Keys a failed situation out of <paramref name="need"/>'s goal for a bounded cooldown
    /// (<c>ProgressCooldownMax</c>): a planner that cancelled a stalled order calls it with the target it gave up
    /// on so the next selection passes that target over until the cooldown lifts. A review that has moved past
    /// <paramref name="revision"/> is <see cref="ConflictException"/>; a need without a record is a no-op.
    /// </summary>
    public Rounds RecordProgressCooldown(ulong revision, ConcernId need, string key, Tick until, CancellationToken cancellationToken) =>
        Run(
            tx =>
            {
                var review = LoadRounds(tx);
                if (review.Revision != revision)
                {
                    throw new ConflictException($"rounds revision {review.Revision}, cooldown under {revision}");
                }
                var changed = false;
                var progress = new List<ConcernProgress>(review.Progress.Count);
                foreach (var p in review.Progress)
                {
                    if (p.Concern != need)
                    {
                        progress.Add(p);
                        continue;
                    }
                    var updated = PolicyFunctions.AddProgressCooldown(p, key, until, review.Tick);
                    PolicyFunctions.ValidateConcernProgress(updated, review.Tick);
                    progress.Add(updated);
                    changed = true;
                }
                if (!changed)
                {
                    return review;
                }
                review = review with { Progress = EquatableArray.From(progress) };
                PutSingleton(tx, "rounds", review, MaxRoundsPayload);
                return review;
            },
            cancellationToken
        );

    public Task<Rounds> RecordProgressCooldownAsync(ulong revision, ConcernId need, string key, Tick until, CancellationToken cancellationToken) =>
        Task.Run(() => RecordProgressCooldown(revision, need, key, until, cancellationToken), cancellationToken);

    /// <summary>
    /// Files each goal's latest planner refusal on its progress record (<c>ConcernProgress.Planner</c>); the zero
    /// note clears it (the planner admitted or found work). A record with no method, or one already naming a
    /// planner refusal or wait, is relabelled at once so the strip names the reason before the next review; held
    /// records keep their hold. Standards without a record are skipped. Reports whether anything changed and each
    /// filed goal's current method (its record's Method, empty when it has none).
    /// </summary>
    public (bool Changed, IReadOnlyDictionary<ConcernId, string> Methods) RecordPlannerReasons(IReadOnlyDictionary<ConcernId, PlannerNote> reasons, CancellationToken cancellationToken)
    {
        if (reasons.Count == 0)
        {
            return (false, new Dictionary<ConcernId, string>());
        }
        return Run<(bool Changed, IReadOnlyDictionary<ConcernId, string> Methods)>(
            tx =>
            {
                var review = LoadRounds(tx);
                var changed = false;
                var methods = new Dictionary<ConcernId, string>();
                var progress = new List<ConcernProgress>(review.Progress.Count);
                foreach (var record in review.Progress)
                {
                    var p = record;
                    if (!reasons.TryGetValue(p.Concern, out var note))
                    {
                        progress.Add(p);
                        continue;
                    }
                    methods[p.Concern] = p.Method;
                    if (p.PlannerNote() != note)
                    {
                        var unmethoded = p.Unmethoded();
                        p = p with { Planner = note.Cause, PlannerSubject = note.Subject };
                        if (unmethoded)
                        {
                            p = p with { Blocked = note.Blocked() };
                        }
                        try
                        {
                            PolicyFunctions.ValidateConcernProgress(p, review.Tick);
                        }
                        catch (DomainException e)
                        {
                            throw new DomainException($"{p.Concern.Value}: {e.Message}");
                        }
                        changed = true;
                    }
                    progress.Add(p);
                }
                if (!changed)
                {
                    return (false, methods);
                }
                PutSingleton(tx, "rounds", review with { Progress = EquatableArray.From(progress) }, MaxRoundsPayload);
                return (true, methods);
            },
            cancellationToken
        );
    }

    public Task<(bool Changed, IReadOnlyDictionary<ConcernId, string> Methods)> RecordPlannerReasonsAsync(
        IReadOnlyDictionary<ConcernId, PlannerNote> reasons,
        CancellationToken cancellationToken
    ) => Task.Run(() => RecordPlannerReasons(reasons, cancellationToken), cancellationToken);
}
