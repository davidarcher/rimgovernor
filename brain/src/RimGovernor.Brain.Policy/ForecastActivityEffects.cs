namespace RimGovernor.Brain.Policy;

/// <summary>Edible terminal output. Eligibility/holder and absolute expiry are conditional post-completion estimates;
/// quantity comes from the task output and nutrition per unit from the native item catalog.</summary>
public sealed record ForecastFoodEffect(Resource Resource, FoodStock Eligibility);
/// <summary>Conditional temperature bounds through the horizon, not inferred from a roof or heater designation.
/// Required activities include delivery, enclosure and heating dependencies where needed.</summary>
public sealed record ForecastShelterEffect(string Location, string[] RequiredActivities,
    Fact<bool> Enclosed, Fact<bool> Roofed, Fact<double> MinC, Fact<double> MaxC, Fact<double> ThroughTicks);

public static class ForecastActivityEffects
{
    internal static Fact<bool> AssessSurvivalEffects(ColonyForecastRequest request, ForecastActivityProblem problem,
        ForecastActivityRelief[] relief, Dictionary<string, ForecastSurvivalNeed> needs,
        ForecastActivityPlan plan, SortedSet<string> findings)
    {
        var tasks = problem.Activities.ToDictionary(t => t.Id, StringComparer.Ordinal);
        var allocations = plan.Allocations.ToDictionary(a => a.Activity, StringComparer.Ordinal);
        var horizon = request.HorizonDays * GameTime.TicksPerDay;
        Fact<bool> Unknown(string reason) { findings.Add(reason); return default; }
        Fact<bool> Refuse(string reason) { findings.Add(reason); return Known(false); }

        // Reuse the shared stock/claim calculation. Rebuild for each candidate, never spend it across alternatives.
        var consumed = plan.Allocations.SelectMany(a => tasks[a.Activity].Inputs).Select(i => i.Resource).ToHashSet();
        if (consumed.Count != 0)
        {
            if (request.Facts.Food.Stocks.Any(s => consumed.Contains(s.DefName)))
                return Unknown("consumes_baseline_food_requires_timed_reallocation");
            var budget = ForecastClaims.ForecastMaterialBudgetOf(request.Facts.Stock, request.Demand.Work.IngredientReservations,
                request.Commitments, request.HorizonDays);
            if (!budget.TryGetValue(out var available)) return Unknown("unknown_effect_material_budget");
            var balance = new Dictionary<Resource, double>();
            foreach (var resource in consumed)
            {
                if (!available.GetValueOrDefault(resource, Known(0.0)).TryGetValue(out var units))
                    return Unknown("unknown_effect_material:" + resource.Value);
                balance[resource] = units;
            }
            // Positive-duration output is ready for a dependent service at the same tick. Zero-duration
            // outputs follow consumption, so a task cannot finance its own input.
            var events = plan.Allocations.SelectMany(a => tasks[a.Activity].Inputs.Select(i => (Time: a.Start, Order: 1, Delta: -1, Item: i))
                .Concat(tasks[a.Activity].Outputs.Select(o => (Time: a.End, Order: a.End > a.Start ? 0 : 2, Delta: 1, Item: o))))
                .Where(e => consumed.Contains(e.Item.Resource)).OrderBy(e => e.Time).ThenBy(e => e.Order);
            foreach (var e in events)
            {
                if (!e.Item.Units.TryGetValue(out var units)) return Unknown("unknown_effect_material_quantity");
                balance[e.Item.Resource] += e.Delta * units;
                if (!double.IsFinite(balance[e.Item.Resource])) throw new DomainException("effect material overflow");
                if (balance[e.Item.Resource] < 0) return Refuse("insufficient_effect_materials");
            }
        }

        var food = relief.Where(r => r.Food is not null).GroupBy(r => (r.Completion, r.Food!.Resource)).ToArray();
        var supplies = new List<(double Ready, double Expiry, IReadOnlyList<ConsumerFoodForecast> Shares)>();
        foreach (var group in food)
        {
            var effect = group.First().Food!;
            if (plan.Allocations.Any(a => a.Start >= allocations[group.Key.Completion].End
                && tasks[a.Activity].Inputs.Any(i => i.Resource == effect.Resource)))
                return Refuse("edible_output_consumed_by_plan");
            if (group.Any(r => r.Food != effect)) throw new DomainException("conflicting evidence for one food output");
            var output = tasks[group.Key.Completion].Outputs.SingleOrDefault(o => o.Resource == effect.Resource);
            if (output is null || !output.Units.TryGetValue(out var quantity)
                || !request.Facts.Items.Nutrition.TryGetValue(effect.Resource, out var nutrition))
                return Unknown("unknown_edible_output");
            if (!double.IsFinite(nutrition) || nutrition < 0 || !double.IsFinite(quantity * nutrition))
                throw new DomainException("invalid edible nutrition");
            var stock = effect.Eligibility with { Id = group.Key.Completion, Nutrition = quantity * nutrition };
            var expiry = stock.Rot is FoodRot.Rots { Ticks: var rot } ? Math.Min(horizon, rot) : horizon;
            try
            {
                // Allocation is once per physical terminal output, including all competing eaters.
                var allocated = FoodForecastRules.ForecastFood(request.Facts.Food with { Stocks = [stock] }, null);
                supplies.Add((allocations[group.Key.Completion].End, expiry, allocated.Consumers));
            }
            catch (FoodFactsException) { return Unknown("unknown_edible_eligibility"); }
        }

        foreach (var effect in relief)
        {
            var need = needs[effect.Need];
            if ((effect.Food is null) == (effect.Shelter is null)) return Unknown("unknown_relief_effect:" + effect.Id);
            if (effect.Food is not null)
            {
                if (need.Basis != "eligible_food_exhaustion") throw new DomainException("food effect does not match need");
                var until = need.Window.Value.Ticks;
                foreach (var supply in supplies.OrderBy(s => s.Expiry).ThenBy(s => s.Ready))
                {
                    // No credit for a delivery after the current coverage ends. All rows use the same
                    // allocation; a shared output cannot promise its entire amount to every pawn.
                    if (supply.Ready > until || supply.Expiry <= until) continue;
                    var share = supply.Shares.SingleOrDefault(s => s.Id == need.Pawn);
                    if (share is null || share.NutritionPerDay <= 0) continue;
                    until += Math.Min(share.AllocatedNutrition / share.NutritionPerDay * GameTime.TicksPerDay, supply.Expiry - until);
                }
                if (until < horizon) return Refuse("insufficient_edible_coverage:" + effect.Id);
            }
            else
            {
                if (need.Basis != "constant_temperature_serious_hypothermia") throw new DomainException("shelter effect does not match need");
                var shelter = effect.Shelter!;
                var body = request.Demand.Work.Pawns.Value.Single(p => p.Id == need.Pawn).Body;
                if (!shelter.Enclosed.TryGetValue(out var enclosed) || !shelter.Roofed.TryGetValue(out var roofed)
                    || !shelter.MinC.TryGetValue(out var low) || !shelter.MaxC.TryGetValue(out var high)
                    || !shelter.ThroughTicks.TryGetValue(out var through) || !body.TryGetValue(out var stats)
                    || !stats.ComfortableMinC.TryGetValue(out var minimum) || !stats.ComfortableMaxC.TryGetValue(out var maximum))
                    return Unknown("unknown_shelter_protection:" + effect.Id);
                if (!double.IsFinite(low) || !double.IsFinite(high) || !double.IsFinite(through)
                    || !double.IsFinite(minimum) || !double.IsFinite(maximum) || low > high || minimum > maximum || through < 0)
                    throw new DomainException("invalid shelter temperature envelope");
                var completion = allocations[effect.Completion].End;
                var occupant = plan.Allocations.Where(a => a.Pawn == need.Pawn.Value).MaxBy(a => a.End);
                if (!enclosed || !roofed || low < minimum || high > maximum || through < horizon
                    || shelter.RequiredActivities.Any(id => !allocations.TryGetValue(id, out var a) || a.End > completion)
                    || occupant is null || occupant.End > completion || tasks[occupant.Activity].EndLocation != shelter.Location)
                    return Refuse("insufficient_shelter_protection:" + effect.Id);
            }
        }
        return Known(true);
    }
}
