namespace RimGovernor.Brain.BuildingRuntime;

public interface IRoundsResearchSource : Observation.IResearchSource
{
}

/// <summary>
/// The runtime half of the economy family's research Need (#3263): its read (the research the colony's evidence
/// waits on, then the native research census) and its building work, the laboratory benches raised under the
/// <c>economy/research</c> Need through the shared building planner and admitted by claims.
/// </summary>
public sealed class EconomyRounds
{
    private readonly Rounder _reviewer;
    private readonly IRoundsResearchSource _native;
    private readonly RoundsBuildingPlanner? _building;

    public EconomyRounds(Rounder reviewer, IRoundsResearchSource native)
    {
        _reviewer = reviewer ?? throw new ControlException("EconomyRounds: reviewer is missing");
        _native = native ?? throw new ControlException("EconomyRounds: native is missing");
        if (native is IRoundsBuildingSource source && native is IRoundsSource)
        {
            _building = new RoundsBuildingPlanner { Reviewer = reviewer, Native = source, Concern = EconomyFamily.Research.FilingKey, Need = EconomyFamily.Research };
        }
    }

    /// <summary>The family's scheduler entry: it wakes on its reads and on its research selection and bench outcomes.</summary>
    public FamilyRunner Runner() =>
        new FamilyRunner<EconomyFacts>(new EconomyFamily(), ReadAsync, new FamilySchedule { Kinds = [ActionKind.ResearchSelect, ActionKind.Building] }, WorkAsync);

    /// <summary>
    /// Reads the family's facts in <paramref name="scope"/>'s world: the research needs (the production ladder's
    /// rung, planners' research waits, the census requests, deep drilling) and, when any is owed, the native
    /// research census. Throws when the read is from another world, older than the review, or slower than the
    /// review's age bound.
    /// </summary>
    internal async Task<EconomyFacts> ReadAsync(FamilyReadScope scope, CancellationToken ct)
    {
        if (scope.Immediate)
        {
            // No SeriousHarm Needs: the immediate pass reads nothing.
            return new EconomyFacts();
        }
        var started = _reviewer.Clock.Now();
        var p = _reviewer.Player;
        var review = await p.Journal.LoadRoundsAsync(ct).ConfigureAwait(false);
        var expected = await StepScopeAsync(_reviewer.Native, ct).ConfigureAwait(false);
        if (!RoundsBuildingBoundary(expected, scope.World, review.Tick))
        {
            throw new ControlException("economy read: the scope is another world or older than the review");
        }
        var items = await _reviewer.ItemFactsAsync(scope.World, ct).ConfigureAwait(false);
        var needs = RoundsResearchNeeds(p.Journal, _reviewer.Policy, items, scope.World, _reviewer.EquipmentOwed(scope.World), ct);
        needs = _reviewer.CensusResearchNeeds(needs);
        needs = PolicyFunctions.DeepDrillingResearch(needs, review.ResourceRunwayState(), items);
        var facts = new EconomyFacts { ResearchNeeds = [.. needs] };
        if (needs.Count == 0)
        {
            // Nothing owed: the Need is met without the census.
            return facts;
        }
        var (read, _) = await _native.ReadResearchAsync(Boundary.Identity(scope.World), ct).ConfigureAwait(false);
        try
        {
            Boundary.Context(read.Context, scope.World);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ControlException("economy read: research context", e);
        }
        if ((read.Context?.Tick ?? 0) < review.Tick.Value)
        {
            throw new ControlException("economy read: research read older than the review");
        }
        var elapsed = _reviewer.Clock.Now() - started;
        if (elapsed < TimeSpan.Zero || elapsed > _reviewer.MaxAge)
        {
            throw new ControlException("economy read: the read took too long");
        }
        return facts with { Research = Known(read.Census()) };
    }

    /// <summary>
    /// The research Need's building work beside its pure selection: the simple bench while a project is current
    /// or the next one waits on it, and the high-tech bench and its analyzer ahead of need. Each is a Method of
    /// the Need, admitted by its claims, so they go side by side with the selection. The bench's verdict is the
    /// Need's when the next project waits on that bench; otherwise only an admission or open work is reported,
    /// and the pure hold stands.
    /// </summary>
    internal async Task<Verdict> WorkAsync(NeedWork work, EconomyFacts facts, CancellationToken ct)
    {
        var idle = Verdict.AwaitingPlan(work.Need.Id.Value, "no_method");
        if (work.Need.Id != EconomyFamily.Research || _building is null)
        {
            return idle;
        }
        var step = PolicyFunctions.ResearchStepOf(facts);
        var result = idle;
        if (step.Bench)
        {
            var bench = await BenchAsync(work, false, ct).ConfigureAwait(false);
            if (bench.Outcome == Outcome.Admitted || bench == BuildingReasonExistingWork
                || (step.Hold?.Subject == "research_bench" && bench != BuildingReasonNoDeficit))
            {
                result = bench;
            }
        }
        if (step.BuildAhead)
        {
            var ahead = await BenchAsync(work, true, ct).ConfigureAwait(false);
            if (ahead.Outcome == Outcome.Admitted)
            {
                result = ahead;
            }
        }
        return result;
    }

    private async Task<Verdict> BenchAsync(NeedWork work, bool advanced, CancellationToken ct)
    {
        var copy = _building!.Copy();
        copy.AdvancedLab = advanced;
        return (await copy.StepAsync(work.Arbiter!, ct, work.Epoch).ConfigureAwait(false)).Verdict;
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
