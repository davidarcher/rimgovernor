namespace RimGovernor.Brain.Policy;

/// <summary>Bounded diagnostic: Ticks is a deadline only when Due is true, otherwise the assessed horizon.</summary>
public sealed record ForecastNeedWindow(double Ticks, bool Due);
public sealed record ForecastSurvivalNeed(string Id, PawnId Pawn, string Basis, Fact<ForecastNeedWindow> Window);
public sealed partial record ForecastSurvivalNeeds(ForecastSurvivalNeed[] Needs, string[] Findings);

public sealed partial record ForecastSurvivalNeeds
{
    /// <summary>Use the existing food allocation and cold model on the same observed forecast frame.
    /// Food exhaustion is not starvation onset; cold assumes unchanged cell temperature and apparel.</summary>
    public static ForecastSurvivalNeeds SurvivalNeedsOf(ColonyForecastRequest request)
    {
        var horizon = request.HorizonDays * GameTime.TicksPerDay;
        if (!double.IsFinite(horizon) || horizon <= 0 || request.Origin.ObservedTick != request.Demand.Work.Tick.Value)
            throw new DomainException("invalid survival forecast horizon or frame");
        if (request.Demand.Work.Pawns is not WorkPawnsRead.Read { Pawns: var pawns }) return new([], ["unknown_survival_roster"]);
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
}
