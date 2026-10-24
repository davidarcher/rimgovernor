using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.Observation;

public sealed partial record ColonyProjection
{
    /// <summary>
    /// Builds the meal tier request from observed work priorities, never a proposed roster. The raw forecast
    /// keeps competing consumers, private ownership and spoilage deadlines.
    /// </summary>
    public MealTierRequest MealRequest(double minDays, double targetDays)
    {
        var r = new MealTierRequest { Plan = Facts.FoodPlan, MinDays = minDays, TargetDays = targetDays, Environment = Environment };
        if (CombinedFoodSupply.TryGetValue(out var supply))
        {
            var kept = new List<FoodStock>();
            foreach (var stock in supply.Stocks)
            {
                if (stock.RawClass != FoodIngredientClass.None && !stock.Reserve && !stock.Corpse)
                {
                    kept.Add(stock);
                }
            }
            try
            {
                var f = FoodForecastRules.ForecastFood(supply with { Stocks = kept }, null);
                if (FoodSupply.TryGetValue(out var human))
                {
                    var ids = new List<PawnId>(human.Consumers.Count);
                    foreach (var c in human.Consumers)
                    {
                        ids.Add(c.Id);
                    }
                    f = f.GateOnColonists(ids, minDays);
                }
                if (f.RunwayDays is FoodRunway.Days { Value: var raw })
                {
                    r = r with { RawRunwayDays = Known(raw) };
                }
            }
            catch (DomainException)
            {
                // A forecast the policy refuses leaves the raw runway unknown.
            }
        }
        if (WorkPawns.TryGetValue(out var pawns))
        {
            var cooks = new List<MealCook>();
            foreach (var pawn in pawns)
            {
                if (!pawn.Available)
                {
                    continue;
                }
                foreach (var w in pawn.Work.Priorities())
                {
                    if (w.Work == WorkType.Cooking && w.Priority > 0 && !w.Disabled)
                    {
                        foreach (var s in pawn.Skills)
                        {
                            if (s.Name == "Cooking" && !s.Disabled)
                            {
                                cooks.Add(new MealCook { Pawn = pawn.Id, Skill = s.Level });
                            }
                        }
                    }
                }
            }
            r = r with { Cooks = Known(EquatableArray.From(cooks)) };
        }
        if (Facts.MoodPawns.TryGetValue(out var moodPawns))
        {
            var pressure = false;
            foreach (var pawn in moodPawns)
            {
                if (pawn.HighExpectations && pawn.Mood is MoodLevel.Has { Level: var mood, Target: var target } && mood < target)
                {
                    pressure = true;
                }
                foreach (var thought in pawn.Thoughts)
                {
                    if (thought.Def == "HighExpectations" || thought.Def == "SkyHighExpectations")
                    {
                        pressure = true;
                    }
                }
            }
            r = r with { HighExpectations = Known(pressure) };
        }
        if (ProductionBenches.TryGetValue(out var benches))
        {
            r = r with { Previous = MealRules.ObservedMealTier(benches) };
        }
        foreach (var dispenser in FoodChannels.PasteDispenser)
        {
            if (dispenser.Powered && dispenser.HopperNutrition > 0)
            {
                r = r with { Previous = MealTier.Paste };
                break;
            }
        }
        foreach (var d in Definitions)
        {
            if (d.Name == "NutrientPasteDispenser")
            {
                r = r with { Paste = Known(new Infrastructure { Name = d.Name, Available = d.Available, PowerW = d.PowerW, Costs = d.CheapestCosts() }) };
            }
        }
        return r;
    }
}
