namespace RimGovernor.Brain.Policy;

/// <summary>The immutable review basis. A handoff never substitutes a new census or a new final date.</summary>
public sealed record ForecastModelBasis(ColonyModelInput Input, ColonyModelPolicy Policy);
public sealed record ForecastProjectDate(string Project, double Day);
public sealed record ForecastTerminalNeed(string Pawn, string Kind, Fact<double> Level);
/// <summary>Effects already in force, with their original start date for memory aging and outfit ordering.</summary>
public sealed record ForecastTerminalEffect(ForecastCommitment Effect, double Day);
/// <summary>Compact terminal evidence from one evaluated schedule; no trajectory or durable ownership.</summary>
public sealed record ForecastHandoff(ColonyModelState State, EquatableArray<ForecastTerminalNeed> Needs,
    EquatableArray<ForecastProjectDate> Completed, EquatableArray<string> Gaps)
{
    public ProjectGraph Graph { get; init; } = new();
    public EquatableArray<ForecastTerminalEffect> ActiveEffects { get; init; }
    public EquatableArray<ForecastShelterOutcome> Shelters { get; init; }
    public EquatableArray<ForecastShelterPlan> ShelterPlans { get; init; }
    public EquatableArray<ForecastRoomClosure> ClosedRooms { get; init; }
}

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
        }

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

        var tasks = context.Activities.Activities.ToDictionary(t => t.Id, StringComparer.Ordinal);
        var projected = ForecastHandoffProjects.Project(basis, context, outcome, finished, day, gaps);
        var nodes = projected.Graph.Nodes;
        var projects = projected.States;
        dates.AddRange(projected.Completed);
        Fact<EquatableArray<ModelResourceStock>> external = default;
        var budget = ForecastCoupledMaterials.CoupledMaterialBudgetOf(request, holds, tasks, outcome.Plan);
        var ownedSites = nodes.Where(n => n.Kind == ProjectKind.Construction && n.Target.Length > 0
            && projects[n.Id].Status != ModelProjectStatus.Done && basis.Input.Sites.IsKnown
            && basis.Input.Sites.Value.Any(s => s.Id == n.Target)).ToArray();
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
                    && context.Bindings.TryGetValue((a.Activity, a.Pawn), out var b) && b.Kind == PawnActivityKind.Supply && b.Container.Length > 0)
                    .SelectMany(a => tasks[a.Activity].Inputs).Where(i => i.Resource == resource && i.Units.IsKnown).Sum(i => i.Units.Value);
                count -= Math.Min(delivered, holds.Owed.GetValueOrDefault(resource));
                var graphHeld = ownedSites.Sum(n => n.Ingredients.Where(i => i.Resource == resource).Sum(i => (double)i.Count));
                if (graphHeld > Math.Max(0, holds.Owed.GetValueOrDefault(resource) - delivered))
                    gaps.Add("terminal_construction_hold_scope_mismatch:" + resource);
                count -= Math.Min(Math.Max(0, count), graphHeld);
                if (count > 0) reserved.Add(new(resource, count));
            }
            if (!gaps.Any(g => g.StartsWith("terminal_hold_unknown:", StringComparison.Ordinal))) external = Known(EquatableArray.From(reserved));
        }
        if (!external.IsKnown) gaps.Add("terminal_holds_unknown");

        if (external.IsKnown)
            foreach (var node in ownedSites)
                projects[node.Id] = projects[node.Id] with { Inputs = ModelProjectInputs.Held };
        var active = EquatableArray.From(commitments.Values.Where(c => c.MoodMemories.Count + c.SituationalChanges.Count + c.ComfortShifts.Count + c.OutfitChanges.Count > 0
            && c.ActivityCompletions.All(finished.ContainsKey))
            .Select(c => new ForecastTerminalEffect(c with { StockChanges = [], MaterialClaims = [], EquipmentClaims = [], ActivityCompletions = [],
                Construction = null, AlternativeLaborCost = default, Outcome = null, UnsupportedEffects = [], ExclusiveOutcome = "" },
                c.ActivityCompletions.Count == 0 ? 0 : c.ActivityCompletions.Max(id => finished[id]) / GameTime.TicksPerDay))
            .OrderBy(c => c.Day).ThenBy(c => c.Effect.Id, StringComparer.Ordinal));
        var pawns = Pawns(start.Pawns, context.PracticeTasks, outcome, finished, day, gaps);
        if (pawns.TryGetValue(out var terminalPawns))
            pawns = Known(EquatableArray.From(terminalPawns.Select(p =>
            {
                var shifts = active.SelectMany(c => c.Effect.ComfortShifts).Where(s => s.Pawn == p.Pawn).ToArray();
                return p with { ComfortableMin = p.ComfortableMin.IsKnown ? Known(p.ComfortableMin.Value + shifts.Sum(s => s.MinDelta)) : default,
                    ComfortableMax = p.ComfortableMax.IsKnown ? Known(p.ComfortableMax.Value + shifts.Sum(s => s.MaxDelta)) : default };
            })));
        var state = start with { Day = day, Nutrition = nutrition, Materials = stockKnown ? Known(EquatableArray.From(stocks)) : default,
            Medicine = medicine, ExternalHolds = external, Projects = Known(EquatableArray.From(nodes.Select(n => projects[n.Id]))), Pawns = pawns };
        if (state.Gap(projected.Graph) is { } gap) gaps.Add(gap);
        var shelterPlans = EquatableArray.From(context.Shelters.Where(p => outcome.Shelters.Any(s => s.Room == p.Room && s.CompleteAt.IsKnown))
            .Select(p => p with { InitialTemperature = outcome.Shelters.Single(s => s.Room == p.Room).TemperatureAtHorizon,
                Prerequisites = [], AlternativePrerequisites = [], RoofCells = [], StandingExchangePerTick = p.ExchangePerTick,
                Sources = EquatableArray.From(p.Sources.Select(s => s with { Complete = true,
                    SupplyTicks = s.SupplyTicks.IsKnown ? Known(Math.Max(0, s.SupplyTicks.Value -
                        (day * GameTime.TicksPerDay - outcome.Shelters.Single(o => o.Room == p.Room).CompleteAt.Value))) : default })) }));
        return new(state, EquatableArray.From(needs), EquatableArray.From(dates), EquatableArray.From(gaps))
        { Graph = projected.Graph, ActiveEffects = active, Shelters = outcome.Shelters, ShelterPlans = shelterPlans, ClosedRooms = outcome.ClosedRooms };
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

}
