namespace RimGovernor.Brain.BuildingRuntime;

public interface IRoundsResearchSource
{
    (ResearchRead Read, BridgeResult Result) ReadResearch(CancellationToken ct, Common.Identity identity);
}

/// <summary>Composes EnsureResearch: picks the next project, builds the bench it needs, or waits on the current one.</summary>
public sealed class RoundsResearchPlanner
{
    internal const uint ResearchNativeWorkTicks = 2500;

    private readonly Rounder reviewer;
    private readonly IRoundsResearchSource native;
    private readonly RoundsBuildingPlanner? building;

    private RoundsResearchPlanner(Rounder reviewer, IRoundsResearchSource native, RoundsBuildingPlanner? building)
    {
        this.reviewer = reviewer;
        this.native = native;
        this.building = building;
    }

    public static RoundsResearchPlanner NewRoundsResearchPlanner(Rounder reviewer, IRoundsResearchSource native)
    {
        if (reviewer is null || native is null)
        {
            throw new ControlException("NewRoundsResearchPlanner: reviewer == null || native == null");
        }

        RoundsBuildingPlanner? building = null;
        if (native is IRoundsBuildingSource source && native is IRoundsSource)
        {
            building = new RoundsBuildingPlanner { Reviewer = reviewer, Native = source, Concern = ConcernId.EnsureResearch };
        }

        return new RoundsResearchPlanner(reviewer, native, building);
    }

    internal RoundsResearchResult Bench(CancellationToken call, CancellationToken epoch, StepArbiter arbiter, bool advanced)
    {
        if (building is null)
        {
            return new RoundsResearchResult { Verdict = BuildingRuntimePackage.BuildingResearchBench };
        }

        var copy = building.Copy();
        copy.AdvancedLab = advanced;
        var result = copy.Step(call, epoch, arbiter);
        return new RoundsResearchResult { Verdict = result.Verdict, NativeWorkTicks = result.NativeWorkTicks };
    }

    internal RoundsResearchResult Step(CancellationToken call, CancellationToken epoch, StepArbiter arbiter)
    {
        var p = reviewer.Player;
        var state = p.Session.State();
        if (!state.Enabled)
        {
            return new RoundsResearchResult { Verdict = BuildingRuntimePackage.BuildingReasonDisabled };
        }

        if (!state.ObservationKnown || !state.Snapshot.IsValid)
        {
            throw new ControlException("step: observation unknown or snapshot invalid");
        }

        var review = p.Journal.LoadRounds(call);
        if (!review.Enabled || review.Snapshot != state.Snapshot)
        {
            return new RoundsResearchResult { Verdict = BuildingRuntimePackage.BuildingReasonNoReview };
        }

        var (recordedCall, recorded) = BuildingRuntimePackage.RecordPlannerStep(call, ConcernId.EnsureResearch, state.Snapshot, review.Tick);
        call = recordedCall;
        using var recordedScope = recorded;
        var staged = reviewer.Staged();
        var items = reviewer.ItemFacts(call, state.Snapshot);
        var needs = BuildingRuntimePackage.RoundsResearchNeeds(call, p.Journal, staged, items, state.Snapshot);
        needs = reviewer.CensusResearchNeeds(needs);
        needs = PolicyFunctions.DeepDrillingResearch(needs, review.ResourceRunwayState(), items);
        if (needs.Count == 0 && staged.ResearchLadder.Count == 0)
        {
            return new RoundsResearchResult { Verdict = BuildingRuntimePackage.BuildingReasonDisabled };
        }

        var (goal, deficit) = p.Journal.Workable(call, review, ConcernId.EnsureResearch);
        if (deficit)
        {
            foreach (var method in goal.Methods)
            {
                var plan = p.Journal.LoadPlan(call, method.Plan);
                if (StorePackage.PlanOpen(plan))
                {
                    return new RoundsResearchResult { Verdict = BuildingRuntimePackage.BuildingReasonExistingWork };
                }
            }
        }

        var identity = BoundaryPackage.Identity(state.Snapshot);
        var (read, _) = native.ReadResearch(call, identity);
        try
        {
            BoundaryPackage.Context(read.Context, state.Snapshot);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ControlException("step: research context", e);
        }

        if (read.Context.Tick < review.Tick.Value)
        {
            throw new ControlException("step: research read older than the review");
        }

        var inputs = new ResearchCall { Policy = PolicyFunctions.ResearchPolicy(staged, review.Latches.Soldiers, review.Latches.FoodFirst), Needs = needs, Read = read };
        if (deficit)
        {
            SnapshotPackage.NoteResearch(call, inputs);
            var (knowledge, reason) = ResearchKnowledgeNext(inputs);
            if (!reason.IsZero)
            {
                return new RoundsResearchResult { Verdict = reason };
            }

            if (knowledge != "")
            {
                return Admit(call, epoch, state, goal, knowledge);
            }
        }

        if (read.CurrentProject != "")
        {
            if (deficit && PolicyFunctions.ResearchBenchNeeded(read.Projects[read.CurrentProject]))
            {
                var (ahead, admittedAhead) = BuildAhead(call, epoch, arbiter);
                if (admittedAhead)
                {
                    return ahead;
                }

                return Bench(call, epoch, arbiter, false);
            }

            if (deficit && building is not null)
            {
                var benchResult = Bench(call, epoch, arbiter, false);
                if (benchResult.Verdict != BuildingRuntimePackage.BuildingReasonNoDeficit)
                {
                    return benchResult;
                }

                var (ahead, admittedAhead) = BuildAhead(call, epoch, arbiter);
                if (admittedAhead)
                {
                    return ahead;
                }
            }

            return new RoundsResearchResult { Verdict = BuildingRuntimePackage.WaitFor(Cause.MethodUsed, "current_research_project"), NativeWorkTicks = ResearchNativeWorkTicks };
        }

        if (!deficit)
        {
            return new RoundsResearchResult { Verdict = BuildingRuntimePackage.BuildingReasonNoDeficit };
        }

        var (next, nextReason) = ResearchNext(inputs);
        if (!nextReason.IsZero)
        {
            return new RoundsResearchResult { Verdict = nextReason };
        }

        if (PolicyFunctions.ResearchBenchNeeded(read.Projects[next]))
        {
            var (ahead, admittedAhead) = BuildAhead(call, epoch, arbiter);
            if (admittedAhead)
            {
                return ahead;
            }

            return Bench(call, epoch, arbiter, false);
        }

        var (last, admittedLast) = BuildAhead(call, epoch, arbiter);
        if (admittedLast)
        {
            return last;
        }

        return Admit(call, epoch, state, goal, next);
    }

    internal (RoundsResearchResult Result, bool Admitted) BuildAhead(CancellationToken call, CancellationToken epoch, StepArbiter arbiter)
    {
        if (building is null)
        {
            return (default, false);
        }

        var result = Bench(call, epoch, arbiter, true);
        return (result, result.Verdict == BuildingRuntimePackage.BuildingReasonAdmitted);
    }

    internal RoundsResearchResult Admit(CancellationToken call, CancellationToken epoch, ControlState state, StandardState goal, string next)
    {
        var p = reviewer.Player;
        var digestNext = SHA256.HashData(Encoding.UTF8.GetBytes(next));
        var method = new MethodId("research-" + Convert.ToHexStringLower(digestNext.AsSpan(0, 16)));
        try
        {
            p.Journal.LoadOwnerMethod(call, goal, method);
            return new RoundsResearchResult { Verdict = BuildingRuntimePackage.WaitFor(Cause.MethodUsed, "research_selection") };
        }
        catch (NotFoundException)
        {
        }

        var id = PlanIds.MintPlanId();
        var value = ResearchSelect.NewResearchSelect(next);
        var action = DomainAction.NewResearchSelectAction(new ActionId($"{id}-0"), value);
        var plan = PlanSpec.NewPlan(id, new PlanRevision(1), [action]);
        p.Current(call, epoch);
        if (p.Session.State() != state)
        {
            throw new ControlException("step: session state changed");
        }

        p.Journal.CommitMethodReason(call, goal.Standard.Id, goal.Revision, method, "", plan);
        return new RoundsResearchResult { Verdict = BuildingRuntimePackage.BuildingReasonAdmitted, Plan = id };
    }

    internal static (string Project, Verdict Reason) ResearchKnowledgeNext(ResearchCall input)
    {
        var read = input.Read;
        var (head, reason) = ResearchNext(input);
        if (reason.IsZero)
        {
            var row = read.Projects[head];
            if (row.KnowledgeCategory != "")
            {
                foreach (var slot in read.Knowledge)
                {
                    if (slot.Category != row.KnowledgeCategory)
                    {
                        continue;
                    }

                    if (slot.Current != "")
                    {
                        return ("", BuildingRuntimePackage.WaitFor(Cause.MethodUsed, "knowledge_slot"));
                    }

                    if (!row.Census || row.LockReasons.Count != 0)
                    {
                        return ("", BuildingRuntimePackage.FieldUnavailable("research_knowledge_project"));
                    }

                    return (head, default);
                }

                return ("", BuildingRuntimePackage.FieldUnavailable("research_knowledge_slot"));
            }
        }

        return (PolicyFunctions.KnowledgePick(read.Projects, read.Finished, read.Knowledge), default);
    }

    internal static (string Project, Verdict Reason) ResearchNext(ResearchCall input)
    {
        var read = input.Read;
        var finished = read.Finished.Select(name => new ResearchProjectId(name)).ToList();
        var facts = new ResearchFacts
        {
            Current = new ResearchProjectId(read.CurrentProject),
            Finished = finished,
            Projects = read.Projects.Keys.Select(name => new ResearchProjectId(name)).ToList(),
        };
        var (target, _) = PolicyFunctions.ResearchConcern(input.Policy, input.Needs, Known(facts));
        if (target == "")
        {
            return ("", BuildingRuntimePackage.BuildingReasonNoDeficit);
        }

        if (!read.Projects.ContainsKey(target))
        {
            return ("", BuildingRuntimePackage.FieldUnavailable("research_project"));
        }

        var projects = read.Projects.ToDictionary(kv => new ResearchProjectId(kv.Key), kv => kv.Value);
        IReadOnlyList<ResearchProjectId> queue;
        try
        {
            queue = PolicyFunctions.ResearchPrerequisiteQueue(projects, finished, [new ResearchProjectId(target)]);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return ("", BuildingRuntimePackage.FieldUnavailable("research_prerequisites"));
        }

        if (queue.Count == 0)
        {
            return ("", BuildingRuntimePackage.FieldUnavailable("research_prerequisites"));
        }

        return (queue[0].Value, default);
    }
}

public sealed partial class RoundsBuildingPlanner
{
    internal (RoundsBuildingPlanner? Planner, Verdict Verdict) SelectResearchBench(ColonyProjection facts)
    {
        var bench = facts.Shapes.Furniture.BenchFor(RoomRole.Laboratory);
        if (AdvancedLab)
        {
            var (owed, ok) = AdvancedLabOwed(facts);
            if (!ok)
            {
                return (null, BuildingReasonNoDeficit);
            }

            bench = owed;
        }

        PlanningDefinition? definition = null;
        foreach (var d in facts.Definitions)
        {
            if (d.Name == bench)
            {
                definition = d;
            }
        }

        if (definition is null)
        {
            return (null, FieldUnavailable("research_bench_definition"));
        }

        if (!definition.Available.TryGetValue(out var available))
        {
            return (null, FieldUnavailable("research_bench_availability"));
        }

        if (!available)
        {
            return (null, BuildingResearchBenchUnavailable);
        }

        var facility = PolicyFunctions.Facility(RoomRole.Laboratory);
        var resolved = Copy();
        resolved.Definition = bench;
        resolved.Environment = PlacementEnvironment.Indoors;
        resolved.Facility = facility;
        resolved.Stuff = facts.BulkBuildStuff(bench, 1);
        return (resolved, default);
    }

    internal static (string Definition, bool Owed) AdvancedLabOwed(ColonyProjection facts)
    {
        var furniture = facts.Shapes.Furniture;
        if (furniture.AdvancedLab == "")
        {
            return ("", false);
        }

        var standing = new HashSet<string>();
        foreach (var c in facts.Cells)
        {
            var d = c.PlayerEdifice();
            if (d != "")
            {
                standing.Add(d);
            }
        }

        var next = furniture.AdvancedLab;
        if (standing.Contains(next))
        {
            next = furniture.Analyzer.Def;
        }

        if (standing.Contains(next))
        {
            return ("", false);
        }

        foreach (var d in facts.Definitions)
        {
            if (d.Name == next)
            {
                if (d.Available.TryGetValue(out var available) && available)
                {
                    return (next, true);
                }

                return ("", false);
            }
        }

        return ("", false);
    }
}

public readonly record struct RoundsResearchResult
{
    public Verdict Verdict { get; init; }

    public PlanId Plan { get; init; }

    public uint NativeWorkTicks { get; init; }
}
