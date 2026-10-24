namespace RimGovernor.Brain.Policy;

/// <summary>Projects the same detailed work into the exact graph identities, retaining each crop's sow date.</summary>
internal static class ForecastHandoffProjects
{
    internal sealed record Result(ProjectGraph Graph, Dictionary<string, ModelProjectState> States, List<ForecastProjectDate> Completed);

    internal static Result Project(ForecastModelBasis basis, ForecastCoupledContext context, ForecastCoupledOutcome outcome,
        IReadOnlyDictionary<string, double> finished, double day, SortedSet<string> gaps)
    {
        var nodes = basis.Policy.Projects.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var states = ColonyModelState.Observed(basis.Input, basis.Policy).Projects.Value.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var dates = new List<ForecastProjectDate>();
        var tasks = context.Activities.Activities.ToDictionary(t => t.Id, StringComparer.Ordinal);
        var sown = new Dictionary<string, List<double>>(StringComparer.Ordinal);
        foreach (var allocation in outcome.Plan.Allocations.OrderBy(a => a.End).ThenBy(a => a.Activity, StringComparer.Ordinal))
        {
            if (!finished.ContainsKey(allocation.Activity) || !tasks.TryGetValue(allocation.Activity, out var task)) continue;
            var binding = context.Bindings.GetValueOrDefault((allocation.Activity, allocation.Pawn));
            ProjectNode? node = null;
            double work = 0;
            if (task.ProjectProgress is { } progress)
            {
                node = nodes.Values.SingleOrDefault(n => n.Kind is ProjectKind.Research or ProjectKind.Sow && ProjectGraph.ProgressKey(n) == progress.Project);
                if (node is null) { gaps.Add("terminal_project_unmapped:" + progress.Project); continue; }
                var units = allocation.Learning?.WorkAmount ?? allocation.WorkTicks * progress.PointsPerTick;
                work = node.Kind == ProjectKind.Sow ? units * node.Work / node.Cells : units;
                if (node.Kind == ProjectKind.Sow)
                {
                    if (!sown.TryGetValue(node.Id, out var cells)) sown[node.Id] = cells = [];
                    var count = Math.Min(node.Cells - cells.Count, (int)Math.Floor(units + 1e-8));
                    for (var cell = 1; cell <= count; cell++)
                    {
                        var tick = allocation.Learning is { } learning ? ForecastProductiveWorkPolicy.CompletionTick(learning, cell)
                            : allocation.Start + (allocation.End - allocation.Start) * cell / units;
                        if (tick is { } completion) cells.Add(completion / GameTime.TicksPerDay);
                        else gaps.Add("terminal_sow_completion_unknown:" + task.Id);
                    }
                }
            }
            else if (binding is not null)
            {
                if (binding.Recipe is { } recipe)
                    node = nodes.Values.FirstOrDefault(n => n.Kind == ProjectKind.Craft && n.Recipe == recipe.Recipe
                        && states[n.Id].Status != ModelProjectStatus.Done
                        && (recipe.Unfinished.Length > 0 || n.Ingredients.All(i => task.Inputs.Any(x => x.Resource == i.Resource)))
                        && n.Products.All(p => task.Outputs.Any(o => o.Resource == p.Resource))
                        && ((recipe.Stuff.Length == 0 && binding.ProductStuff.Length == 0)
                            || n.Ingredients.Any(i => i.Resource.Value == (recipe.Stuff.Length == 0 ? binding.ProductStuff : recipe.Stuff))));
                else if (binding.Job == "Harvest" && basis.Input.Land.TryGetValue(out var land))
                {
                    var field = land.FirstOrDefault(f => f.Cells.Contains(binding.Location));
                    node = field is null ? null : nodes.Values.FirstOrDefault(n => n.Kind == ProjectKind.Harvest && n.Field == field.Id);
                }
                // A detailed operation absent from the long-term graph changes physical stock only; never infer identity from overlapping resources.
                if (node is null) continue;
                var fraction = node.Kind == ProjectKind.Harvest ? 1d / Math.Max(1, node.Cells)
                    : node.Products.Select(p => task.Outputs.Where(o => o.Resource == p.Resource).Sum(o => o.Units) / p.Count).DefaultIfEmpty(1).Min();
                work = Math.Min(1, fraction) * states[node.Id].Remaining;
                var paid = binding.Recipe is { Unfinished.Length: > 0 }
                    ? EquatableArray.From(node.Ingredients.Select(i => new ForecastActivityYield(i.Resource, i.Count * Math.Min(1, fraction)))) : task.Inputs;
                nodes[node.Id] = node with { Ingredients = Subtract(node.Ingredients, paid), Products = Subtract(node.Products, task.Outputs),
                    Cells = node.Kind == ProjectKind.Harvest ? Math.Max(0, node.Cells - 1) : node.Cells };
                if (node.Kind == ProjectKind.Harvest)
                {
                    // A harvested native cell has already passed sowing and growth. Remove that cell's
                    // cycle from the remaining graph as well as its yield, so it is not planted and paid again.
                    foreach (var prior in nodes.Values.Where(n => n.Field == node.Field && n.Kind is ProjectKind.Sow or ProjectKind.Grow).ToArray())
                    {
                        var cells = Math.Max(0, prior.Cells - 1);
                        var left = prior.Kind == ProjectKind.Sow ? Math.Max(0, states[prior.Id].Remaining - prior.Work / Math.Max(1, prior.Cells)) : states[prior.Id].Remaining;
                        nodes[prior.Id] = prior with { Cells = cells, Work = prior.Kind == ProjectKind.Sow ? prior.Work * cells / Math.Max(1, prior.Cells) : prior.Work };
                        states[prior.Id] = states[prior.Id] with { Remaining = cells == 0 ? 0 : left,
                            Status = cells == 0 ? ModelProjectStatus.Done : states[prior.Id].Status };
                    }
                }
            }
            if (node is null) continue;
            var old = states[node.Id];
            var remaining = Math.Max(0, old.Remaining - work);
            if (remaining < 1e-8) remaining = 0;
            states[node.Id] = old with { Remaining = remaining, Status = remaining == 0 ? ModelProjectStatus.Done : ModelProjectStatus.Waiting,
                Inputs = remaining == 0 ? ModelProjectInputs.Consumed : old.Inputs };
            if (remaining == 0 && old.Status != ModelProjectStatus.Done)
                dates.Add(new(node.Id, (node.Kind == ProjectKind.Research ? ResearchCompletion(node, outcome.Plan, tasks, finished)
                    : allocation.Start + (allocation.End - allocation.Start) * Math.Min(1, old.Remaining / Math.Max(work, double.Epsilon))) / GameTime.TicksPerDay));
        }
        foreach (var pair in sown)
            Cohorts(basis.Input, nodes, states, dates, pair.Key, pair.Value, day, gaps);
        return new(new() { Nodes = EquatableArray.From(nodes.Values), Findings = basis.Policy.Projects.Findings }, states, dates);
    }

    private static EquatableArray<Amount> Subtract(EquatableArray<Amount> original, EquatableArray<ForecastActivityYield> used) =>
        EquatableArray.From(original.Select(p => p with { Count = Math.Max(0, p.Count - (long)Math.Round(used.Where(u => u.Resource == p.Resource).Sum(u => u.Units))) }).Where(p => p.Count > 0));

    private static double ResearchCompletion(ProjectNode node, ForecastActivityPlan plan, Dictionary<string, ForecastActivity> tasks,
        IReadOnlyDictionary<string, double> finished)
    {
        var rows = plan.Allocations.Where(a => finished.ContainsKey(a.Activity) && tasks[a.Activity].ProjectProgress?.Project == ProjectGraph.ProgressKey(node)).ToArray();
        double Produced(double tick) => rows.Sum(a => a.Learning is { } learning
            ? learning.WorkSegments.Sum(s => Math.Floor(Math.Max(0, Math.Min(tick, s.End) - s.Start) / s.CallbackInterval) * s.CallbackInterval * s.WorkPerTick)
            : Math.Clamp((tick - a.Start) / Math.Max(double.Epsilon, a.End - a.Start), 0, 1) * a.WorkTicks * tasks[a.Activity].ProjectProgress!.PointsPerTick);
        var low = (long)Math.Floor(rows.Min(a => a.Start)) - 1; var high = (long)Math.Ceiling(rows.Max(a => a.End));
        // Native callbacks are integer game ticks; monotone cumulative work includes simultaneous researchers once each.
        while (high - low > 1)
        {
            var middle = low + (high - low) / 2;
            if (Produced(middle) >= node.Work) high = middle; else low = middle;
        }
        return high;
    }

    private static void Cohorts(ColonyModelInput input, Dictionary<string, ProjectNode> nodes, Dictionary<string, ModelProjectState> states,
        List<ForecastProjectDate> dates, string sowId, List<double> sown, double day, SortedSet<string> gaps)
    {
        var sow = nodes[sowId];
        var grow = nodes.Values.SingleOrDefault(n => n.Kind == ProjectKind.Grow && n.Field == sow.Field);
        var harvest = nodes.Values.SingleOrDefault(n => n.Kind == ProjectKind.Harvest && n.Field == sow.Field);
        if (grow is null) return;
        var growIds = new List<string>(); var harvestIds = new List<string>();
        for (var i = 0; i <= sown.Count; i++)
        {
            var residual = i == sown.Count;
            var cells = residual ? sow.Cells - sown.Count : 1;
            if (cells <= 0) continue;
            var id = grow.Id + "/cohort/" + i;
            var growth = residual ? (Remaining: 0d, Mature: (double?)null, Known: true) : Growth(input, grow, sown[i], day);
            if (!growth.Known) gaps.Add("terminal_growth_unknown:" + id);
            var g = grow with { Id = id, Cells = cells, Prerequisites = residual ? [sowId] : [] };
            nodes[id] = g; growIds.Add(id);
            states[id] = new(id, growth.Remaining, growth.Mature is null ? ModelProjectStatus.Waiting : ModelProjectStatus.Done, ModelProjectInputs.Unpaid, !residual);
            if (growth.Mature is { } mature) dates.Add(new(id, mature));
            if (harvest is null) continue;
            var h = harvest with { Id = harvest.Id + "/cohort/" + i, Cells = cells, Work = harvest.Work * cells / Math.Max(1, harvest.Cells), Prerequisites = [id] };
            nodes[h.Id] = h; harvestIds.Add(h.Id);
            states[h.Id] = new(h.Id, h.Work, ModelProjectStatus.Waiting, ModelProjectInputs.Unpaid, false);
        }
        void Aggregate(ProjectNode node, List<string> prerequisites)
        {
            nodes[node.Id] = node with { Kind = ProjectKind.Milestone, Work = 0, Cells = 0, Prerequisites = EquatableArray.From(prerequisites), Products = [], Ingredients = [] };
            var done = prerequisites.All(id => states[id].Status == ModelProjectStatus.Done);
            states[node.Id] = new(node.Id, 0, done ? ModelProjectStatus.Done : ModelProjectStatus.Waiting, ModelProjectInputs.Consumed, true);
            if (done) dates.Add(new(node.Id, dates.Where(d => prerequisites.Contains(d.Project)).Max(d => d.Day)));
        }
        Aggregate(grow, growIds);
        if (harvest is not null) Aggregate(harvest, harvestIds);
    }

    private static (double Remaining, double? Mature, bool Known) Growth(ColonyModelInput input, ProjectNode node, double from, double until)
    {
        if (!input.Crops.TryGetValue(out var crops) || !input.Land.TryGetValue(out var land) || !input.Climate.TryGetValue(out var climate)) return (0, null, false);
        var crop = crops.FirstOrDefault(c => c.Name == node.Crop); var field = land.FirstOrDefault(f => f.Id == node.Field);
        if (crop is null || field is null || field.Fertility.Count == 0 || !crop.HarvestMinGrowth.TryGetValue(out var remaining)) return (0, null, false);
        var twelfth = climate.CurrentTwelfth; var boundary = Math.Max(1, climate.TicksToNextTwelfth) / (double)GameTime.TicksPerDay;
        while (boundary <= from) { twelfth = (twelfth + 1) % 12; boundary += ColonyModel.TwelfthDays; }
        while (from < until)
        {
            if (!CropGrowthModel.DailyGrowth(crop, field.Fertility.Average(), climate.Temperatures[twelfth]).TryGetValue(out var rate)) return (remaining, null, false);
            var end = Math.Min(until, boundary);
            if (rate > 0 && remaining <= (end - from) * rate) return (0, from + remaining / rate, true);
            remaining -= (end - from) * rate; from = end;
            if (from >= boundary) { twelfth = (twelfth + 1) % 12; boundary += ColonyModel.TwelfthDays; }
        }
        return (Math.Max(0, remaining), null, true);
    }
}
