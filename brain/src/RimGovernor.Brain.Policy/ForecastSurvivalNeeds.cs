namespace RimGovernor.Brain.Policy;

/// <summary>Bounded diagnostic: Ticks is a deadline only when Due is true, otherwise the assessed horizon.</summary>
public sealed record ForecastNeedWindow(double Ticks, bool Due);
public sealed record ForecastSurvivalNeed(string Id, PawnId Pawn, string Basis, Fact<ForecastNeedWindow> Window);
public sealed partial record ForecastSurvivalNeeds(ForecastSurvivalNeed[] Needs, string[] Findings);
/// <summary>Explicit candidate effect: completing this task prevents this need through the assessed horizon.
/// A roof or harvested crop alone does not establish that effect.</summary>
public sealed record ForecastActivityRelief(string Id, string Need, string Completion)
{
    public ForecastFoodEffect? Food { get; init; }
    public ForecastShelterEffect? Shelter { get; init; }
}

public sealed partial record ForecastSurvivalNeeds
{
    /// <summary>Use the existing food allocation and cold model on the same observed forecast frame.
    /// Food exhaustion is not starvation onset; cold assumes unchanged cell temperature and apparel.</summary>
    public static ForecastSurvivalNeeds SurvivalNeedsOf(ColonyForecastRequest request)
    {
        var horizon = request.HorizonDays * GameTime.TicksPerDay;
        if (!double.IsFinite(horizon) || horizon <= 0 || request.Origin.ObservedTick != request.Demand.Work.Tick.Value)
            throw new DomainException("invalid survival forecast horizon or frame");
        if (!request.Demand.Work.Pawns.TryGetValue(out var pawns)) return new([], ["unknown_survival_roster"]);
        if (pawns.Select(p => p.Id).Distinct().Count() != pawns.Count || pawns.Any(p => p.Id.IsEmpty))
            throw new DomainException("invalid survival roster");
        var findings = new SortedSet<string>(StringComparer.Ordinal);
        var food = new Dictionary<PawnId, ConsumerFoodForecast>();
        if (request.Facts.FoodUnread) findings.Add("unknown_survival_food");
        else try
        {
            food = FoodForecastRules.ForecastFood(request.Facts.Food, pawns.Select(p => p.Id).ToArray()).Consumers.ToDictionary(p => p.Id);
        }
        catch (FoodFactsException) { findings.Add("unknown_survival_food"); }
        var needs = new List<ForecastSurvivalNeed>();
        Fact<ForecastNeedWindow> Window(double ticks)
        {
            if (!double.IsFinite(ticks) || ticks < 0) return default;
            return Known(new ForecastNeedWindow(Math.Min(ticks, horizon), ticks <= horizon));
        }
        foreach (var pawn in pawns.OrderBy(p => p.Id.Value, StringComparer.Ordinal))
        {
            var foodWindow = food.TryGetValue(pawn.Id, out var row) ? Window(row.RunwayDays * GameTime.TicksPerDay) : default;
            needs.Add(new("food/" + pawn.Id.Value, pawn.Id, "eligible_food_exhaustion", foodWindow));
            Fact<ForecastNeedWindow> coldWindow = default;
            var (temperature, comfort, severity) = (pawn.Body.CellTemperatureC, pawn.Body.ComfortableMinC, pawn.Body.Hypothermia);
            if (double.IsFinite(temperature) && double.IsFinite(comfort) && double.IsFinite(comfort - temperature)
                && double.IsFinite(severity) && severity >= 0 && severity <= 1)
            {
                // Already serious requires immediate attention even if the current cell is warm.
                if (severity >= DashEstimateRules.HypothermiaSerious) coldWindow = Window(0);
                else
                {
                    var budget = DashEstimateRules.OutdoorBudgetFor(HazardKind.ExtremeCold, Known(temperature), Known(comfort),
                        Known(severity), DashEstimateRules.HypothermiaSerious, DashEstimateRules.PublishedHypothermiaTuning).Value;
                    coldWindow = budget.Unbounded ? Known(new ForecastNeedWindow(horizon, false)) : Window(budget.Ticks);
                }
            }
            needs.Add(new("cold/" + pawn.Id.Value, pawn.Id, "constant_temperature_serious_hypothermia", coldWindow));
        }
        foreach (var need in needs.Where(n => !n.Window.IsKnown)) findings.Add("unknown_need_window:" + need.Id);
        return new(needs.ToArray(), findings.ToArray());
    }

    /// <summary>Earliest observed deadline first, subject to the caller's explicit labor ceiling and effect evidence.
    /// Task generation, material feasibility and native execution remain separate responsibilities.</summary>
    public static ForecastActivityOutcomeComparison CompareSurvivalActivities(ColonyForecastRequest request,
        ForecastActivityProblem problem, ForecastActivityRelief[] relief)
    {
        if (!problem.Deadline.TryGetValue(out var deadline) || deadline != request.HorizonDays * GameTime.TicksPerDay)
            throw new DomainException("activity and survival horizons must match");
        var observed = SurvivalNeedsOf(request);
        if (request.Demand.Work.Pawns.TryGetValue(out var roster)
            && problem.Pawns.Any(p => !roster.Any(r => r.Id.Value == p.Pawn)))
            throw new DomainException("activity capacity names an unobserved pawn");
        if (relief.Select(r => r.Id).Distinct().Count() != relief.Length)
            throw new DomainException("duplicate survival relief alternative");
        var needs = observed.Needs.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var findings = new SortedSet<string>(observed.Findings, StringComparer.Ordinal);
        var outcomes = new List<ForecastActivityOutcome>();
        foreach (var effect in relief)
        {
            if (!needs.TryGetValue(effect.Need, out var need))
            { findings.Add("unobserved_relief_need:" + effect.Id); continue; }
            if (!need.Window.TryGetValue(out var window) || !window.Due) continue;
            outcomes.Add(new(effect.Id, effect.Need, effect.Completion, Known(window.Ticks), default) { ChoiceIdentity = effect.Need });
        }
        var comparison = ForecastActivityOutcomes.CompareActivityOutcomes(problem, outcomes.ToArray(), ForecastOutcomeObjective.EarliestDeadline,
            (selected, plan) => ForecastActivityEffects.AssessSurvivalEffects(request, problem, relief.Where(r => selected.Any(o => o.Id == r.Id)).ToArray(),
                needs, plan, findings));
        findings.UnionWith(comparison.Findings);
        return comparison with { Findings = findings.ToArray() };
    }
}
