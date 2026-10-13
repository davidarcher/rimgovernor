namespace RimGovernor.Brain.Policy;

public sealed record HumanButcherCandidate
{
    public PawnId ID { get; init; }
    public Fact<IReadOnlyList<PawnTrait>> Traits { get; init; }

    /// <summary>The pawn's own ideoligion and precept defNames from its row; unknown when the row carries no policy inputs.</summary>
    public Fact<HeldPrecepts> Held { get; init; }

    public Fact<bool> CanWork { get; init; }
}

/// <summary>A pawn's ideoligion load id (empty without one) and the precept defNames of it.</summary>
public sealed record HeldPrecepts
{
    public string Ideo { get; init; } = "";
    public IReadOnlyList<string> Defs { get; init; } = [];
}

/// <summary>Where a stranger corpse (raider, prisoner, visitor) goes.</summary>
public enum StrangerRoute
{
    [WireName("butcher")]
    Butcher,

    [WireName("incinerate")]
    Incinerate,
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

public static partial class PolicyFunctions
{
    /// <summary>The HistoryEventDef a butchered human raises (HistoryEventDefOf.ButcheredHuman); the precept rule answers by it.</summary>
    public const string ButcheredHumanEvent = "ButcheredHuman";

    /// <summary>
    /// Whether the pawn's ideoligion costs no mood for butchering a human: it has one, and none of its precepts
    /// has a self-took-action effect on ButcheredHuman with a mood-losing stage. A pawn without an ideoligion is
    /// not acceptable (only a trait qualifies it); with Ideology absent no precept exists to object. Unknown while
    /// the pawn row or the ideoligion defs are unread.
    /// </summary>
    public static Fact<bool> PreceptAcceptable(Fact<HeldPrecepts> held, IdeologyRead ideology)
    {
        if (ideology.Absent())
        {
            return Known(true);
        }
        if (!held.TryGetValue(out var h))
        {
            return Unknown<bool>();
        }
        if (h.Ideo.Length == 0)
        {
            return Known(false);
        }
        if (!ideology.Ideology.TryGetValue(out var i))
        {
            return Unknown<bool>();
        }
        foreach (var inForce in i.HeldBy(h.Defs).EffectsInForce())
        {
            var e = inForce.Effect;
            if (e.Kind == PreceptEffectKind.SelfTookAction && e.HistoryEvent == ButcheredHumanEvent && e.Penalises())
            {
                return Known(false);
            }
        }
        return Known(true);
    }

    /// <summary>
    /// Picks the lowest-id eligible worker whose ideoligion does not forbid butchering humans. An unread
    /// ideoligion with Ideology installed holds; without the expansion nothing is forbidden and the native
    /// disposition still gates the worker.
    /// </summary>
    public static (PawnId Worker, bool Ok) QualifyingHumanButcher(IReadOnlyList<HumanButcherCandidate> rows, IdeologyRead ideology)
    {
        var selected = default(PawnId);
        var seen = new HashSet<PawnId>();
        foreach (var row in rows)
        {
            if (!FoodId(row.ID.Value) || !seen.Add(row.ID))
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
    public static (BillSelection Selection, bool Ok) SelectHumanButcher(Fact<IReadOnlyList<ProductionBench>> benches, IdeologyRead ideology)
    {
        if (!benches.TryGetValue(out var rows))
        {
            return (new BillSelection(), false);
        }
        var choices = new List<BillSelection>();
        var animalBill = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var b in rows)
        {
            var uk = b.Usable.TryGetValue(out var usable);
            var nk = b.HumanCorpseNutrition.TryGetValue(out var nutrition);
            var tk = b.Token.TryGetValue(out var token);
            if (!b.Butcher || !uk || !usable || !nk || !FoodNumber(nutrition) || nutrition <= 0 || !tk || !FoodId(token!) || !FoodId(b.ID) || b.Bills.Count >= 15)
            {
                continue;
            }
            var exists = false;
            foreach (var bill in b.Bills)
            {
                exists = exists || (bill.Role == RecipeRole.ButcherFlesh && bill.Humanlike);
                animalBill[b.ID] = animalBill.GetValueOrDefault(b.ID) || (bill.Role == RecipeRole.ButcherFlesh && !bill.Humanlike);
            }
            var butcher = "";
            foreach (var recipe in b.Recipes)
            {
                if (recipe.Role == RecipeRole.ButcherFlesh && recipe.Available.TryGetValue(out var v) && v)
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
                        Bench = b.ID,
                        Recipe = butcher,
                        Token = token!,
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
    /// Whether the precept rule forbids the worker to butcher a human, or cannot say (unread ideoligion, Ideology
    /// installed). Traits cancel an unwilling effect; unknown traits cancel none.
    /// </summary>
    private static bool ButcherHeld(IdeologyRead ideology, Fact<IReadOnlyList<PawnTrait>> traits)
    {
        var names = new List<string>();
        if (traits.TryGetValue(out var rows))
        {
            foreach (var t in rows)
            {
                names.Add(t.Name);
            }
        }
        var subject = new PreceptSubject { Pawn = true, Traits = names };
        var stance = ideology.ActionStance(new PreceptAction { HistoryEvent = ButcheredHumanEvent }, subject).Stance;
        return stance is PreceptStance.Forbidden or PreceptStance.Unknown;
    }

    /// <summary>Separates the worker's disposition from the colony's response. Other pawns may still receive native butchery memories.</summary>
    public static bool HumanButcherEligible(Fact<IReadOnlyList<PawnTrait>> traits, Fact<bool> preceptAcceptable, Fact<bool> canWork)
    {
        if (!canWork.TryGetValue(out var works) || !works)
        {
            return false;
        }
        if (preceptAcceptable.TryGetValue(out var acceptable) && acceptable)
        {
            return true;
        }
        if (!traits.TryGetValue(out var rows))
        {
            return false;
        }
        foreach (var trait in rows)
        {
            if (trait.Effects.HumanButcher)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Butchers a corpse that is still fresh while butchery is open and burns every other in the incinerator: a
    /// rotting or desiccated one is never hauled to the butchery. An unread rot stage is not spoiled.
    /// </summary>
    public static StrangerRoute RouteStranger(RotStage rot, bool butcheryOpen) =>
        butcheryOpen && !rot.Spoiled() ? StrangerRoute.Butcher : StrangerRoute.Incinerate;

    /// <summary>
    /// Whether the existing human-butcher gate would take a stranger corpse now: a usable butchery whose recipe is
    /// available, a worker who qualifies (QualifyingHumanButcher); the morgue holds the corpse. It ignores whether
    /// the human bill stands.
    /// </summary>
    public static bool HumanButcheryOpen(Fact<IReadOnlyList<ProductionBench>> benches, IdeologyRead ideology)
    {
        if (!benches.TryGetValue(out var rows))
        {
            return false;
        }
        foreach (var b in rows)
        {
            var uk = b.Usable.TryGetValue(out var usable);
            var nk = b.HumanCorpseNutrition.TryGetValue(out var nutrition);
            if (!b.Butcher || !uk || !usable || !nk || !FoodNumber(nutrition) || nutrition <= 0)
            {
                continue;
            }
            var available = false;
            foreach (var recipe in b.Recipes)
            {
                available = available || (recipe.Role == RecipeRole.ButcherFlesh && recipe.Available.TryGetValue(out var v) && v);
            }
            var (_, ok) = QualifyingHumanButcher(b.HumanButchers, ideology);
            if (ok && available)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Allocates the human meat to its destinations in order. Throws <see cref="FoodFactsException"/> where Go returned ErrFoodFacts.</summary>
    public static IReadOnlyList<HumanMeatAllocation> RouteHumanMeat(HumanMeatRouting r)
    {
        if (!FoodNumber(r.Nutrition) || !FoodNumber(r.FeedShortfall) || !FoodNumber(r.EligibleMealDemand) || !FoodNumber(r.VegetableNutrition))
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
