namespace RimGovernor.Brain.Policy;

public static partial class GoalRules
{
    /// <summary>A goal's importance after its theme membership's override (#3503).</summary>
    public static Importance ImportanceOf(GoalRecord goal) => goal.ImportanceOverride.Or(goal.Importance);

    /// <summary>A goal's deadline: its override, else its own, else its theme's (#3503); open when none is set.</summary>
    public static GoalDeadline DeadlineOf(GoalRecord goal, IEnumerable<ThemeRecord> themes) =>
        goal.DeadlineOverride.Or(goal.DeadlineDay).Or(themes.FirstOrDefault(t => goal.Theme.Is(t.Id))?.DeadlineDay ?? GoalDeadline.Unbounded);

    /// <summary>
    /// The unmet goals the forecast can price progress on (#3503), in goal id order. Research: the goal's value is split
    /// equally over the projects it waits on and paid per point of the project's remaining cost, so finishing them earns
    /// the whole worth. Worn garment: one share per eligible colonist among the first <c>Count</c> not yet wearing it, paid when a
    /// selected commitment dresses that colonist in the garment (#3816). A goal with neither (stock, room, population, ideology
    /// and the rest) is valued by no ledger term yet.
    /// </summary>
    public static EquatableArray<ForecastGoalValue> ValuesOf(GoalsFacts f)
    {
        var values = new List<ForecastGoalValue>();
        foreach (var goal in f.Goals.OrderBy(g => g.Id))
        {
            var research = new List<ForecastGoalResearch>();
            var wearers = new List<ForecastGoalWearer>();
            if (goal.Kind is GoalKind.ResearchProject or GoalKind.ResearchTier && f.Research is { } census)
            {
                var open = ResearchOf(goal, f).Where(p => census.Details.TryGetValue(p, out var d) && d.Cost > 0).ToList();
                foreach (var project in open)
                {
                    var detail = census.Details[project];
                    var remaining = detail.Cost * (1 - Math.Clamp(detail.Progress, 0, 1));
                    if (remaining > 0)
                    {
                        research.Add(new(project, 1.0 / (open.Count * remaining)));
                    }
                }
            }
            else if (goal.Kind == GoalKind.WornCount && goal.Target != "" && goal.Count > 0 && f.Rounds.Gear.TryGetValue(out var gear))
            {
                var want = new WornGoal(new Resource(goal.Target), new Resource(goal.Qualifier), goal.Count);
                var missing = goal.Count - WearingCount(gear, want);
                if (missing > 0)
                {
                    var wearing = gear.Pawns.Where(p => p.Model is { } model
                        && model.Worn.Items.Any(o => o.Definition == want.Garment && (want.Stuff.Value == "" || o.Stuff == want.Stuff))).Select(p => p.Pawn).ToHashSet();
                    foreach (var pawn in EligibleWearers(gear, want.Garment).Take(goal.Count).Where(p => !wearing.Contains(p.Pawn)))
                    {
                        wearers.Add(new(pawn.Pawn.Value, goal.Target, want.Stuff.Value, 1.0 / missing));
                    }
                }
            }
            if (research.Count + wearers.Count > 0)
            {
                values.Add(new(NeedOf(goal).Value, ImportanceOf(goal), DeadlineOf(goal, f.Themes)) { Research = EquatableArray.From(research), Wearers = EquatableArray.From(wearers) });
            }
        }
        return EquatableArray.From(values);
    }

    /// <summary>
    /// <paramref name="values"/> with tier 2's marginal value for each goal it priced (#3505): such a goal is paid per unit of progress by
    /// what the progress advances its completion (<see cref="ForecastGoalValue.Units"/>) and not by its flat share; the rest are unchanged.
    /// </summary>
    public static EquatableArray<ForecastGoalValue> Priced(EquatableArray<ForecastGoalValue> values, IReadOnlyDictionary<string, GoalProjection> projections) =>
        EquatableArray.From(values.Select(v => projections.TryGetValue(v.Goal, out var p) && p.Priced
            ? v with { Priced = true, Units = EquatableArray.From(p.PerUnit.Select(u => new ForecastGoalUnit(u.Key, u.Value))) }
            : v));

    /// <summary>The colonists still to wear a <c>WornCount</c> goal's garment; null while the gear census is unread.</summary>
    internal static int? WornMissing(GoalRecord goal, GoalsFacts f) =>
        f.Rounds.Gear.TryGetValue(out var gear) ? goal.Count - WearingCount(gear, new WornGoal(new Resource(goal.Target), new Resource(goal.Qualifier), goal.Count)) : null;

    /// <summary>The floor each goal's routes all break, by goal need id, from a coupled selection's <c>goal_infeasible</c> findings (#3503).</summary>
    public static IReadOnlyDictionary<string, string> InfeasibleOf(ForecastCoupledSelection selection)
    {
        var output = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var finding in selection.Selection.Findings.Where(x => x.StartsWith(ForecastCoupledSelection.GoalInfeasiblePrefix, StringComparison.Ordinal)))
        {
            var body = finding[ForecastCoupledSelection.GoalInfeasiblePrefix.Length..];
            var split = body.LastIndexOf(':');
            if (split > 0)
            {
                output[body[..split]] = body[(split + 1)..];
            }
        }
        return output;
    }
}
