namespace RimGovernor.Brain.Policy;

/// <summary>A divisible projected stock; unlike native Amount, forecast consumption can leave fractional units.</summary>
public sealed record ModelResourceStock(Resource Resource, double Count);

public enum ModelProjectStatus { Waiting, Done, Failed }

/// <summary>Held inputs remain in physical stock but are unavailable to other projects. Consumed inputs are already absent.</summary>
public enum ModelProjectInputs { Unpaid, Held, Consumed }

/// <summary>All mutable progress of one graph node, independent of the single-node marginal perturbation.</summary>
public sealed record ModelProjectState(string Id, double Remaining, ModelProjectStatus Status, ModelProjectInputs Inputs, bool GrowthInitialized);

/// <summary>
/// Disposable invocation-owned state on the review's time axis. Day is days after the review; EndDay is the common final
/// date on that axis, not a fresh duration. A carried state includes all events at or before Day (the observed origin is before
/// its day-zero events). Material stocks include held inputs, exclude consumed inputs. External holds exclude graph-owned holds.
/// Pawns retain the current skill mechanics and XP; initial need recovery belongs to the detailed/continuation bridge.
/// </summary>
public sealed record ColonyModelState
{
    public double Day { get; init; }
    public double EndDay { get; init; }
    public required double Nutrition { get; init; }
    /// <summary>Research produced since the original review, separate from each project's remaining work.</summary>
    public double ResearchPoints { get; init; }
    public required EquatableArray<ModelResourceStock> Materials { get; init; }
    public required EquatableArray<ModelResourceStock> Medicine { get; init; }
    public required EquatableArray<ModelResourceStock> ExternalHolds { get; init; }
    public required EquatableArray<ModelProjectState> Projects { get; init; }
    public required EquatableArray<ModelPawn> Pawns { get; init; }
    /// <summary>Completed treatment history on the original review axis; referenced by continuation, never persisted.</summary>
    public ColonyTrajectory? TreatmentHistory { get; init; }

    /// <summary>The ordinary observed-origin entry, shared by the production baseline and marginal perturbations.</summary>
    public static ColonyModelState Observed(ColonyModelInput input, ColonyModelPolicy policy) => new()
    {
        EndDay = policy.HorizonDays,
        Nutrition = input.Stocks.Nutrition,
        Materials = Stocks(input.Stocks.Materials),
        Medicine = Stocks(input.Stocks.Medicine),
        ExternalHolds = EquatableArray.From<ModelResourceStock>([]),
        Projects = EquatableArray.From(policy.Projects.Nodes.Select(n => new ModelProjectState(n.Id, n.Work, ModelProjectStatus.Waiting, ModelProjectInputs.Unpaid, false))),
        Pawns = input.Pawns,
    };

    private static EquatableArray<ModelResourceStock> Stocks(EquatableArray<Amount> amounts) =>
        EquatableArray.From(amounts.Select(a => new ModelResourceStock(a.Resource, a.Count)));

    internal string? Gap(ProjectGraph graph)
    {
        if (!double.IsFinite(Day) || !double.IsFinite(EndDay) || Day < 0 || EndDay <= 0 || Day > EndDay)
        {
            return "colony_model_time_invalid";
        }

        var holds = ExternalHolds;
        var projects = Projects;
        if (!double.IsFinite(Nutrition) || Nutrition < 0 || !double.IsFinite(ResearchPoints) || ResearchPoints < 0 || Materials.Concat(Medicine).Concat(holds).Any(a => !double.IsFinite(a.Count) || a.Count < 0))
        {
            return "colony_model_stock_invalid";
        }

        if (graph.Nodes.Select(n => n.Id).Distinct().Count() != graph.Nodes.Count || graph.Nodes.Any(n => n.Id.Length == 0))
        {
            return "colony_model_project_graph_invalid";
        }

        var byId = graph.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        if (projects.Count != byId.Count || projects.Select(p => p.Id).Distinct().Count() != projects.Count
            || projects.Any(p => !double.IsFinite(p.Remaining) || p.Remaining < 0 || !Enum.IsDefined(p.Status) || !Enum.IsDefined(p.Inputs)
                || p.Status == ModelProjectStatus.Done && p.Remaining != 0 || !byId.ContainsKey(p.Id)))
        {
            return "colony_model_project_state_invalid";
        }

        foreach (var project in projects)
        {
            if (project.Status == ModelProjectStatus.Done && byId[project.Id].Ingredients.Count > 0 && project.Inputs != ModelProjectInputs.Consumed)
            {
                return "colony_model_completed_inputs_invalid";
            }
        }

        var reserved = holds.Concat(projects.Where(p => p.Inputs == ModelProjectInputs.Held)
            .SelectMany(p => byId[p.Id].Ingredients.Select(a => new ModelResourceStock(a.Resource, a.Count)))).ToArray();
        // Duplicate census/hold rows are summed, never last-wins.
        var physical = Materials.GroupBy(a => a.Resource).ToDictionary(g => g.Key, g => g.Sum(a => a.Count));
        if (reserved.GroupBy(a => a.Resource).Any(g => g.Sum(a => a.Count) > physical.GetValueOrDefault(g.Key)))
        {
            return "colony_model_holds_exceed_stock";
        }

        return null;
    }
}
