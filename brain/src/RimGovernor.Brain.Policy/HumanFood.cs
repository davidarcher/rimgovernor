namespace RimGovernor.Brain.Policy;

public sealed record HumanButcherCandidate
{
    public PawnId ID { get; init; }
    public EquatableArray<PawnTrait> Traits { get; init; }

    /// <summary>The pawn's own ideoligion and precept defNames from its row; required from the matching pawn row.</summary>
    public required HeldPrecepts Held { get; init; }

    public bool CanWork { get; init; }
}

/// <summary>A pawn's ideoligion load id (empty without one) and the precept defNames of it.</summary>
public sealed record HeldPrecepts
{
    public string Ideo { get; init; } = "";
    public IReadOnlyList<string> Defs { get; init; } = [];
}

public enum HumanMeatRoute
{
    [WireName("animal_feed")]
    Feed,

    [WireName("survival_trade")]
    SurvivalTrade,

    [WireName("raw_trade")]
    RawTrade,

    [WireName("eligible_meals")]
    Meals,
}

/// <summary>Allocates existing nutrition once, in destination order. Survival production requires observed vegetable nutrition and production capacity; raw trade is the fallback when nobody will eat the remainder.</summary>
public sealed record HumanMeatRouting
{
    public double Nutrition { get; init; }
    public double FeedShortfall { get; init; }
    public double EligibleMealDemand { get; init; }
    public double VegetableNutrition { get; init; }
    public bool CanMakeSurvivalMeals { get; init; }
}

public sealed record HumanMeatAllocation(HumanMeatRoute Route, double Nutrition);

public static class HumanFoodRules
{
    /// <summary>The HistoryEventDef a butchered human raises (HistoryEventDefOf.ButcheredHuman); the precept rule answers by it.</summary>
    public const string ButcheredHumanEvent = "ButcheredHuman";

    /// <summary>
    /// Whether the pawn's ideoligion costs no mood for butchering a human: it has one, and none of its precepts
    /// has a self-took-action effect on ButcheredHuman with a mood-losing stage. A pawn without an ideoligion is
    /// not acceptable (only a trait qualifies it); with Ideology absent no precept exists to object. The matching pawn row is required.
    /// </summary>
    public static bool PreceptAcceptable(HeldPrecepts held, IdeologyMode ideology)
    {
        if (ideology is not IdeologyMode.Installed { Primary: var i })
        {
            return true;
        }
        var h = held;
        if (h.Ideo.Length == 0)
        {
            return false;
        }
        foreach (var e in i.HeldBy(h.Defs).EffectsInForce())
        {
            if (e.Kind == PreceptEffectKind.SelfTookAction && e.HistoryEvent == ButcheredHumanEvent && e.Penalises())
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Picks the lowest-id eligible worker whose observed ideoligion permits butchering humans. Without
    /// the expansion no precept forbids the action; native work eligibility still gates the worker.
    /// </summary>
    public static (PawnId Worker, bool Ok) QualifyingHumanButcher(IReadOnlyList<HumanButcherCandidate> rows, IdeologyMode ideology)
    {
        var selected = default(PawnId);
        var seen = new HashSet<PawnId>();
        foreach (var row in rows)
        {
            if (!FoodForecastRules.FoodId(row.ID.Value) || !seen.Add(row.ID))
            {
                return (default, false);
            }
            if (
                HumanButcherEligible(row.Traits, PreceptAcceptable(row.Held, ideology), row.CanWork)
                && !ButcherHeld(ideology, row.Traits)
                && (selected.IsEmpty || row.ID.CompareTo(selected) < 0)
            )
            {
                selected = row.ID;
            }
        }
        return (selected, !selected.IsEmpty);
    }

    /// <summary>Native candidates already establish disposition, assignment and reachability; execution rechecks them before the write. Never substitute another worker.</summary>
    public static (BillSelection Selection, bool Ok) SelectHumanButcher(EquatableArray<ProductionBench> rows, IdeologyMode ideology, PreferencesRecord preferences)
    {
        if (preferences.Forbids(ForbiddenMethod.HumanButchery))
        {
            return (new BillSelection(), false);
        }
        var choices = new List<BillSelection>();
        var animalBill = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var b in rows)
        {
            var nutrition = b.HumanCorpseNutrition;
            if (!b.Butcher || !b.Usable || !FoodForecastRules.FoodNumber(nutrition) || nutrition <= 0 || !FoodForecastRules.FoodId(b.Token) || !FoodForecastRules.FoodId(b.Id) || b.Bills.Count >= 15)
            {
                continue;
            }
            var exists = false;
            foreach (var bill in b.Bills)
            {
                exists = exists || (bill.Role == RecipeRole.ButcherFlesh && bill.Humanlike);
                animalBill[b.Id] = animalBill.GetValueOrDefault(b.Id) || (bill.Role == RecipeRole.ButcherFlesh && !bill.Humanlike);
            }
            var butcher = "";
            foreach (var recipe in b.Recipes)
            {
                if (recipe.Role == RecipeRole.ButcherFlesh && recipe.Available)
                {
                    butcher = recipe.Name;
                }
            }
            if (exists || butcher.Length == 0)
            {
                continue;
            }
            var (worker, ok) = QualifyingHumanButcher(b.HumanButchers, ideology);
            if (ok)
            {
                choices.Add(
                    new BillSelection
                    {
                        Bench = b.Id,
                        Recipe = butcher,
                        Mode = BillMode.HumanButcherForever,
                        Worker = worker.Value,
                    }
                );
            }
        }
        if (choices.Count == 0)
        {
            return (new BillSelection(), false);
        }
        var first = choices
            .OrderByDescending(c => animalBill.GetValueOrDefault(c.Bench))
            .ThenBy(c => c.Bench, StringComparer.Ordinal)
            .ThenBy(c => c.Worker, StringComparer.Ordinal)
            .First();
        return (first, true);
    }

    /// <summary>
    /// Whether the precept rule forbids the worker to butcher a human.
    /// Traits may cancel an unwilling effect.
    /// </summary>
    private static bool ButcherHeld(IdeologyMode ideology, EquatableArray<PawnTrait> traits)
    {
        var names = traits.Select(t => t.Name).ToArray();
        var subject = new PreceptSubject { Pawn = true, Traits = names };
        var stance = ideology.ActionStance(new PreceptAction { HistoryEvent = ButcheredHumanEvent }, subject).Stance;
        return stance is PreceptStance.Forbidden;
    }

    /// <summary>Separates the worker's disposition from the colony's response. Other pawns may still receive native butchery memories.</summary>
    public static bool HumanButcherEligible(EquatableArray<PawnTrait> traits, bool preceptAcceptable, bool canWork)
    {
        if (!canWork)
        {
            return false;
        }
        if (preceptAcceptable)
        {
            return true;
        }
        foreach (var trait in traits)
        {
            if (trait.Effects.HumanButcher)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Allocates the human meat to its destinations in order. Throws <see cref="FoodFactsException"/>.</summary>
    public static IReadOnlyList<HumanMeatAllocation> RouteHumanMeat(HumanMeatRouting r)
    {
        if (!FoodForecastRules.FoodNumber(r.Nutrition) || !FoodForecastRules.FoodNumber(r.FeedShortfall) || !FoodForecastRules.FoodNumber(r.EligibleMealDemand) || !FoodForecastRules.FoodNumber(r.VegetableNutrition))
        {
            throw new FoodFactsException();
        }
        var @out = new List<HumanMeatAllocation>();
        var remaining = r.Nutrition;
        void Allocate(HumanMeatRoute route, double amount)
        {
            amount = Math.Min(amount, remaining);
            if (amount > 0)
            {
                @out.Add(new HumanMeatAllocation(route, amount));
                remaining -= amount;
            }
        }
        Allocate(HumanMeatRoute.Feed, r.FeedShortfall);
        // Only nutrition beyond eligible diners' demand is trade surplus.
        var meal = Math.Min(remaining, r.EligibleMealDemand);
        if (r.CanMakeSurvivalMeals)
        {
            Allocate(HumanMeatRoute.SurvivalTrade, Math.Min(r.VegetableNutrition, remaining - meal));
        }
        Allocate(HumanMeatRoute.RawTrade, remaining - meal);
        Allocate(HumanMeatRoute.Meals, meal);
        return @out;
    }
}
