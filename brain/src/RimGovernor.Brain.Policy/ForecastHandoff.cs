namespace RimGovernor.Brain.Policy;

/// <summary>The immutable review basis. A handoff never substitutes a new census or a new final date.</summary>
public sealed record ForecastModelBasis(ColonyModelInput Input, ColonyModelPolicy Policy);
public sealed record ForecastProjectDate(string Project, double Day);
public sealed record ForecastTerminalNeed(string Pawn, string Kind, Fact<double> Level);
/// <summary>Compact terminal evidence from one evaluated schedule; no trajectory or durable ownership.</summary>
public sealed record ForecastHandoff(ColonyModelState State, EquatableArray<ForecastTerminalNeed> Needs,
    EquatableArray<ForecastProjectDate> Completed, EquatableArray<string> Gaps);

/// <summary>The same evaluation's compact accounting. Expensive model/skill projection is deferred until selection.</summary>
public sealed class ForecastTerminalAccounting
{
    internal ForecastTerminalAccounting(ColonyForecastRequest request, IReadOnlyDictionary<string, ForecastCommitment> commitments,
        IReadOnlyDictionary<Resource, Fact<double>> stocks, IReadOnlyDictionary<string, double> finished,
        ForecastCoupledMaterials.MaterialHolds? holds)
    { Request = request; Commitments = commitments; Stocks = stocks; Finished = finished; Holds = holds; }
    internal ColonyForecastRequest Request { get; }
    internal IReadOnlyDictionary<string, ForecastCommitment> Commitments { get; }
    internal IReadOnlyDictionary<Resource, Fact<double>> Stocks { get; }
    internal IReadOnlyDictionary<string, double> Finished { get; }
    internal ForecastCoupledMaterials.MaterialHolds? Holds { get; }
}

public static class ForecastHandoffPolicy
{
    /// <summary>Project a selected outcome (or bounded finalist), without reevaluating its schedule or reading another census.</summary>
    public static ForecastHandoff? Project(ForecastCoupledOutcome outcome) => outcome.Terminal is { } t
        ? Project(t.Request, t.Commitments, outcome, t.Stocks, t.Finished, t.Holds) : null;

    internal static ForecastHandoff Project(ColonyForecastRequest request, IReadOnlyDictionary<string, ForecastCommitment> commitments,
        ForecastCoupledOutcome outcome, IReadOnlyDictionary<Resource, Fact<double>> physical,
        IReadOnlyDictionary<string, double> finished, ForecastCoupledMaterials.MaterialHolds? holds)
    {
        var context = request.Coupled!;
        var basis = context.ModelBasis!;
        var start = ColonyModelState.Observed(basis.Input, basis.Policy);
        var gaps = new SortedSet<string>(StringComparer.Ordinal);
        var day = request.HorizonDays;
        var dates = new List<ForecastProjectDate>();
        var needs = outcome.Needs.SelectMany(p => p.Value.Needs.Select(n => new ForecastTerminalNeed(p.Key, n.Kind, n.FinalLevel))).ToArray();
        foreach (var n in needs.Where(n => !n.Level.IsKnown)) gaps.Add($"terminal_need_unknown:{n.Pawn}:{n.Kind}");
        if (basis.Policy.Scheduled.Any(e => e.Day <= day)) gaps.Add("terminal_scheduled_effect_unrepresented");
        foreach (var pawn in basis.Input.Pawns.IsKnown ? basis.Input.Pawns.Value : EquatableArray.From<ModelPawn>([]))
            foreach (var need in pawn.Needs)
                if (!needs.Any(n => n.Pawn == pawn.Pawn && n.Kind == need.Kind)) gaps.Add($"terminal_need_missing:{pawn.Pawn}:{need.Kind}");
        if (outcome.Feasible != Known(true)) gaps.Add("terminal_schedule_not_known_feasible");
        foreach (var c in commitments.Values)
        {
            foreach (var unsupported in c.UnsupportedEffects) gaps.Add("terminal_unsupported:" + unsupported);
            if (c.OutfitChanges.Count + c.ComfortShifts.Count > 0) gaps.Add("terminal_outfit_or_comfort_effect_unrepresented");
        }
        if (outcome.ClosedRooms.Count + outcome.PlannedRooms.Count > 0 || outcome.Shelters.Any(s => s.CompleteAt.IsKnown))
            gaps.Add("terminal_shelter_effect_unrepresented");
        foreach (var f in outcome.Findings.Where(f => f.Reason.Contains("unassessed", StringComparison.Ordinal)
            || f.Reason.StartsWith("unknown_", StringComparison.Ordinal) || f.Reason.StartsWith("unbound_", StringComparison.Ordinal)))
            gaps.Add("terminal_evaluation:" + f.Reason);

        var stocks = physical.Where(p => p.Value.IsKnown).Select(p => new ModelResourceStock(p.Key, p.Value.Value)).ToArray();
        var stockKnown = request.Facts.Stock.IsKnown && physical.All(p => p.Value.IsKnown && p.Value.Value >= 0);
        if (!SameStocks(request.Facts.Stock, basis.Input.Stocks.Materials))
        { stockKnown = false; gaps.Add("terminal_material_census_scope_mismatch"); }
        if (!stockKnown) gaps.Add("terminal_stock_unknown");
        var nutrition = Nutrition(request, basis.Input, physical, gaps);
        foreach (var consumer in request.Facts.Food.Consumers.Where(c => !outcome.Needs.ContainsKey(c.Id.Value)))
            if (consumer.NutritionPerDay != Known(0d)) gaps.Add("terminal_food_consumer_unmodelled:" + consumer.Id);
        Fact<EquatableArray<ModelResourceStock>> medicine = default;
        if (start.Medicine.TryGetValue(out var originalMedicine))
        {
            var result = new List<ModelResourceStock>();
            foreach (var m in originalMedicine)
            {
                var observed = request.Facts.Stock.IsKnown ? request.Facts.Stock.Value.Where(s => s.Resource == m.Resource).Sum(s => (double)s.Count) : double.NaN;
                if (observed != m.Count) { gaps.Add("terminal_medicine_census_scope_mismatch:" + m.Resource); break; }
                if (!physical.GetValueOrDefault(m.Resource, Known(0.0)).TryGetValue(out var count)) break;
                result.Add(new(m.Resource, count));
            }
            if (result.Count == originalMedicine.Count) medicine = Known(EquatableArray.From(result));
        }
        if (!medicine.IsKnown) gaps.Add("terminal_medicine_unknown");

        var projects = start.Projects.Value.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var nodes = basis.Policy.Projects.Nodes;
        var tasks = context.Activities.Activities.ToDictionary(t => t.Id, StringComparer.Ordinal);
        foreach (var allocation in outcome.Plan.Allocations.OrderBy(a => a.End).ThenBy(a => a.Activity, StringComparer.Ordinal))
        {
            if (!finished.ContainsKey(allocation.Activity) || !tasks.TryGetValue(allocation.Activity, out var task)) continue;
            if (task.ProjectProgress is not { } progress) continue;
            var node = nodes.SingleOrDefault(n => n.Kind is ProjectKind.Research or ProjectKind.Sow && ProjectGraph.ProgressKey(n) == progress.Project);
            if (node is null) { gaps.Add("terminal_project_unmapped:" + progress.Project); continue; }
            var old = projects[node.Id];
            var perUnit = node.Kind == ProjectKind.Sow ? node.Work / node.Cells : 1;
            var work = (allocation.Learning?.WorkAmount ?? allocation.WorkTicks * progress.PointsPerTick) * perUnit;
            var remaining = Math.Max(0, old.Remaining - work);
            projects[node.Id] = old with { Remaining = remaining, Status = remaining == 0 ? ModelProjectStatus.Done : ModelProjectStatus.Waiting };
            if (remaining == 0 && old.Status != ModelProjectStatus.Done)
            {
                // Continuous research crosses its threshold during work; sow tasks complete indivisible cells at their end.
                var tick = node.Kind == ProjectKind.Sow ? allocation.End : ResearchCompletion(node, outcome.Plan, tasks, finished);
                dates.Add(new(node.Id, tick / GameTime.TicksPerDay));
            }

        }
        foreach (var sow in nodes.Where(n => n.Kind == ProjectKind.Sow && projects[n.Id].Remaining > 0 && projects[n.Id].Remaining < n.Work))
            gaps.Add("terminal_partial_field_growth_unrepresented:" + sow.Id);
        foreach (var grow in nodes.Where(n => n.Kind == ProjectKind.Grow))
        {
            var sow = nodes.SingleOrDefault(n => n.Kind == ProjectKind.Sow && n.Field == grow.Field);
            var completed = sow is null ? null : dates.FirstOrDefault(d => d.Project == sow.Id);
            if (completed is null) continue;
            if (!Growth(basis.Input, grow, completed.Day, day).TryGetValue(out var remaining))
            { gaps.Add("terminal_growth_unknown:" + grow.Id); continue; }
            projects[grow.Id] = projects[grow.Id] with { Remaining = remaining, GrowthInitialized = true,
                Status = remaining == 0 ? ModelProjectStatus.Done : ModelProjectStatus.Waiting };
            if (remaining == 0) gaps.Add("terminal_mature_crop_completion_time_unrepresented:" + grow.Id);
        }
        foreach (var output in outcome.Plan.Allocations.Where(a => finished.ContainsKey(a.Activity)).SelectMany(a => tasks[a.Activity].Outputs))
        {
            if (request.Facts.Items.Nutrition.TryGetValue(output.Resource, out var edible) && edible > 0)
            {
                if (!request.Facts.Food.Stocks.Any(s => s.DefName == output.Resource)) gaps.Add("terminal_food_output_scope_unrepresented:" + output.Resource);
                if (output.Perishable && !output.FreshTicks.IsKnown) gaps.Add("terminal_output_expiry_unknown:" + output.Resource);
            }
            if (request.Facts.Items.MedicalPotency.ContainsKey(output.Resource)
                && (!start.Medicine.IsKnown || !start.Medicine.Value.Any(m => m.Resource == output.Resource)))
                gaps.Add("terminal_medicine_output_scope_unrepresented:" + output.Resource);
        }        // Detailed work has no graph-node identity for craft/build/hunt yet. Never repeat its paid inputs or grant its payoff.
        if (outcome.Plan.Allocations.Any(a => finished.ContainsKey(a.Activity) && tasks.TryGetValue(a.Activity, out var t)
            && t.ProjectProgress is null && (t.Inputs.Count > 0 || t.Outputs.Count > 0)
            && nodes.Any(n => n.Kind is ProjectKind.Craft or ProjectKind.Construction or ProjectKind.Hunt or ProjectKind.Harvest
                && (n.Ingredients.Any(i => t.Inputs.Any(x => x.Resource == i.Resource))
                    || n.Products.Any(p => t.Outputs.Any(o => o.Resource == p.Resource))
                    || n.Kind is ProjectKind.Hunt or ProjectKind.Harvest && t.Outputs.Count > 0))))
            gaps.Add("terminal_paid_project_identity_unmapped");

        Fact<EquatableArray<ModelResourceStock>> external = default;
        var budget = ForecastCoupledMaterials.CoupledMaterialBudgetOf(request, holds, tasks, outcome.Plan);
        if (holds is not null && budget.TryGetValue(out var available) && request.Facts.Stock.TryGetValue(out var initial))
        {
            var reserved = new List<ModelResourceStock>();
            foreach (var resource in initial.Select(s => s.Resource).Concat(available.Keys).Distinct())
            {
                if (!available.GetValueOrDefault(resource, Known(0.0)).TryGetValue(out var free))
                { gaps.Add("terminal_hold_unknown:" + resource); continue; }
                var count = Math.Max(0, initial.Where(s => s.Resource == resource).Sum(s => (double)s.Count) - free);
                // Deliveries pay the shared construction demand; unrelated reservations remain protected.
                var delivered = outcome.Plan.Allocations.Where(a => finished.ContainsKey(a.Activity)
                    && context.Bindings.TryGetValue((a.Activity, a.Pawn), out var b) && b.Kind == PawnActivityKind.Deliver)
                    .SelectMany(a => tasks[a.Activity].Inputs).Where(i => i.Resource == resource && i.Units.IsKnown).Sum(i => i.Units.Value);
                count -= Math.Min(delivered, holds.Owed.GetValueOrDefault(resource));
                if (count > 0) reserved.Add(new(resource, count));
            }
            if (!gaps.Any(g => g.StartsWith("terminal_hold_unknown:", StringComparison.Ordinal))) external = Known(EquatableArray.From(reserved));
        }
        if (!external.IsKnown) gaps.Add("terminal_holds_unknown");
        else if (nodes.Any(n => n.Ingredients.Any(i => external.Value.Any(h => h.Resource == i.Resource && h.Count > 0))))
            gaps.Add("terminal_graph_hold_identity_unmapped");
        var pawns = Pawns(start.Pawns, context.PracticeTasks, outcome, finished, day, gaps);
        var state = start with { Day = day, Nutrition = nutrition, Materials = stockKnown ? Known(EquatableArray.From(stocks)) : default,
            Medicine = medicine, ExternalHolds = external, Projects = Known(EquatableArray.From(nodes.Select(n => projects[n.Id]))), Pawns = pawns };
        if (state.Gap(basis.Policy.Projects) is { } gap) gaps.Add(gap);
        return new(state, EquatableArray.From(needs), EquatableArray.From(dates), EquatableArray.From(gaps));
    }

    private static bool SameStocks(Fact<EquatableArray<Amount>> a, Fact<EquatableArray<Amount>> b) => a.IsKnown && b.IsKnown
        && a.Value.Concat(b.Value).Select(s => s.Resource).Distinct().All(r => a.Value.Where(s => s.Resource == r).Sum(s => s.Count) == b.Value.Where(s => s.Resource == r).Sum(s => s.Count));

    private static Fact<double> Nutrition(ColonyForecastRequest request, ColonyModelInput input,
        IReadOnlyDictionary<Resource, Fact<double>> terminal, SortedSet<string> gaps)
    {
        var context = request.Coupled!;
        if (!request.Facts.Stock.IsKnown || !input.Stocks.Nutrition.IsKnown) { gaps.Add("terminal_nutrition_unknown"); return default; }
        double initial = 0, final = 0;
        if (request.Facts.Food.Complete != Known(true)) { gaps.Add("terminal_food_census_unknown"); return default; }
        foreach (var row in request.Facts.Stock.Value.GroupBy(s => s.Resource))
        {
            if (!request.Facts.Items.Nutrition.TryGetValue(row.Key, out var perUnit) || perUnit <= 0) continue;
            var count = row.Sum(s => (double)s.Count);
            var food = request.Facts.Food.Stocks.Where(s => s.DefName == row.Key).ToArray();
            if (food.Any(s => !s.Count.IsKnown) || food.Sum(s => (double)s.Count.Value) != count)
            { gaps.Add("terminal_food_census_scope_mismatch:" + row.Key); return default; }
            initial += count * perUnit;
            var lots = context.InitialLots.Where(l => l.Resource == row.Key).ToArray();
            if (lots.Any(l => !l.Count.IsKnown || l.Perishable && !l.FreshTicks.IsKnown) || Math.Abs(lots.Sum(l => l.Count.Value) - count) > 1e-8)
            { gaps.Add("terminal_food_lot_scope_mismatch:" + row.Key); return default; }
        }
        if (Math.Abs(initial - input.Stocks.Nutrition.Value) > 1e-8)
        { gaps.Add("terminal_food_census_scope_mismatch"); return default; }
        foreach (var row in terminal)
        {
            if (!request.Facts.Items.Nutrition.TryGetValue(row.Key, out var perUnit) || perUnit <= 0) continue;
            if (!row.Value.IsKnown) { gaps.Add("terminal_nutrition_unknown"); return default; }
            final += row.Value.Value * perUnit;
        }
        return Known(final);
    }

    private static Fact<EquatableArray<ModelPawn>> Pawns(Fact<EquatableArray<ModelPawn>> original, EquatableArray<ForecastPracticeTask> practice, ForecastCoupledOutcome outcome,
        IReadOnlyDictionary<string, double> finished, double day, SortedSet<string> gaps)
    {
        if (!original.TryGetValue(out var pawns)) { gaps.Add("terminal_pawns_unknown"); return default; }
        return Known(EquatableArray.From(pawns.Select(p =>
        {
            if (!p.Skills.TryGetValue(out var skills)) { gaps.Add("terminal_skills_unknown:" + p.Pawn); return p; }
            var projected = skills.Select(s =>
            {
                var events = outcome.Plan.Allocations.Where(a => a.Pawn == p.Pawn && finished.ContainsKey(a.Activity) && a.Learning?.Skill == s.Name)
                    .SelectMany(a => a.Learning!.Callbacks).ToList();
                foreach (var task in practice.Where(t => t.Worker.Pawn == p.Pawn && t.Worker.Skill == s.Name))
                {
                    var allocation = outcome.Plan.Allocations.FirstOrDefault(a => a.Activity == task.Task && finished.ContainsKey(a.Activity));
                    if (allocation is null) continue;
                    events.AddRange(ForecastMaintenancePolicy.PracticeCallbacks(task, allocation, (int)Math.Ceiling(day * GameTime.TicksPerDay)));
                    gaps.Add("terminal_practice_injury_recovery_unassessed:" + task.Task);
                    if (!task.Worker.CallbackIntervalTicks.IsKnown || !task.Worker.RawXpPerCallback.IsKnown)
                        gaps.Add("terminal_practice_callbacks_unknown:" + task.Task);
                }
                var result = ForecastSkillPolicy.ForecastTerminalSkill(s.Learning, events.OrderBy(e => e.Tick).ThenBy(e => e.Id, StringComparer.Ordinal).ToArray(), (int)Math.Ceiling(day * GameTime.TicksPerDay));
                if (!result.StoredLevel.IsKnown || !result.Level.IsKnown || !result.Xp.IsKnown)
                { gaps.Add($"terminal_skill_unknown:{p.Pawn}:{s.Name}"); return s with { Learning = null }; }
                return s with { Level = result.Level.Value, Stored = result.StoredLevel.Value,
                    Learning = s.Learning! with { StoredLevel = result.StoredLevel, Xp = result.Xp } };
            });
            return p with { Skills = Known(EquatableArray.From(projected)) };
        })));
    }

    private static double ResearchCompletion(ProjectNode node, ForecastActivityPlan plan, Dictionary<string, ForecastActivity> tasks,
        IReadOnlyDictionary<string, double> finished)
    {
        var rows = plan.Allocations.Where(a => finished.ContainsKey(a.Activity) && tasks.TryGetValue(a.Activity, out var t)
            && t.ProjectProgress?.Project == ProjectGraph.ProgressKey(node)).ToArray();
        if (rows.Length == 1 && rows[0].Learning is { } learning
            && ForecastProductiveWorkPolicy.CompletionTick(learning, node.Work).TryGetValue(out var exact)) return exact;
        double Progress(int tick) => rows.Sum(a => a.Learning is { } learned
            ? learned.WorkSegments.Sum(segment => Math.Floor(Math.Clamp(tick - segment.Start, 0, segment.End - segment.Start)
                / (double)segment.CallbackInterval) * segment.CallbackInterval * segment.WorkPerTick)
            : Math.Clamp(tick - a.Start, 0, a.End - a.Start) * tasks[a.Activity].ProjectProgress!.PointsPerTick * a.WorkTicks / (a.End - a.Start));
        var low = (int)Math.Floor(rows.Min(a => a.Start));
        var high = (int)Math.Ceiling(rows.Max(a => a.End));
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (Progress(middle) >= node.Work) high = middle; else low = middle + 1;
        }
        return high;
    }
    private static Fact<double> Growth(ColonyModelInput input, ProjectNode node, double from, double until)
    {
        if (!input.Crops.TryGetValue(out var crops) || !input.Land.TryGetValue(out var land) || !input.Climate.TryGetValue(out var climate)) return default;
        var crop = crops.FirstOrDefault(c => c.Name == node.Crop);
        var field = land.FirstOrDefault(f => f.Id == node.Field);
        if (crop is null || field is null || field.Fertility.Count == 0 || !crop.HarvestMinGrowth.TryGetValue(out var remaining)) return default;
        var twelfth = climate.CurrentTwelfth;
        var boundary = Math.Max(1, climate.TicksToNextTwelfth) / (double)GameTime.TicksPerDay;
        while (boundary <= from) { twelfth = (twelfth + 1) % 12; boundary += ColonyModel.TwelfthDays; }
        while (from < until)
        {
            if (!CropGrowthModel.DailyGrowth(crop, field.Fertility.Average(), climate.Temperatures[twelfth]).TryGetValue(out var rate)) return default;
            var end = Math.Min(until, boundary);
            remaining -= (end - from) * rate;
            from = end;
            if (from >= boundary) { twelfth = (twelfth + 1) % 12; boundary += ColonyModel.TwelfthDays; }
        }
        return Known(Math.Max(0, remaining));
    }
}
