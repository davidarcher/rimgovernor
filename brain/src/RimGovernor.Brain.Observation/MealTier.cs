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
            var complete = true;
            foreach (var stock in supply.Stocks)
            {
                if (!stock.RawClass.TryGetValue(out var kind))
                {
                    complete = false;
                    break;
                }
                if (kind != FoodIngredientClass.None && !stock.Reserve && !stock.Corpse)
                {
                    kept.Add(stock);
                }
            }
            if (complete)
            {
                try
                {
                    var f = PolicyFunctions.ForecastFood(supply with { Stocks = kept }, null);
                    if (FoodSupply.TryGetValue(out var human))
                    {
                        var ids = new List<PawnId>(human.Consumers.Count);
                        foreach (var c in human.Consumers)
                        {
                            ids.Add(c.Id);
                        }
                        f = f.GateOnColonists(ids, minDays);
                    }
                    r = r with { RawRunwayDays = f.RunwayDays };
                }
                catch (DomainException)
                {
                    // A forecast the policy refuses leaves the raw runway unknown.
                }
            }
        }
        if (WorkPawns.TryGetValue(out var pawns))
        {
            var cooks = new List<MealCook>();
            var complete = true;
            foreach (var pawn in pawns)
            {
                var ak = pawn.Available.TryGetValue(out var available);
                var wk = pawn.Work.TryGetValue(out var work);
                var sk = pawn.Skills.TryGetValue(out var skills);
                if (!ak || !wk || !sk)
                {
                    complete = false;
                    break;
                }
                if (!available)
                {
                    continue;
                }
                foreach (var w in work)
                {
                    if (w.Work == WorkType.Cooking && w.Priority > 0 && !w.Disabled)
                    {
                        foreach (var s in skills)
                        {
                            if (s.Name == "Cooking" && !s.Disabled)
                            {
                                cooks.Add(new MealCook { Pawn = pawn.ID, Skill = s.Level });
                            }
                        }
                    }
                }
            }
            if (complete)
            {
                r = r with { Cooks = Known<IReadOnlyList<MealCook>>(cooks), CookLaborConstrained = Known(cooks.Count == 0) };
            }
        }
        if (Facts.MoodPawns.TryGetValue(out var moodPawns))
        {
            var pressure = false;
            var complete = true;
            foreach (var pawn in moodPawns)
            {
                var hk = pawn.HighExpectations.TryGetValue(out var high);
                var mk = pawn.Mood.TryGetValue(out var mood);
                var tk = pawn.Target.TryGetValue(out var target);
                if (hk && high && mk && tk && mood < target)
                {
                    pressure = true;
                }
                if (!pawn.Thoughts.TryGetValue(out var thoughts))
                {
                    complete = false;
                    continue;
                }
                foreach (var thought in thoughts)
                {
                    if (thought.Def == "HighExpectations" || thought.Def == "SkyHighExpectations")
                    {
                        pressure = true;
                    }
                }
            }
            if (complete || pressure)
            {
                r = r with { HighExpectations = Known(pressure) };
            }
        }
        if (ProductionBenches.TryGetValue(out var benches))
        {
            r = r with { Previous = PolicyFunctions.ObservedMealTier(benches) };
        }
        if (FoodChannels.TryGetValue(out var channels))
        {
            foreach (var dispenser in channels.PasteDispenser)
            {
                var pk = dispenser.Powered.TryGetValue(out var powered);
                var nk = dispenser.HopperNutrition.TryGetValue(out var nutrition);
                if (pk && powered && nk && nutrition > 0)
                {
                    r = r with { Previous = MealTier.Paste };
                    break;
                }
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
