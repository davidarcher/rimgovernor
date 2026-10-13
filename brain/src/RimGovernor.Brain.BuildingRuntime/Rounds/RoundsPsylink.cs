namespace RimGovernor.Brain.BuildingRuntime;

public interface IRoundsPsylinkSource
{
    (IReadOnlyList<string> Items, BridgeResult Result) ReadNeuroformerItems(CancellationToken ct, Common.Identity identity, string neuroformer);
}

/// <summary>Remembers the last review's psylink candidates so the planner step acts on what the review saw.</summary>
internal sealed class PsylinkMemory
{
    private readonly IRoundsPsylinkSource native;
    private readonly object mu = new();
    private string world = "";
    private Tick tick;
    private Fact<IReadOnlyList<PawnId>> who = Unknown<IReadOnlyList<PawnId>>();
    private List<PawnId> up = [];
    private List<string> items = [];

    public PsylinkMemory(IRoundsPsylinkSource native) => this.native = native;

    public (Fact<IReadOnlyList<PawnId>> Candidates, Fact<bool> Owed) Review(CancellationToken ct, Common.Identity identity, GenerationSnapshot current, ColonyProjection projection)
    {
        var candidates = PolicyFunctions.PsylinkCandidates(projection.Royalty, projection.WorkPawns);
        var levelUps = PolicyFunctions.PsylinkLevelUps(projection.Royalty, projection.WorkPawns);
        var psylinkItems = Unknown<IReadOnlyList<string>>();
        var ok1 = candidates.TryGetValue(out var first);
        var ok2 = levelUps.TryGetValue(out var upList);
        if (ok1 && ok2 && first!.Count + upList!.Count > 0)
        {
            projection.Royalty.TryGetValue(out var royalty);
            if (royalty!.Neuroformers.TryGetValue(PolicyFunctions.PsylinkNeuroformer, out var nf) && nf.Held.TryGetValue(out var held) && held == 0)
            {
                psylinkItems = Known<IReadOnlyList<string>>([]);
            }
            else if (royalty.Neuroformers.ContainsKey(PolicyFunctions.PsylinkNeuroformer))
            {
                try
                {
                    var (read, _) = native.ReadNeuroformerItems(ct, identity, PolicyFunctions.PsylinkNeuroformer);
                    psylinkItems = Known(read);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // an unreadable neuroformer census leaves the items unknown
                }
            }
        }

        psylinkItems.TryGetValue(out var heldItems);
        lock (mu)
        {
            world = BuildingRuntimePackage.StockpileWorld(current);
            tick = projection.Identity.Tick;
            who = candidates;
            up = [.. upList ?? []];
            items = [.. heldItems ?? []];
        }

        return (candidates, PolicyFunctions.PsylinkOwed(candidates, levelUps, psylinkItems));
    }

    public (IReadOnlyList<PawnId> Candidates, IReadOnlyList<PawnId> LevelUps, IReadOnlyList<string> Items, bool Ok) Take(string newWorld, Tick newTick)
    {
        lock (mu)
        {
            var known = who.TryGetValue(out var list);
            if (world != newWorld || tick != newTick || !known)
            {
                return ([], [], [], false);
            }

            return ([.. list!], [.. up], [.. items], true);
        }
    }
}

public sealed partial class Rounder
{
    internal PsylinkMemory? Psylink { get; set; }
}

/// <summary>Composes MaintainPsylink: use a neuroformer on a candidate colonist.</summary>
public sealed class RoundsPsylinkPlanner
{
    internal const string PsylinkPrefix = "psylink-";

    private readonly Rounder reviewer;
    private readonly PsylinkMemory memory;

    private RoundsPsylinkPlanner(Rounder reviewer, PsylinkMemory memory)
    {
        this.reviewer = reviewer;
        this.memory = memory;
    }

    public static RoundsPsylinkPlanner NewRoundsPsylinkPlanner(Rounder reviewer, IRoundsPsylinkSource native)
    {
        if (reviewer is null || native is null || !reviewer.MethodEnabled(ConcernId.MaintainPsylink))
        {
            throw new ControlException("NewRoundsPsylinkPlanner: reviewer, native or method disabled");
        }

        reviewer.Psylink = new PsylinkMemory(native);
        return new RoundsPsylinkPlanner(reviewer, reviewer.Psylink);
    }

    internal RoundsPsylinkResult Step(CancellationToken call, CancellationToken epoch, StepArbiter arbiter)
    {
        var p = reviewer.Player;
        var state = p.Session.State();
        if (!state.Enabled)
        {
            return new RoundsPsylinkResult { Verdict = BuildingRuntimePackage.BuildingReasonDisabled };
        }

        if (!state.ObservationKnown || !state.Snapshot.IsValid)
        {
            throw new ControlException("step: observation unknown or snapshot invalid");
        }

        var review = p.Journal.LoadRounds(call);
        if (!review.Enabled || review.Snapshot != state.Snapshot)
        {
            return new RoundsPsylinkResult { Verdict = BuildingRuntimePackage.BuildingReasonNoReview };
        }

        var (goal, workable) = p.Journal.Workable(call, review, ConcernId.MaintainPsylink);
        if (!workable)
        {
            return new RoundsPsylinkResult { Verdict = BuildingRuntimePackage.BuildingReasonNoDeficit };
        }

        foreach (var method0 in goal.Methods)
        {
            var open = p.Journal.LoadPlan(call, method0.Plan);
            if (StorePackage.PlanOpen(open))
            {
                return new RoundsPsylinkResult { Verdict = BuildingRuntimePackage.BuildingReasonExistingWork };
            }
        }

        var (candidates, levelUps, items, ok) = memory.Take(BuildingRuntimePackage.StockpileWorld(state.Snapshot), review.Tick);
        if (!ok)
        {
            return new RoundsPsylinkResult { Verdict = BuildingRuntimePackage.BuildingReasonNoReview };
        }

        Verdict barred = default;
        List<PawnId> WillingOf(IReadOnlyList<PawnId> who)
        {
            var output = new List<PawnId>();
            foreach (var pawn in who)
            {
                var willingPrefix = $"{PsylinkPrefix}{pawn}-";
                var (verdict, admitted) = BuildingRuntimePackage.AdmitSubject(call, p.Journal, willingPrefix, BuildingRuntimePackage.StandardMethodPlans(goal.History, goal.Standard.Episode, willingPrefix), state.Snapshot);
                if (admitted)
                {
                    output.Add(pawn);
                }
                else
                {
                    barred = verdict;
                }
            }

            return output;
        }

        var (choice, owed) = PolicyFunctions.NextPsylinkUse(WillingOf(candidates), WillingOf(levelUps), items);
        if (!owed)
        {
            if (barred != default)
            {
                return new RoundsPsylinkResult { Verdict = barred };
            }

            return new RoundsPsylinkResult { Verdict = BuildingRuntimePackage.WaitFor(Cause.MethodUsed, "psylink_item") };
        }

        if (!arbiter.TryClaim([new PawnId(choice.Pawn)]))
        {
            return new RoundsPsylinkResult { Verdict = BuildingRuntimePackage.WaitFor(Cause.MethodUsed, "psylink_pawn_claim") };
        }

        var prefix = $"{PsylinkPrefix}{choice.Pawn}-";
        var use = UseItem.NewUseItem(new PawnId(choice.Pawn), choice.Item, new PawnId(choice.Pawn));
        var id = PlanIds.MintPlanId();
        var action = DomainAction.NewUseItemAction(new ActionId($"{id}-0"), use);
        var plan = PlanSpec.NewPlan(id, new PlanRevision(1), [action]);
        p.Current(call, epoch);
        if (p.Session.State() != state)
        {
            throw new ControlException("step: session state changed");
        }

        var method = BuildingRuntimePackage.NextMethodId(prefix, BuildingRuntimePackage.HistoryMethodIds(goal.History, goal.Standard.Episode));
        p.Journal.CommitMethodReason(call, goal.Standard.Id, goal.Revision, method, $"psylink: {choice.Pawn} uses neuroformer {choice.Item}", plan);
        return new RoundsPsylinkResult { Verdict = BuildingRuntimePackage.BuildingReasonAdmitted, Plan = id };
    }
}

public readonly record struct RoundsPsylinkResult
{
    public Verdict Verdict { get; init; }

    public PlanId Plan { get; init; }
}
