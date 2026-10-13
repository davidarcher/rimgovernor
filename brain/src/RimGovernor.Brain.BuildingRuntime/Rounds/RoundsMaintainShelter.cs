namespace RimGovernor.Brain.BuildingRuntime;

internal sealed class OwnedArea
{
    public IReadOnlyList<Cell> Current { get; set; } = [];

    public bool Known { get; set; }

    public IReadOnlyList<Cell> Want { get; set; } = [];
}

/// <summary>Remembers the bot-owned allowed areas so each review plans only the edits that differ.</summary>
public sealed class SafeAreaMemory
{
    private readonly object mu = new();
    private string world = "";
    private Dictionary<string, OwnedArea>? areas;
    private List<Area> edits = [];
    private Tick last;
    private bool lastKnown;
    private HashSet<Cell>? danger;

    private void Enter(string newWorld)
    {
        if (world != newWorld || areas is null)
        {
            world = newWorld;
            areas = [];
            edits = [];
            lastKnown = false;
            danger = null;
        }
    }

    private void Plan(string key, IReadOnlyList<Cell> want)
    {
        if (!areas!.TryGetValue(key, out var a))
        {
            a = new OwnedArea();
            areas[key] = a;
        }

        var planned = PolicyFunctions.PlanBotArea(key, want, a.Current, a.Known);
        a.Want = want;
        edits.AddRange(planned);
    }

    public Fact<bool> Review(string newWorld, ColonyProjection projection)
    {
        if (!projection.Rooms.TryGetValue(out var rooms))
        {
            return Unknown<bool>();
        }

        IReadOnlyList<Cell> killbox = [], hospital = [], isolation = [];
        if (projection.LayoutPlan.TryGetValue(out var plan))
        {
            killbox = plan.KillboxCells();
            hospital = plan.HospitalCells();
            isolation = plan.IsolationRoomCells();
        }

        lock (mu)
        {
            Enter(newWorld);
            edits = [];
            Plan(PolicyFunctions.SafeAreaKey, PolicyFunctions.SafeAreaCells(rooms, [.. killbox, .. isolation]));
            if (hospital.Count > 0)
            {
                Plan(PolicyFunctions.HospitalAreaKey, hospital);
            }

            if (isolation.Count > 0)
            {
                Plan(PolicyFunctions.IsolationAreaKey, isolation);
            }

            if (projection.Facts.HomeCoverage.TryGetValue(out var census) && census.Home.TryGetValue(out var home) && home.Count > 0)
            {
                Plan(PolicyFunctions.NoDangerAreaKey, PolicyFunctions.NoDangerCells(home, killbox, BuildingRuntimePackage.SortedSeeds(danger)));
            }

            return Known(edits.Count > 0);
        }
    }

    public IReadOnlyList<Area> Take(string newWorld)
    {
        lock (mu)
        {
            Enter(newWorld);
            return edits;
        }
    }

    public void Commit(string newWorld)
    {
        lock (mu)
        {
            Enter(newWorld);
            foreach (var a in areas!.Values)
            {
                a.Current = a.Want;
                a.Known = true;
            }

            edits = [];
        }
    }

    public Fact<bool> DangerWindow(string newWorld, Fact<long> hostiles, IReadOnlyList<Cell> seeds, Tick tick)
    {
        lock (mu)
        {
            Enter(newWorld);
            if (hostiles.TryGetValue(out var n) && n > 0)
            {
                last = tick;
                lastKnown = true;
            }

            var window = PolicyFunctions.DangerWindowOf(hostiles, last, lastKnown, tick);
            if (window.TryGetValue(out var open) && open)
            {
                danger ??= [];
                foreach (var c in seeds)
                {
                    danger.Add(c);
                }
            }
            else if (window.IsKnown)
            {
                danger = null;
            }

            return window;
        }
    }
}

internal static partial class BuildingRuntimePackage
{
    internal static Fact<IReadOnlyList<Cell>> DangerSeedFact(Fact<long> hostiles, IReadOnlyList<EmergencyThreat> threats) =>
        hostiles.IsKnown ? Known(PolicyFunctions.DangerSeeds(threats)) : Unknown<IReadOnlyList<Cell>>();

    internal static IReadOnlyList<Cell> SortedSeeds(HashSet<Cell>? set) =>
        set is null ? [] : [.. set.OrderBy(c => c.Z).ThenBy(c => c.X)];
}

public sealed class MaintainShelterPlanner
{
    private readonly Rounder reviewer;

    private MaintainShelterPlanner(Rounder reviewer) => this.reviewer = reviewer;

    public static MaintainShelterPlanner NewMaintainShelterPlanner(Rounder reviewer)
    {
        if (reviewer is null || !reviewer.MethodEnabled(ConcernId.MaintainShelter))
        {
            throw new ControlException("NewMaintainShelterPlanner: reviewer missing or method disabled");
        }

        return new MaintainShelterPlanner(reviewer);
    }

    internal MaintainShelterResult Step(CancellationToken call, CancellationToken epoch, StepArbiter? arbiter)
    {
        var p = reviewer.Player;
        var state = p.Session.State();
        if (!state.Enabled)
        {
            return new MaintainShelterResult { Verdict = BuildingRuntimePackage.BuildingReasonDisabled };
        }

        if (!state.ObservationKnown || !state.Snapshot.IsValid)
        {
            throw new ControlException("step: observation unknown or snapshot invalid");
        }

        var review = p.Journal.LoadRounds(call);
        if (!review.Enabled || review.Snapshot != state.Snapshot)
        {
            return new MaintainShelterResult { Verdict = BuildingRuntimePackage.BuildingReasonNoReview };
        }

        var (goal, workable) = p.Journal.Workable(call, review, ConcernId.MaintainShelter);
        if (!workable)
        {
            return new MaintainShelterResult { Verdict = BuildingRuntimePackage.BuildingReasonNoDeficit };
        }

        foreach (var method0 in goal.Methods)
        {
            var open = p.Journal.LoadPlan(call, method0.Plan);
            if (StorePackage.PlanOpen(open))
            {
                return new MaintainShelterResult { Verdict = BuildingRuntimePackage.BuildingReasonExistingWork };
            }
        }

        var world = BuildingRuntimePackage.StockpileWorld(state.Snapshot);
        var edits = reviewer.SafeArea.Take(world);
        if (edits.Count == 0)
        {
            return new MaintainShelterResult { Verdict = BuildingRuntimePackage.WaitFor(Cause.MethodUsed, "safe_area_edits") };
        }

        var id = PlanIds.MintPlanId();
        var actions = new List<DomainAction>(edits.Count);
        for (var i = 0; i < edits.Count; i++)
        {
            actions.Add(DomainAction.NewAreaAction(new ActionId($"{id}-{i}"), edits[i]));
        }

        var plan = PlanSpec.NewPlan(id, new PlanRevision(1), actions);
        p.Current(call, epoch);
        if (p.Session.State() != state)
        {
            throw new ControlException("step: session state changed");
        }

        var method = new MethodId($"safe-area-{id}");
        p.Journal.CommitMethodReason(call, goal.Standard.Id, goal.Revision, method, $"bot areas {edits.Count} edits", plan);
        reviewer.SafeArea.Commit(world);
        return new MaintainShelterResult { Verdict = BuildingRuntimePackage.BuildingReasonAdmitted, Plan = id };
    }
}

public readonly record struct MaintainShelterResult
{
    public Verdict Verdict { get; init; }

    public PlanId Plan { get; init; }
}
