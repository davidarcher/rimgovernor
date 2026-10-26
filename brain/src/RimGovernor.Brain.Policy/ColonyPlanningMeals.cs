using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.Policy;

public static partial class ColonyPlanningRules
{
    /// <summary>
    /// Builds the meal tier request from observed work priorities, never a proposed roster. The raw forecast
    /// keeps competing consumers, private ownership and spoilage deadlines.
    /// </summary>
    public static MealTierRequest MealRequest(ReviewFacts facts, ControlledEnvironment environment, double minDays, double targetDays)
    {
        var r = new MealTierRequest { Plan = facts.FoodPlan, MinDays = minDays, TargetDays = targetDays, Environment = environment };
        var kept = new List<FoodStock>();
        foreach (var stock in facts.CombinedFoodSupply.Stocks)
        {
            if (stock.RawClass != FoodIngredientClass.None && !facts.CombinedFoodSupply.IsReserved(stock) && !stock.IsCorpse())
            {
                kept.Add(stock);
            }
        }
        try
        {
            var f = FoodForecastRules.ForecastFood(facts.CombinedFoodSupply with { Stocks = kept }, null);
            var ids = new List<PawnId>(facts.FoodSupply.Consumers.Count);
            foreach (var c in facts.FoodSupply.Consumers)
            {
                ids.Add(c.Id);
            }
            f = f.GateOnColonists(ids, minDays);
            r = r with { RawRunwayDays = f.RunwayDays };
        }
        catch (DomainException)
        {
            throw new RequiredFactException("meal raw-food forecast");
        }
        r = r with { Cooks = EquatableArray.From(MealCooks(facts.ForecastObserved.WorkPawns.Pawns)) };
        var moodPawns = facts.MoodPawns;
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
            r = r with { HighExpectations = pressure };
        }
        r = r with { Previous = MealRules.ObservedMealTier(facts.EssentialInputs.Benches) };
        foreach (var dispenser in facts.FoodChannels.PasteDispenser)
        {
            if (dispenser.Powered && dispenser.HopperNutrition > 0)
            {
                r = r with { Previous = MealTier.Paste };
                break;
            }
        }
        foreach (var d in facts.Definitions)
        {
            if (d.PasteDispenser && d.Available)
            {
                r = r with { Paste = new Infrastructure { Name = d.Name, Available = d.Available, PowerW = (d.Power ?? throw new RequiredFactException("planning Power")).PowerW, Costs = d.CheapestCosts() } };
            }
        }
        return r;
    }
    /// <summary>Observed available, allocated cooking workers; shared by meal and preservation selection.</summary>
    public static IReadOnlyList<MealCook> MealCooks(IReadOnlyList<WorkPawn> pawns)
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
                        if (s.DefName == "Cooking" && !s.Disabled)
                        {
                            cooks.Add(new MealCook { Pawn = pawn.Id, Skill = s.Level });
                        }
                    }
                }
            }
        }
        return cooks;
    }
}
