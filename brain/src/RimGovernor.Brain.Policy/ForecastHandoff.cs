namespace RimGovernor.Brain.Policy;

/// <summary>The immutable review basis. A handoff never substitutes a new census or a new final date.</summary>
public sealed record ForecastModelBasis(ColonyModelInput Input, ColonyModelPolicy Policy);
public sealed record ForecastProjectDate(string Project, double Day);
public sealed record ForecastTerminalNeed(string Pawn, string Kind, ForecastTerm Level);
/// <summary>Effects already in force, with their original start date for memory aging and outfit ordering.</summary>
public sealed record ForecastTerminalEffect(ForecastCommitment Effect, double Day);
/// <summary>
/// Compact terminal evidence from one evaluated schedule; no trajectory or durable ownership. <paramref name="State"/> is the colony
/// model's state at <paramref name="Day"/>, absent while the terminal accounting leaves the food, a stock, the medicine or the holds unread
/// (<paramref name="Gaps"/> names which).
/// </summary>
public sealed record ForecastHandoff(double Day, double EndDay, ColonyModelState? State, EquatableArray<ForecastTerminalNeed> Needs,
    EquatableArray<ForecastProjectDate> Completed, EquatableArray<string> Gaps)
{
    public ProjectGraph Graph { get; init; } = new();
    public EquatableArray<ForecastTerminalEffect> ActiveEffects { get; init; }
    public EquatableArray<ForecastShelterOutcome> Shelters { get; init; }
    public EquatableArray<ForecastShelterPlan> ShelterPlans { get; init; }
    /// <summary>The planned rooms the selected schedule serves (#3796), at their service days.</summary>
    public EquatableArray<ForecastRoomService> ServedRooms { get; init; }
}

/// <summary>The same evaluation's compact accounting. Expensive model/skill projection is deferred until selection.</summary>
public sealed class ForecastTerminalAccounting
{
    internal ForecastTerminalAccounting(ColonyForecastRequest request, IReadOnlyDictionary<string, ForecastCommitment> commitments,
        IReadOnlyDictionary<Resource, double> stocks, IReadOnlyDictionary<string, double> finished,
        ForecastCoupledMaterials.MaterialHolds? holds)
    { Request = request; Commitments = commitments; Stocks = stocks; Finished = finished; Holds = holds; }
    internal ColonyForecastRequest Request { get; }
    internal IReadOnlyDictionary<string, ForecastCommitment> Commitments { get; }
    internal IReadOnlyDictionary<Resource, double> Stocks { get; }
    internal IReadOnlyDictionary<string, double> Finished { get; }
    internal ForecastCoupledMaterials.MaterialHolds? Holds { get; }
    public EquatableArray<ForecastTerminalLot> Lots { get; init; }
    public bool FoodUnread { get; init; }
    public bool MedicineComplete { get; init; }
}

public static class ForecastHandoffPolicy
{
    /// <summary>Project a selected outcome (or bounded finalist), without reevaluating its schedule or reading another census.</summary>
    public static ForecastHandoff? Project(ForecastCoupledOutcome outcome) => outcome.Terminal is { Request.Coupled.ModelBasis: not null } t
        ? Project(t.Request, t.Commitments, outcome, t.Stocks, t.Finished, t.Holds, t) : null;

    internal static ForecastHandoff Project(ColonyForecastRequest request, IReadOnlyDictionary<string, ForecastCommitment> commitments,
        ForecastCoupledOutcome outcome, IReadOnlyDictionary<Resource, double> physical,
        IReadOnlyDictionary<string, double> finished, ForecastCoupledMaterials.MaterialHolds? holds, ForecastTerminalAccounting terminal)
    {
        var context = request.Coupled!;
        var basis = context.ModelBasis!;
        var start = ColonyModelState.Observed(basis.Input, basis.Policy);
        var gaps = new SortedSet<string>(StringComparer.Ordinal);
        var day = request.HorizonDays;
        var dates = new List<ForecastProjectDate>();
        var needs = outcome.Needs.SelectMany(p => p.Value.Needs.Select(n => new ForecastTerminalNeed(p.Key, n.Kind, n.FinalLevel))).ToArray();
        foreach (var n in needs.Where(n => n.Level is ForecastTerm.Unassessed)) gaps.Add($"terminal_need_unknown:{n.Pawn}:{n.Kind}");
        foreach (var e in basis.Policy.Scheduled.Where(e => e.Day <= day && e.Stock is ModelStock.Materials or ModelStock.Medicine && string.IsNullOrEmpty(e.Resource.Value)))
            gaps.Add("terminal_scheduled_resource_unknown:" + e.Id);
        foreach (var pawn in basis.Input.Pawns)
            foreach (var need in pawn.Needs)
                if (!needs.Any(n => n.Pawn == pawn.Pawn && n.Kind == need.Kind)) gaps.Add($"terminal_need_missing:{pawn.Pawn}:{need.Kind}");
        if (!outcome.Feasible) gaps.Add("terminal_schedule_infeasible");
        foreach (var c in commitments.Values)
        {
            foreach (var unsupported in c.UnsupportedEffects) gaps.Add("terminal_unsupported:" + unsupported);
        }

        var stocks = physical.Select(p => new ModelResourceStock(p.Key, p.Value)).ToArray();
        var stockKnown = physical.All(p => p.Value >= 0);
        if (!SameStocks(request.Facts.Stock, basis.Input.Stocks.Materials))
        { stockKnown = false; gaps.Add("terminal_material_census_scope_mismatch"); }
        if (!stockKnown) gaps.Add("terminal_stock_unknown");
        var nutrition = Nutrition(terminal, gaps);
        EquatableArray<ModelResourceStock>? medicine = null;
        if (terminal.MedicineComplete)
        {
            var result = new List<ModelResourceStock>();
            var medicineLots = terminal.Lots.Where(l => l.Medicine).ToArray();
            foreach (var group in medicineLots.GroupBy(l => l.Resource).OrderByDescending(g => request.Facts.Items.MedicalPotency.GetValueOrDefault(g.Key)).ThenBy(g => g.Key))
            {
                result.Add(new(group.Key, group.Sum(l => l.Count)));
            }
            if (result.Count == medicineLots.Select(l => l.Resource).Distinct().Count()) medicine = EquatableArray.From(result);
        }
        if (medicine is null) gaps.Add("terminal_medicine_unknown");

        var tasks = context.Activities.Activities.ToDictionary(t => t.Id, StringComparer.Ordinal);
        var projected = ForecastHandoffProjects.Project(basis, context, outcome, finished, day, gaps);
        var nodes = projected.Graph.Nodes;
        var projects = projected.States;
        dates.AddRange(projected.Completed);
        EquatableArray<ModelResourceStock>? external = null;
        var budget = ForecastCoupledMaterials.CoupledMaterialBudgetOf(request, holds, tasks, outcome.Plan);
        if (holds is not null && budget is MaterialBudget.Read(var available))
        {
            var initial = request.Facts.Stock;
            var reserved = new List<ModelResourceStock>();
            foreach (var resource in initial.Select(s => s.Resource).Concat(available.Keys).Distinct())
            {
                var free = available.GetValueOrDefault(resource);
                var count = Math.Max(0, initial.Where(s => s.Resource == resource).Sum(s => (double)s.Count) - free);
                // Deliveries pay the shared construction demand; unrelated reservations remain protected.
                var delivered = outcome.Plan.Allocations.Where(a => finished.ContainsKey(a.Activity)
                    && context.Bindings.TryGetValue((a.Activity, a.Pawn), out var b) && b.Kind == PawnActivityKind.Supply && b.Container.Length > 0)
                    .SelectMany(a => tasks[a.Activity].Inputs).Where(i => i.Resource == resource).Sum(i => i.Units);
                count -= Math.Min(delivered, holds.Owed.GetValueOrDefault(resource));
                if (count > 0) reserved.Add(new(resource, count));
            }
            if (!gaps.Any(g => g.StartsWith("terminal_hold_unknown:", StringComparison.Ordinal))) external = EquatableArray.From(reserved);
        }
        if (external is null) gaps.Add("terminal_holds_unknown");

        var active = EquatableArray.From(commitments.Values.Where(c => c.MoodMemories.Count + c.SituationalChanges.Count + c.ComfortShifts.Count + c.OutfitChanges.Count > 0
            && c.ActivityCompletions.All(finished.ContainsKey))
            .Select(c => new ForecastTerminalEffect(c with { StockChanges = [], MaterialClaims = [], EquipmentClaims = [], ActivityCompletions = [],
                Outcome = null, UnsupportedEffects = [], ExclusiveOutcome = "" },
                c.ActivityCompletions.Count == 0 ? 0 : c.ActivityCompletions.Max(id => finished[id]) / GameTime.TicksPerDay))
            .OrderBy(c => c.Day).ThenBy(c => c.Effect.Id, StringComparer.Ordinal));
        var pawns = EquatableArray.From(Pawns(start.Pawns, context.PracticeTasks, outcome, finished, day, gaps).Select(p =>
        {
            var shifts = active.SelectMany(c => c.Effect.ComfortShifts).Where(s => s.Pawn == p.Pawn).ToArray();
            return p with { ComfortableMin = p.ComfortableMin + shifts.Sum(s => s.MinDelta), ComfortableMax = p.ComfortableMax + shifts.Sum(s => s.MaxDelta) };
        }));
        ColonyModelState? state = null;
        if (nutrition is { } food && stockKnown && medicine is { } medicineRows && external is { } holdRows)
        {
            state = start with { Day = day, Nutrition = food, Materials = EquatableArray.From(stocks),
                ResearchPoints = basis.Policy.Projects.Nodes.Where(n => n.Kind == ProjectKind.Research).Sum(n => n.Work - projects[n.Id].Remaining)
                    + basis.Policy.Scheduled.Where(e => e.Day <= day && e.Stock == ModelStock.ResearchPoints).Sum(e => e.Amount),
                Medicine = medicineRows, ExternalHolds = holdRows, Projects = EquatableArray.From(nodes.Select(n => projects[n.Id])), Pawns = pawns };
            if (state.Gap(projected.Graph) is { } gap) gaps.Add(gap);
        }
        // A shelter whose shell completes carries on as a standing, closed room at its projected temperature; one with no
        // projected temperature carries on unestimated.
        var shelterPlans = EquatableArray.From(context.Shelters
            .Select(p => (Plan: p, Outcome: outcome.Shelters.SingleOrDefault(s => s.Room == p.Room) as ForecastShelterOutcome.Scheduled))
            .Where(p => p.Outcome is not null)
            .Select(p => p.Plan with {
                Thermal = (p.Plan.Thermal, p.Outcome) is (ForecastShelterThermal.Estimated estimated, ForecastShelterOutcome.Projected projected)
                    ? estimated with { InitialTemperature = projected.TemperatureAtHorizon, Standing = new ForecastShelterStanding.Enclosed(estimated.ExchangePerTick) }
                    : new ForecastShelterThermal.Unestimated(),
                Prerequisites = [], AlternativePrerequisites = [], RoofCells = [],
                Sources = EquatableArray.From(p.Plan.Sources.Select(s => s with { Complete = true,
                    Supply = HeatSupplies.After(s.Supply, day * GameTime.TicksPerDay - p.Outcome!.CompleteAt) })) }));
        return new(day, start.EndDay, state, EquatableArray.From(needs), EquatableArray.From(dates), EquatableArray.From(gaps))
        { Graph = projected.Graph, ActiveEffects = active, Shelters = outcome.Shelters, ShelterPlans = shelterPlans, ServedRooms = outcome.ServedRooms };
    }

    private static bool SameStocks(EquatableArray<Amount> a, EquatableArray<Amount> b) =>
        a.Concat(b).Select(s => s.Resource).Distinct().All(r => a.Where(s => s.Resource == r).Sum(s => s.Count) == b.Where(s => s.Resource == r).Sum(s => s.Count));

    private static double? Nutrition(ForecastTerminalAccounting terminal, SortedSet<string> gaps)
    {
        if (terminal.FoodUnread) { gaps.Add("terminal_food_census_unknown"); return null; }
        double total = 0;
        foreach (var lot in terminal.Lots)
        {
            switch (lot.FoodEligible)
            {
                case FoodEligibility.NoObservedUse: gaps.Add("terminal_food_eligibility_unknown:" + lot.Id); return default;
                case FoodEligibility.Eligible: total += lot.Count * lot.NutritionPerUnit; break;
                case FoodEligibility.Ineligible: break;
            }
        }
        return total;
    }
    private static ModelPawn[] Pawns(EquatableArray<ModelPawn> original, EquatableArray<ForecastPracticeTask> practice, ForecastCoupledOutcome outcome,
        IReadOnlyDictionary<string, double> finished, double day, SortedSet<string> gaps)
    {
        return original.Select(p =>
        {
            var projected = p.Skills.Select(s =>
            {
                var events = outcome.Plan.Allocations.Where(a => a.Pawn == p.Pawn && finished.ContainsKey(a.Activity) && a.Learning?.Skill == s.Name)
                    .SelectMany(a => a.Learning!.Callbacks).ToList();
                foreach (var task in practice.Where(t => t.Worker.Pawn == p.Pawn && t.Worker.Skill == s.Name))
                {
                    var allocation = outcome.Plan.Allocations.FirstOrDefault(a => a.Activity == task.Task && finished.ContainsKey(a.Activity));
                    if (allocation is null) continue;
                    events.AddRange(ForecastMaintenancePolicy.PracticeCallbacks(task, allocation, (int)Math.Ceiling(day * GameTime.TicksPerDay)));
                    gaps.Add("terminal_practice_injury_recovery_unassessed:" + task.Task);
                }
                if (ForecastSkillPolicy.ForecastSkill(s.Learning, events.OrderBy(e => e.Tick).ThenBy(e => e.Id, StringComparer.Ordinal).ToArray(),
                    (int)Math.Ceiling(day * GameTime.TicksPerDay)) is not SkillForecast.Projected { Projection: var result })
                { gaps.Add($"terminal_skill_unprojectable:{p.Pawn}:{s.Name}"); return s with { Learning = null }; }
                return s with { Level = result.Level, Stored = result.StoredLevel, Learning = result.TerminalLearning };
            });
            return p with { Skills = EquatableArray.From(projected) };
        }).ToArray();
    }

}
