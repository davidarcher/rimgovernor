namespace RimGovernor.Brain.Policy;

// The marginal value of project progress (#3531 of #3470), by perturbation: the model is run as it is, then once per valued
// project (research and sowing, the work tier 1 can offer) with one hour of progress injected at time zero; the difference of the
// two trajectories' values is what an hour of that work is worth. A project whose completion falls outside the horizon, or whose
// crop cannot mature, changes nothing and so is worth nothing. Labor allocation is a fixed order, so there is no search.
//
// The same runs price the player's goals (#3505): a goal is a terminal of the graph (GoalPaths), its completion day is read off each
// trajectory, and an hour of work is worth the goal's value at the day it completes less its value at the day it would have.
//
// Skills and medicine (#3634, #3344): each pawn's stock of each skill is run again one stored level higher, its XP toward the next level
// kept; the value of that level divided by the native XP it takes is the value of one XP. A stock at the top level, or one the value
// does not read (ITrajectoryValue.Reads, #3664), is worth nothing more by XP and is not run. Each medicine kind held is run again
// with one more unit. Each knowledge category (#3843) is priced by finishing the project its points fund (ProjectGraph.KnowledgeProject).

/// <summary>What a tier-2 trajectory is worth (<see cref="ColonyValue"/>; a test may price it otherwise).</summary>
public interface ITrajectoryValue
{
    ColonyValueResult Price(ColonyTrajectory trajectory);

    /// <summary>A goal's worth at its modeled completion time.</summary>
    double GoalValue(GoalTerminal goal, ModelEventTime completionDay);

    /// <summary>
    /// Whether <see cref="Price"/> reads the stock's level (#3664). A stock no price reads is worth 0 per XP by construction, so
    /// <see cref="ProjectMarginalValue.Of"/> writes 0 for it without a run.
    /// </summary>
    bool Reads(SkillTrack track);
}

/// <summary>What a raise of a skill stock by one stored level is (#3912): the XP the level takes and the days it lasts inside the horizon.</summary>
public sealed record SkillRaise(double Xp, double Days);

/// <summary>The tier-2 run of a review and what an hour of each valued project is worth in it.</summary>
public sealed record ProjectMarginals
{
    /// <summary>The run as it is.</summary>
    public ColonyTrajectory Trajectory { get; init; } = new();

    /// <summary>The run's value in capability-days; missing required pricing facts fail the computation.</summary>
    public double Value { get; init; }

    /// <summary>The run's gaps, its project graph's and the value's unassessed parts, sorted.</summary>
    public EquatableArray<string> Findings { get; init; }

    /// <summary>Capability-days one hour of the project's work adds (dV/dt), by node id.</summary>
    public IReadOnlyDictionary<string, double> PerHour { get; init; } = new Dictionary<string, double>();

    /// <summary>
    /// Capability-days a unit of the project's progress adds, by <see cref="ProjectGraph.ProgressKey"/>: per research point, per
    /// cell sown; per XP of a skill stock (<see cref="ProjectGraph.SkillKey"/>) and per unit of a medicine kind
    /// (<see cref="ProjectGraph.MedicineKey"/>); per knowledge point of each knowledge category (<see cref="ProjectGraph.KnowledgeKey"/>, every
    /// category the colony has, zero for one that funds no project). This is what tier 1 multiplies the progress of a task by (<see cref="ForecastCoupledContext.Investment"/>).
    /// </summary>
    public IReadOnlyDictionary<string, double> PerUnit { get; init; } = new Dictionary<string, double>();

    /// <summary>
    /// For each skill stock (<see cref="ProjectGraph.SkillKey"/>), the XP its next level takes and the days of the horizon a raise of one
    /// stored level lasts before decay takes it back (<see cref="SkillStock.RaisedLasts"/>, #3912); a stock at the top level has
    /// <see cref="SkillRaise.Xp"/> 0 and no days: there is no level to raise.
    /// </summary>
    public IReadOnlyDictionary<string, SkillRaise> SkillRaises { get; init; } = new Dictionary<string, SkillRaise>();

    /// <summary>What the run says about each goal given to <see cref="ProjectMarginalValue.Of"/>, by goal need id.</summary>
    public IReadOnlyDictionary<string, GoalProjection> Goals { get; init; } = new Dictionary<string, GoalProjection>();
}

/// <summary>Runs the perturbations.</summary>
public static class ProjectMarginalValue
{
    // Below this a difference of two values is rounding, not progress.
    private const double Negligible = 1e-9;

    /// <summary>
    /// The run of <paramref name="policy"/> over <paramref name="input"/>, the marginal value of each valued node of its project graph
    /// and, for each of <paramref name="goals"/>, its completion day and what a unit of each node's progress adds to it.
    /// </summary>
    public static ProjectMarginals Of(ColonyModelInput input, ColonyModelPolicy policy, ITrajectoryValue valuation, IReadOnlyList<GoalTerminal>? goals = null)
    {
        goals ??= [];
        IReadOnlyList<ModelPawn> roster = input.Pawns;
        var cycles = roster.Select(p => PawnCycle.Year(input, p).ToArray()).ToArray();
        var start = ColonyModelState.Observed(input, policy);
        var baseline = ColonyModel.Run(input, start, cycles, policy);
        var value = valuation.Price(baseline);
        var findings = new SortedSet<string>(baseline.Findings.Concat(value.Findings).Concat(policy.Projects.Findings), StringComparer.Ordinal);
        var perHour = new SortedDictionary<string, double>(StringComparer.Ordinal);
        var perUnit = new SortedDictionary<string, double>(StringComparer.Ordinal);
        var before = goals.ToDictionary(g => g.Goal, g => CompletionDay(baseline, g), StringComparer.Ordinal);
        var goalUnits = goals.ToDictionary(g => g.Goal, _ => new SortedDictionary<string, double>(StringComparer.Ordinal), StringComparer.Ordinal);
        var completes = goals.ToDictionary(g => g.Goal, g => before[g.Goal] is ModelEventTime.AtDay, StringComparer.Ordinal);
        foreach (var node in policy.Projects.Nodes.Where(n => n.Kind is ProjectKind.Research or ProjectKind.Sow && n.Work > 0))
        {
            // One hour of one pawn at work speed 1: research points for research, work units for sowing.
            var hour = node.Kind == ProjectKind.Research ? ProjectGraph.ResearchPointsPerWorkTick * GameTime.TicksPerHour : GameTime.TicksPerHour;
            var units = node.Kind == ProjectKind.Research ? hour : hour * node.Cells / node.Work;
            var run = ColonyModel.Run(input, start, cycles, policy with { Injected = new InjectedProgress(node.Id, hour) });
            var baseValue = value.Value;
            var after = valuation.Price(run).Value;
            {
                var gain = Math.Abs(after - baseValue) < Negligible ? 0 : after - baseValue;
                perHour[node.Id] = gain;
                perUnit[ProjectGraph.ProgressKey(node)] = gain / units;
            }

            foreach (var goal in goals)
            {
                var day = CompletionDay(run, goal);
                completes[goal.Goal] |= day is ModelEventTime.AtDay;
                var gain = valuation.GoalValue(goal, day) - valuation.GoalValue(goal, before[goal.Goal]);
                goalUnits[goal.Goal][ProjectGraph.ProgressKey(node)] = (Math.Abs(gain) < Negligible ? 0 : gain) / units;
            }
        }

        var asIs = value.Value;
        // The value a unit of <amount> injected at time zero on the node or stock <node> adds.
        double Gain(string node, double amount)
        {
            var after = valuation.Price(ColonyModel.Run(input, start, cycles, policy with { Injected = new InjectedProgress(node, amount) })).Value;
            return (Math.Abs(after - asIs) < Negligible ? 0 : after - asIs) / amount;
        }

        // A stock the value cannot price (ColonyValueResult.Unpriced) is left out with its findings: its marginal is not a number.
        void Perturb(string key, double amount)
        {
            if (!value.Unpriced.Contains(key))
            {
                perUnit[key] = Gain(key, amount);
            }
        }

        // Each pawn's stock of each skill the value reads one level higher, over the XP that level takes; a stock at the top level
        // gains nothing by XP, nor does a priced stock the value does not read (#3664: its run would change nothing).
        foreach (var track in baseline.Skills)
        {
            if (track.NextLevel is not SkillNextLevel.Takes { Xp: var levelXp })
            {
                perUnit[track.Key] = 0;
            }
            else if (valuation.Reads(track))
            {
                Perturb(track.Key, levelXp);
            }
            else if (!value.Unpriced.Contains(track.Key))
            {
                perUnit[track.Key] = 0;
            }
        }

        foreach (var kind in baseline.MedicineKinds)
        {
            Perturb(ProjectGraph.MedicineKey(kind), 1);
        }

        // Each knowledge category's point (#3843) funds its node (ProjectGraph.Funding). Study is not in the run, so that node completes
        // only once all its points are given: it is run again with them given now, and a point is worth its share of what finishing the
        // project now adds. A category that funds no project, or one whose points are all given, is worth nothing a point. Two
        // categories funding one project (Advanced overflowing into Basic's) share its run.
        var perPoint = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var funding in policy.Projects.Funding)
        {
            var key = ProjectGraph.KnowledgeKey(funding.Category);
            if (policy.Projects.Nodes.FirstOrDefault(n => n.Kind == ProjectKind.Knowledge && n.Id == funding.Node) is not { Work: > 0 } node)
            {
                perUnit[key] = 0;
                continue;
            }

            if (!perPoint.TryGetValue(node.Id, out var point))
            {
                perPoint[node.Id] = point = Gain(node.Id, node.Work);
            }

            perUnit[key] = point;
        }

        // A goal no run completes inside the horizon cannot be priced by the model: it keeps its flat value.
        var projections = goals.ToDictionary(g => g.Goal, g => new GoalProjection { CompletionDay = before[g.Goal], PerUnit = goalUnits[g.Goal], Priced = completes[g.Goal] }, StringComparer.Ordinal);
        return new ProjectMarginals
        {
            Trajectory = baseline, Value = value.Value, Findings = EquatableArray.From(findings), PerHour = perHour, PerUnit = perUnit, Goals = projections,
            SkillRaises = SkillRaisesOf(roster, policy.HorizonDays),
        };
    }

    // Each skill stock of the roster: the native XP its next level takes and the days of the horizon that level lasts (#3912).
    private static SortedDictionary<string, SkillRaise> SkillRaisesOf(IReadOnlyList<ModelPawn> roster, double horizonDays)
    {
        var raises = new SortedDictionary<string, SkillRaise>(StringComparer.Ordinal);
        foreach (var stock in roster.SelectMany(p => SkillStock.Of(p, new SortedSet<string>(StringComparer.Ordinal))))
        {
            raises[stock.Key] = stock.Track().NextLevel is SkillNextLevel.Takes { Xp: var xp } ? new SkillRaise(xp, stock.RaisedLasts(horizonDays)) : new SkillRaise(0, 0);
        }

        return raises;
    }

    /// <summary>When the last node of the goal completes in this run, or NotReached.</summary>
    private static ModelEventTime CompletionDay(ColonyTrajectory trajectory, GoalTerminal goal)
    {
        var last = 0.0;
        foreach (var node in goal.Nodes)
        {
            if (trajectory.Events.FirstOrDefault(e => e.Id == node && e.Kind is ModelEventKind.ProjectCompletion or ModelEventKind.Harvest) is not { } done)
            {
                return new ModelEventTime.NotReached();
            }

            last = Math.Max(last, done.Day);
        }

        return new ModelEventTime.AtDay(last);
    }
}
