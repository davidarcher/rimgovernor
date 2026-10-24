namespace RimGovernor.Brain.Policy;

/// <summary>
/// The defs' map from a coupled task's binding to the work it is (#3825), from the catalog (DefinitionCatalog.WorkSpeedFacts): per
/// WorkGiverDef its WorkTypeDef, per JobDef the speed stat its driver's work toil applies (a job whose driver applies none is
/// absent), per RecipeDef its workSpeedStat ("" when it has none, so it works at speed 1) and the hauling work type.
/// </summary>
public sealed record WorkSpeedFacts
{
    public IReadOnlyDictionary<string, string> GiverWork { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, string> JobStat { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, string> RecipeStat { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    public string Hauling { get; init; } = "";
}

/// <summary>
/// One work type's part of the shortfall: the work of <see cref="WorkType"/>, run at <see cref="Stat"/> ("" for work no stat speeds,
/// such as hauling), the colony could not schedule, in ticks a day at speed 1 (a pawn at stat value v does it in TicksPerDay / v).
/// </summary>
public sealed record WorkShortfallRow(string WorkType, string Stat, double TicksPerDay);

/// <summary>
/// The work the tier-1 scheduler could not schedule (#3825, V1 of epic #3650): per work type and speed stat, in ticks a day at
/// speed 1 (<see cref="Rows"/>, none when nothing is short). #3805 and #3809 build on it; <see cref="WorkShortfallRules.Of"/> derives it.
/// </summary>
public sealed record WorkShortfall
{
    public EquatableArray<WorkShortfallRow> Rows { get; init; }

    /// <summary>The colony is short of nothing.</summary>
    public static readonly WorkShortfall None = new();
}

public static class WorkShortfallRules
{
    /// <summary>
    /// The work the selection left unscheduled (#3825). Every candidate root (alternatives and incumbents) is one outcome among those
    /// sharing its <see cref="ForecastCommitment.ExclusiveOutcome"/> (its own id without one); an outcome the selection serves with
    /// none of its roots is unserved, and costs its cheapest root's work: the activities of that root's completions and their
    /// prerequisites the plan does not allocate, each counted once over all outcomes. An activity is work by its binding: a
    /// delivery or supply is <see cref="WorkSpeedFacts.Hauling"/> at no stat, a recipe its giver's work type at its recipe's
    /// stat, other work its giver's work type at its job's stat; eating, sleeping, recreation, holding, approaching and practice are
    /// not work. Its work is the least over its options of the option's ticks (<see cref="ForecastCoupledContext.OptionWork"/>) times
    /// the option pawn's speed at the stat. The pawns' time the plan leaves idle over the horizon (the horizon less the union of their
    /// allocations' departure to end) first takes what they have an option on, cheapest option first; what remains is short. A
    /// work activity no option prices, a giver, job or recipe the defs do not map, or a pawn speed the read lacks throws
    /// <see cref="RequiredFactException"/>.
    /// </summary>
    public static WorkShortfall Of(ForecastCoupledCandidates candidates, ForecastCoupledSelection selection, WorkSpeedFacts work,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> speeds)
    {
        var context = candidates.Context;
        var horizon = selection.Horizon;
        DomainException.Require(double.IsFinite(horizon) && horizon > 0, "work shortfall horizon");
        var chosen = selection.Selection.Selected.ToHashSet(StringComparer.Ordinal);
        var allocations = selection.Selected.Plan.Allocations;
        var bound = context.Bindings.GroupBy(b => b.Key.Task, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(b => b.Key.Pawn, StringComparer.Ordinal).First().Value, StringComparer.Ordinal);
        // One piece of work under every task that does it: a delayed variant is its original's binding under a new id.
        string Identity(string activity) => bound.TryGetValue(activity, out var b)
            ? string.Join('|', b.Kind, b.Target, b.WorkGiver, b.Job, b.Recipe?.Bill ?? "", b.Location, b.Count) : activity;
        var allocated = allocations.Select(a => Identity(a.Activity)).ToHashSet(StringComparer.Ordinal);
        var activities = context.Activities.Activities.ToDictionary(a => a.Id, StringComparer.Ordinal);
        var options = context.Activities.Options.Where(o => context.OptionWork.ContainsKey((o.Activity, o.Pawn)))
            .GroupBy(o => o.Activity, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Select(o => o.Pawn).Distinct().ToArray(), StringComparer.Ordinal);
        var kinds = new Dictionary<string, (string WorkType, string Stat)?>(StringComparer.Ordinal);
        (string WorkType, string Stat)? Kind(string activity)
        {
            if (!kinds.TryGetValue(activity, out var kind))
                kinds[activity] = kind = bound.TryGetValue(activity, out var binding) ? KindOf(binding, work) : null;
            return kind;
        }
        double Speed(string pawn, string stat) =>
            stat.Length == 0 ? 1
                : speeds.TryGetValue(pawn, out var of) && of.TryGetValue(stat, out var speed) ? speed
                : throw new RequiredFactException("work speed " + stat + " of " + pawn);
        double UnitWork(string activity, string stat)
        {
            var least = double.PositiveInfinity;
            foreach (var pawn in options.GetValueOrDefault(activity) ?? [])
                least = Math.Min(least, context.OptionWork[(activity, pawn)] * Speed(pawn, stat));
            return double.IsFinite(least) ? least : throw new RequiredFactException("work of " + activity);
        }
        // The unscheduled work activities of a root: its completions and their prerequisites the plan does not allocate.
        IEnumerable<string> Unscheduled(ForecastCommitment root)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var stack = new Stack<string>(root.ActivityCompletions);
            while (stack.Count > 0)
            {
                var id = stack.Pop();
                if (!seen.Add(id) || !activities.TryGetValue(id, out var activity)) continue;
                foreach (var prerequisite in activity.Prerequisites) stack.Push(prerequisite);
                if (!allocated.Contains(Identity(id)) && Kind(id) is not null) yield return id;
            }
        }

        var roots = candidates.Alternatives.Concat(candidates.Incumbents).ToArray();
        var unscheduled = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var outcome in roots.GroupBy(r => r.ExclusiveOutcome.Length == 0 ? r.Id : r.ExclusiveOutcome, StringComparer.Ordinal))
        {
            if (outcome.Any(r => chosen.Contains(r.Id))) continue;
            var cheapest = outcome.Select(r => (Work: Unscheduled(r).ToArray(), r.Id))
                .Select(r => (r.Work, r.Id, Ticks: r.Work.Sum(a => UnitWork(a, Kind(a)!.Value.Stat))))
                .OrderBy(r => r.Ticks).ThenBy(r => r.Id, StringComparer.Ordinal).First();
            foreach (var activity in cheapest.Work) unscheduled.TryAdd(Identity(activity), activity);
        }

        if (unscheduled.Count == 0) return WorkShortfall.None;
        var idle = IdleTicks(context, allocations, horizon);
        var short_ = new SortedDictionary<(string WorkType, string Stat), double>();
        foreach (var activity in unscheduled.Values)
        {
            var kind = Kind(activity)!.Value;
            var left = 1.0;
            foreach (var pawn in (options.GetValueOrDefault(activity) ?? []).OrderBy(p => context.OptionWork[(activity, p)]).ThenBy(p => p, StringComparer.Ordinal))
            {
                var ticks = context.OptionWork[(activity, pawn)];
                if (left <= 0 || ticks <= 0 || idle.GetValueOrDefault(pawn) <= 0) continue;
                var take = Math.Min(left, idle[pawn] / ticks);
                idle[pawn] -= take * ticks;
                left -= take;
            }

            if (left > 0) short_[kind] = short_.GetValueOrDefault(kind) + (left * UnitWork(activity, kind.Stat));
        }

        var days = horizon / GameTime.TicksPerDay;
        return new WorkShortfall
        {
            Rows = EquatableArray.From(short_.Where(s => s.Value > 0).Select(s => new WorkShortfallRow(s.Key.WorkType, s.Key.Stat, s.Value / days))),
        };
    }

    /// <summary>
    /// The work a task is by its binding (the first pawn's: the generators bind every option of a task alike), null when it is not
    /// work. A giver, job or recipe the defs do not map throws.
    /// </summary>
    internal static (string WorkType, string Stat)? KindOf(Methods.ActivityTaskCandidate binding, WorkSpeedFacts work)
    {
        if (binding.Practice is not null) return null;
        switch (binding.Kind)
        {
            case PawnActivityKind.Eat or PawnActivityKind.Sleep or PawnActivityKind.Hold or PawnActivityKind.Recreation or PawnActivityKind.Approach:
                return null;
            case PawnActivityKind.Deliver or PawnActivityKind.Supply:
                return (work.Hauling.Length != 0 ? work.Hauling : throw new RequiredFactException("hauling work type"), "");
        }

        var type = work.GiverWork.TryGetValue(binding.WorkGiver, out var giver) ? giver : throw new RequiredFactException("work type of giver " + binding.WorkGiver);
        if (binding.Kind == PawnActivityKind.Recipe || binding.Recipe is not null)
        {
            var recipe = binding.Recipe?.Recipe ?? throw new RequiredFactException("recipe of " + binding.Id);
            return (type, work.RecipeStat.TryGetValue(recipe, out var stat) ? stat : throw new RequiredFactException("work speed stat of recipe " + recipe));
        }

        return (type, work.JobStat.TryGetValue(binding.Job, out var jobStat) ? jobStat : throw new RequiredFactException("work speed stat of job " + binding.Job));
    }

    // Per pawn the plan schedules, the horizon's ticks no allocation of it holds (its departure to end, overlaps once).
    private static Dictionary<string, double> IdleTicks(ForecastCoupledContext context, IReadOnlyList<ForecastActivityAllocation> allocations, double horizon)
    {
        var idle = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var pawn in context.Activities.Pawns.Select(p => p.Pawn).Distinct())
        {
            var busy = 0.0;
            var reach = 0.0;
            foreach (var (start, end) in allocations.Where(a => a.Pawn == pawn).Select(a => (Math.Max(0, a.Depart), Math.Min(horizon, a.End))).OrderBy(s => s.Item1))
            {
                var from = Math.Max(start, reach);
                if (end > from) { busy += end - from; reach = end; }
            }

            idle[pawn] = Math.Max(0, horizon - busy);
        }

        return idle;
    }
}
