namespace RimGovernor.Brain.Policy;

public static partial class GoalRules
{
    /// <summary>A goal's importance after its theme membership's override (#3503).</summary>
    public static Importance ImportanceOf(GoalRecord goal) => goal.ImportanceOverride ?? goal.Importance;

    /// <summary>A goal's deadline day: its override, else its own, else its theme's (#3503); null for none.</summary>
    public static int? DeadlineOf(GoalRecord goal, IEnumerable<ThemeRecord> themes) =>
        goal.DeadlineOverride ?? goal.DeadlineDay ?? themes.FirstOrDefault(t => t.Id == goal.Theme)?.DeadlineDay;

    /// <summary>
    /// The unmet goals the forecast can price progress on (#3503), in goal id order. Research: the goal's value is split
    /// equally over the projects it waits on and paid per point of the project's remaining cost, so finishing them earns
    /// the whole worth. Worn garment: one share per eligible colonist among the first <c>Count</c> not yet wearing it, on the
    /// requirement relief the clothing planner gives that colonist (<c>clothing/&lt;pawn&gt;/required/&lt;garment&gt;</c>). A goal
    /// with neither (stock, room, population, ideology and the rest) is valued by no ledger term yet.
    /// </summary>
    public static EquatableArray<ForecastGoalValue> ValuesOf(GoalsFacts f)
    {
        var values = new List<ForecastGoalValue>();
        foreach (var goal in f.Goals.OrderBy(g => g.Id))
        {
            var research = new List<ForecastGoalResearch>();
            var relief = new List<ForecastGoalRelief>();
            if (goal.Kind is GoalKind.ResearchProject or GoalKind.ResearchTier && f.Research.TryGetValue(out var census))
            {
                var open = ResearchOf(goal, f).Where(p => census.Details.TryGetValue(p, out var d) && d.Cost > 0).ToList();
                foreach (var project in open)
                {
                    var detail = census.Details[project];
                    var remaining = detail.Cost * (1 - Math.Clamp(detail.Progress.ValueOrDefault, 0, 1));
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
                    var wearing = gear.Pawns.Where(p => p.LoadoutModel.TryGetValue(out var model)
                        && model.Worn.Items.Any(o => o.Definition == want.Garment && (want.Stuff.Value == "" || o.Stuff == want.Stuff))).Select(p => p.Pawn).ToHashSet();
                    foreach (var pawn in EligibleWearers(gear, want.Garment).Take(goal.Count).Where(p => !wearing.Contains(p.Pawn)))
                    {
                        relief.Add(new(FormattableString.Invariant($"clothing/{pawn.Pawn.Value}/required/{goal.Target}"), 1.0 / missing));
                    }
                }
            }
            if (research.Count + relief.Count > 0)
            {
                values.Add(new(NeedOf(goal).Value, ImportanceOf(goal), DeadlineOf(goal, f.Themes)) { Research = EquatableArray.From(research), Relief = EquatableArray.From(relief) });
            }
        }
        return EquatableArray.From(values);
    }

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
