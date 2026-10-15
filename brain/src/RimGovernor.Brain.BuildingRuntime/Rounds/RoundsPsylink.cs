namespace RimGovernor.Brain.BuildingRuntime;

public interface IRoundsPsylinkSource
{
    Task<(IReadOnlyList<string> Items, BridgeResult Result)> ReadNeuroformerItemsAsync(C.Identity identity, string neuroformer, CancellationToken ct);
}

/// <summary>Remembers the last review's psylink candidates so the planner step acts on what the review saw.</summary>
internal sealed class PsylinkMemory
{
    private readonly IRoundsPsylinkSource native;
    private readonly object mu = new();
    private string world = "";
    private Tick tick;
    private Fact<EquatableArray<PawnId>> who = Unknown<EquatableArray<PawnId>>();
    private List<PawnId> up = [];
    private List<string> items = [];

    public PsylinkMemory(IRoundsPsylinkSource native) => this.native = native;

    public async Task<(Fact<EquatableArray<PawnId>> Candidates, Fact<bool> Owed)> ReviewAsync(C.Identity identity, GenerationSnapshot current, ColonyProjection projection, CancellationToken ct)
    {
        var candidates = PolicyFunctions.PsylinkCandidates(projection.Royalty, projection.WorkPawns);
        var levelUps = PolicyFunctions.PsylinkLevelUps(projection.Royalty, projection.WorkPawns);
        var psylinkItems = Unknown<EquatableArray<string>>();
        var ok1 = candidates.TryGetValue(out var first);
        var ok2 = levelUps.TryGetValue(out var upList);
        if (ok1 && ok2 && first!.Count + upList!.Count > 0)
        {
            projection.Royalty.TryGetValue(out var royalty);
            if (royalty!.Neuroformers.TryGetValue(PolicyConstants.PsylinkNeuroformer, out var nf) && nf.Held.TryGetValue(out var held) && held == 0)
            {
                psylinkItems = Known<EquatableArray<string>>([]);
            }
            else if (royalty.Neuroformers.ContainsKey(PolicyConstants.PsylinkNeuroformer))
            {
                try
                {
                    var (read, _) = await native.ReadNeuroformerItemsAsync(identity, PolicyConstants.PsylinkNeuroformer, ct).ConfigureAwait(false);
                    psylinkItems = Known<EquatableArray<string>>([.. read]);
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
            up = [.. upList];
            items = [.. heldItems];
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

            return ([.. list], [.. up], [.. items], true);
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

    private readonly Rounder reviewer;
    private readonly PsylinkMemory memory;

    private RoundsPsylinkPlanner(Rounder reviewer, PsylinkMemory memory)
    {
        this.reviewer = reviewer;
        this.memory = memory;
    }

    public static RoundsPsylinkPlanner NewRoundsPsylinkPlanner(Rounder reviewer, IRoundsPsylinkSource native)
    {
        if (reviewer is null || native is null || !reviewer.MethodEnabled(PolicyConstants.MaintainPsylink))
        {
            throw new ControlException("NewRoundsPsylinkPlanner: reviewer, native or method disabled");
        }

        reviewer.Psylink = new PsylinkMemory(native);
        return new RoundsPsylinkPlanner(reviewer, reviewer.Psylink);
    }

    internal async Task<RoundsPsylinkResult> StepAsync(StepArbiter arbiter, CancellationToken call, CancellationToken epoch)
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

        var review = await p.Journal.LoadRoundsAsync(call).ConfigureAwait(false);
        if (!review.Enabled || review.Snapshot != state.Snapshot)
        {
            return new RoundsPsylinkResult { Verdict = BuildingRuntimePackage.BuildingReasonNoReview };
        }

        var (goal, workable) = await p.Journal.WorkableAsync(review, PolicyConstants.MaintainPsylink, call).ConfigureAwait(false);
        if (!workable || goal is null)
        {
            return new RoundsPsylinkResult { Verdict = BuildingRuntimePackage.BuildingReasonNoDeficit };
        }

        foreach (var method0 in goal.Methods)
        {
            var open = await p.Journal.LoadPlanAsync(method0.Plan, call).ConfigureAwait(false);
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

        return new RoundsPsylinkResult { Verdict = Verdict.AwaitingPlan("unsupported_forecast_neuroformer",
            "Neuroformer use requires shared forecast participant, timing and need effects.") };
    }
}

public readonly record struct RoundsPsylinkResult : IPlannerMethod
{
    public Verdict Verdict { get; init; }

    public PlanId Plan { get; init; }
}
