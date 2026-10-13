namespace RimGovernor.Brain.BuildingRuntime;

public interface IRoundsResearchSource : Observation.IResearchSource
{
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
            building = new RoundsBuildingPlanner { Reviewer = reviewer, Native = source, Concern = PolicyFunctions.EnsureResearch };
        }

        return new RoundsResearchPlanner(reviewer, native, building);
    }

    internal async Task<RoundsResearchResult> BenchAsync(StepArbiter arbiter, bool advanced, CancellationToken call, CancellationToken epoch)
    {
        if (building is null)
        {
            return new RoundsResearchResult { Verdict = BuildingRuntimePackage.BuildingReasonResearchBench };
        }

        var copy = building.Copy();
        copy.AdvancedLab = advanced;
        var result = await copy.StepAsync(arbiter, call, epoch).ConfigureAwait(false);
        return new RoundsResearchResult { Verdict = result.Verdict, NativeWorkTicks = result.NativeWorkTicks };
    }

    internal async Task<RoundsResearchResult> StepAsync(StepArbiter arbiter, CancellationToken call, CancellationToken epoch)
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

        using var recorded = PlannerSnapshot.RecordPlannerStep(PolicyFunctions.EnsureResearch, state.Snapshot, review.Tick);
        try
        {
            return await StepRecordedAsync(arbiter, p, state, review, call, epoch).ConfigureAwait(false);
        }
        finally
        {
            recorded.Finish();
        }
    }

    private async Task<RoundsResearchResult> StepRecordedAsync(StepArbiter arbiter, Player p, ControlState state, Sto.Rounds review, CancellationToken call, CancellationToken epoch)
    {
        var staged = reviewer.Staged();
        var items = await reviewer.ItemFactsAsync(state.Snapshot, call).ConfigureAwait(false);
        var needs = BuildingRuntimePackage.RoundsResearchNeeds(p.Journal, staged, items, state.Snapshot, call);
        needs = reviewer.CensusResearchNeeds(needs);
        needs = PolicyFunctions.DeepDrillingResearch(needs, review.ResourceRunwayState(), items);
        if (needs.Count == 0 && staged.ResearchLadder.Count == 0)
        {
            return new RoundsResearchResult { Verdict = BuildingRuntimePackage.BuildingReasonDisabled };
        }

        var (goal, deficit) = p.Journal.Workable(review, PolicyFunctions.EnsureResearch, call);
        if (deficit && goal is not null)
        {
            foreach (var method in goal.Methods)
            {
                var plan = p.Journal.LoadPlan(method.Plan, call);
                if (StorePackage.PlanOpen(plan))
                {
                    return new RoundsResearchResult { Verdict = BuildingRuntimePackage.BuildingReasonExistingWork };
                }
            }
        }

        var identity = Boundary.Identity(state.Snapshot);
        var (read, _) = await native.ReadResearchAsync(identity, call).ConfigureAwait(false);
        try
        {
            Boundary.Context(read.Context, state.Snapshot);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ControlException("step: research context", e);
        }

        if ((read.Context?.Tick ?? 0) < review.Tick.Value)
        {
            throw new ControlException("step: research read older than the review");
        }

        var inputs = new ResearchCall(PolicyFunctions.ResearchPolicy(staged, review.Latches.Soldiers, review.Latches.FoodFirst), needs, read);
        if (deficit && goal is not null)
        {
            SnapshotPackage.NoteResearch(inputs);
            var (knowledge, reason) = ResearchKnowledgeNext(inputs);
            if (!reason.IsZero)
            {
                return new RoundsResearchResult { Verdict = reason };
            }

            if (knowledge != "")
            {
                return Admit(state, goal, knowledge, call, epoch);
            }
        }

        if (read.CurrentProject != "")
        {
            if (deficit && PolicyFunctions.ResearchBenchNeeded(read.Projects[read.CurrentProject]))
            {
                var (ahead, admittedAhead) = await BuildAheadAsync(arbiter, call, epoch).ConfigureAwait(false);
                if (admittedAhead)
                {
                    return ahead;
                }

                return await BenchAsync(arbiter, false, call, epoch).ConfigureAwait(false);
            }

            if (deficit && building is not null)
            {
                var benchResult = await BenchAsync(arbiter, false, call, epoch).ConfigureAwait(false);
                if (benchResult.Verdict != BuildingRuntimePackage.BuildingReasonNoDeficit)
                {
                    return benchResult;
                }

                var (ahead, admittedAhead) = await BuildAheadAsync(arbiter, call, epoch).ConfigureAwait(false);
                if (admittedAhead)
                {
                    return ahead;
                }
            }

            return new RoundsResearchResult { Verdict = Verdict.WaitFor(Cause.MethodUsed, "current_research_project"), NativeWorkTicks = ResearchNativeWorkTicks };
        }

        if (!deficit || goal is null)
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
            var (ahead, admittedAhead) = await BuildAheadAsync(arbiter, call, epoch).ConfigureAwait(false);
            if (admittedAhead)
            {
                return ahead;
            }

            return await BenchAsync(arbiter, false, call, epoch).ConfigureAwait(false);
        }

        var (last, admittedLast) = await BuildAheadAsync(arbiter, call, epoch).ConfigureAwait(false);
        if (admittedLast)
        {
            return last;
        }

        return Admit(state, goal, next, call, epoch);
    }

    internal async Task<(RoundsResearchResult Result, bool Admitted)> BuildAheadAsync(StepArbiter arbiter, CancellationToken call, CancellationToken epoch)
    {
        if (building is null)
        {
            return (default, false);
        }

        var result = await BenchAsync(arbiter, true, call, epoch).ConfigureAwait(false);
        return (result, result.Verdict == BuildingRuntimePackage.BuildingReasonAdmitted);
    }

    internal RoundsResearchResult Admit(ControlState state, StandardState goal, string next, CancellationToken call, CancellationToken epoch)
    {
        var p = reviewer.Player;
        var digestNext = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(next));
        var method = new MethodId("research-" + Convert.ToHexStringLower(digestNext.AsSpan(0, 16)));
        try
        {
            p.Journal.LoadOwnerMethod(goal, method, call);
            return new RoundsResearchResult { Verdict = Verdict.WaitFor(Cause.MethodUsed, "research_selection") };
        }
        catch (NotFoundException)
        {
        }

        var id = PlanIds.MintPlanId();
        var value = new ResearchSelect(next);
        var action = DomainAction.NewResearchSelectAction(new ActionId($"{id}-0"), value);
        var plan = new PlanSpec(id, new PlanRevision(1), [action]);
        p.Current(call, epoch);
        if (p.Session.State() != state)
        {
            throw new ControlException("step: session state changed");
        }

        p.Journal.CommitMethodReason(goal.Standard.Id, goal.Revision, method, "", plan, call);
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

                    if (!slot.Current.IsEmpty)
                    {
                        return ("", Verdict.WaitFor(Cause.MethodUsed, "knowledge_slot"));
                    }

                    if (!row.Census || row.LockReasons.Count != 0)
                    {
                        return ("", Verdict.FieldUnavailable("research_knowledge_project"));
                    }

                    return (head, default);
                }

                return ("", Verdict.FieldUnavailable("research_knowledge_slot"));
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
            Finished = [.. finished],
            Projects = [.. read.Projects.Keys.Select(name => new ResearchProjectId(name))],
        };
        var (target, _) = PolicyFunctions.ResearchConcern(input.Policy, input.Needs, Known(facts));
        if (target == "")
        {
            return ("", BuildingRuntimePackage.BuildingReasonNoDeficit);
        }

        if (!read.Projects.ContainsKey(target))
        {
            return ("", Verdict.FieldUnavailable("research_project"));
        }

        var projects = read.Projects.ToDictionary(kv => new ResearchProjectId(kv.Key), kv => kv.Value);
        IReadOnlyList<ResearchProjectId> queue;
        try
        {
            queue = PolicyFunctions.ResearchPrerequisiteQueue(projects, finished, [new ResearchProjectId(target)]);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return ("", Verdict.FieldUnavailable("research_prerequisites"));
        }

        if (queue.Count == 0)
        {
            return ("", Verdict.FieldUnavailable("research_prerequisites"));
        }

        return (queue[0].Value, default);
    }
}

public sealed partial class RoundsBuildingPlanner
{
    internal (RoundsBuildingPlanner? Planner, Verdict Verdict) SelectResearchBench(ColonyProjection facts)
    {
        var bench = facts.Shapes.Furniture.BenchFor(Pol.RoomRole.Laboratory);
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
            return (null, BuildingReasonResearchBenchUnavailable);
        }

        var facility = PolicyFunctions.Facility(Pol.RoomRole.Laboratory);
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

public readonly record struct RoundsResearchResult : IPlannerMethod
{
    public Verdict Verdict { get; init; }

    public PlanId Plan { get; init; }

    public uint NativeWorkTicks { get; init; }
}
