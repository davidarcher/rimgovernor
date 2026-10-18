namespace RimGovernor.Brain.Policy;

// The marginal value of project progress (#3531 of #3470), by perturbation: the model is run as it is, then once per valued
// project (research and sowing, the work tier 1 can offer) with one hour of progress injected at time zero; the difference of the
// two trajectories' values is what an hour of that work is worth. A project whose completion falls outside the horizon, or whose
// crop cannot mature, changes nothing and so is worth nothing. Labor allocation is a fixed order, so there is no search.

/// <summary>The tier-2 run of a review and what an hour of each valued project is worth in it.</summary>
public sealed record ProjectMarginals
{
    /// <summary>The run as it is.</summary>
    public ColonyTrajectory Trajectory { get; init; } = new();

    /// <summary>The run's value in capability-days; unknown when its hunger could not be priced (then there are no marginals).</summary>
    public Fact<double> Value { get; init; }

    /// <summary>The run's gaps and the value's unassessed parts, sorted.</summary>
    public EquatableArray<string> Findings { get; init; }

    /// <summary>Capability-days one hour of the project's work adds (dV/dt), by node id.</summary>
    public IReadOnlyDictionary<string, double> PerHour { get; init; } = new Dictionary<string, double>();

    /// <summary>
    /// Capability-days a unit of the project's progress adds, by <see cref="ProjectGraph.ProgressKey"/>: per research point, per
    /// cell sown. This is what tier 1 multiplies the progress of a task by (<see cref="ForecastCoupledContext.Investment"/>).
    /// </summary>
    public IReadOnlyDictionary<string, double> PerUnit { get; init; } = new Dictionary<string, double>();
}

/// <summary>Runs the perturbations.</summary>
public static class ProjectMarginalValue
{
    // Below this a difference of two values is rounding, not progress.
    private const double Negligible = 1e-9;

    /// <summary>The run of <paramref name="policy"/> over <paramref name="input"/> and the marginal value of each valued node of its project graph.</summary>
    public static ProjectMarginals Of(ColonyModelInput input, ColonyModelPolicy policy, ColonyValue valuation)
    {
        var cycles = input.Pawns.TryGetValue(out var pawns)
            ? pawns.Select(p => PawnCycle.Year(input, p).ToArray()).ToArray()
            : [];
        var baseline = ColonyModel.Run(input, cycles, policy);
        var value = valuation.Of(baseline);
        var findings = new SortedSet<string>(baseline.Findings.Concat(value.Findings), StringComparer.Ordinal);
        var perHour = new SortedDictionary<string, double>(StringComparer.Ordinal);
        var perUnit = new SortedDictionary<string, double>(StringComparer.Ordinal);
        if (value.Value.TryGetValue(out var before))
        {
            foreach (var node in policy.Projects.Nodes.Where(n => n.Kind is ProjectKind.Research or ProjectKind.Sow && n.Work > 0))
            {
                // One hour of one pawn at work speed 1: research points for research, work units for sowing.
                var hour = node.Kind == ProjectKind.Research ? ProjectGraph.ResearchPointsPerWorkTick * GameTime.TicksPerHour : GameTime.TicksPerHour;
                var run = ColonyModel.Run(input, cycles, policy with { Injected = new InjectedProgress(node.Id, hour) });
                if (!valuation.Of(run).Value.TryGetValue(out var after))
                {
                    continue;
                }

                var gain = Math.Abs(after - before) < Negligible ? 0 : after - before;
                perHour[node.Id] = gain;
                perUnit[ProjectGraph.ProgressKey(node)] = gain / (node.Kind == ProjectKind.Research ? hour : hour * node.Cells / node.Work);
            }
        }

        return new ProjectMarginals { Trajectory = baseline, Value = value.Value, Findings = EquatableArray.From(findings), PerHour = perHour, PerUnit = perUnit };
    }
}
