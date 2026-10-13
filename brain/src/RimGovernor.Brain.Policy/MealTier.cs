using System.Globalization;

namespace RimGovernor.Brain.Policy;

public enum MealTier
{
    [WireName("")]
    None = 0,
    Simple,
    Fine,
    Lavish,
    Paste,
}

public sealed record MealCook
{
    public PawnId Pawn { get; init; }
    public int Skill { get; init; }
}

/// <summary>
/// The food-plan owner's tick-consistent input. RawRunwayDays excludes prepared meals and reserves. Cooks contains
/// only allocated, available cooking workers; a skilled but unallocated pawn is not cooking capacity.
/// </summary>
public sealed record MealTierRequest
{
    public Fact<FoodPlan> Plan { get; init; }
    public Fact<double> RawRunwayDays { get; init; }
    public double MinDays { get; init; }
    public double TargetDays { get; init; }
    public Fact<EquatableArray<MealCook>> Cooks { get; init; }
    public Fact<bool> CookLaborConstrained { get; init; }
    public Fact<bool> HighExpectations { get; init; }
    public MealTier Previous { get; init; }
    public Fact<Infrastructure> Paste { get; init; }
    public Fact<ControlledEnvironment> Environment { get; init; }
}

public sealed record MealRecipeChoice
{
    public string Bench { get; init; } = "";
    public string Recipe { get; init; } = "";

    /// <summary>Whether the recipe is a bulk sibling.</summary>
    public bool Bulk { get; init; }

    public double Mood { get; init; }
    public double NutrientEfficiency { get; init; }
    public double WorkPerNutrition { get; init; }
}

/// <summary>
/// PasteNetwork is a power-feasible construction recommendation, never proof of connectivity, hopper stock or
/// native placement. Recipes contains only the chosen tier, including existing bills so callers can reconcile
/// owned work.
/// </summary>
public sealed record MealTierReview
{
    public MealTier Tier { get; init; }
    public IReadOnlyList<MealRecipeChoice> Recipes { get; init; } = [];
    public string PasteNetwork { get; init; } = "";
    public string Reason { get; init; } = "";
    public IReadOnlyList<CandidateTerm> Terms { get; init; } = [];

    public string Explain()
    {
        var s = $"meal tier={Wire.Name(Tier)} reason={Reason}";
        foreach (var term in Terms)
        {
            s += $" {term.Name}={term.Value.ToString("F3", CultureInfo.InvariantCulture)}";
        }
        if (PasteNetwork != "")
        {
            s += " paste_network=" + PasteNetwork;
        }
        return s;
    }
}

/// <summary>Go's <c>ErrMealTierFacts</c>.</summary>
public sealed class MealTierFactsException() : DomainException("meal tier inputs unavailable or invalid");

public static partial class PolicyFunctions
{
    /// <summary>
    /// Upgrades half a day above the target (or half the min/target gap when narrower), and retains an advanced
    /// tier until runway drops below target. Lavish additionally requires observed expectations pressure and strict
    /// surplus. Losing a cook or an ingredient source overrides the runway latch.
    /// </summary>
    public static MealTierReview ReviewMealTier(MealTierRequest r, Fact<EquatableArray<ProductionBench>> benches)
    {
        var pk = r.Plan.TryGetValue(out var plan);
        var dk = r.RawRunwayDays.TryGetValue(out var days);
        if (!pk || !dk || !FoodNumber(days) || !FoodNumber(r.MinDays) || !FieldPositive(r.TargetDays) || r.TargetDays > 60 || r.MinDays >= r.TargetDays || !ValidMealTier(r.Previous))
        {
            throw new MealTierFactsException();
        }
        var (sources, protein, ok) = MealIngredientSources(plan!);
        if (!ok)
        {
            throw new MealTierFactsException();
        }
        var ck = r.Cooks.TryGetValue(out var cooks);

        var skill = -1;
        var seen = new HashSet<PawnId>();
        foreach (var cook in cooks)
        {
            if (!FoodId(cook.Pawn.Value) || seen.Contains(cook.Pawn) || cook.Skill < 0 || cook.Skill > 20)
            {
                throw new MealTierFactsException();
            }
            seen.Add(cook.Pawn);
            if (cook.Skill > skill)
            {
                skill = cook.Skill;
            }
        }
        var margin = Math.Min(0.5, (r.TargetDays - r.MinDays) / 2);
        var review = new MealTierReview
        {
            Tier = MealTier.Simple,
            Reason = "raw surplus or qualified recipe unavailable",
            Terms = [new CandidateTerm("raw_runway_days", days), new CandidateTerm("upgrade_days", r.TargetDays + margin), new CandidateTerm("downgrade_days", r.TargetDays)],
        };
        var constrained = Positive(r.CookLaborConstrained) || (ck && cooks.Count == 0);
        var pasteNeeded = constrained || days < r.MinDays || (r.Previous == MealTier.Paste && days < r.MinDays + margin);
        if (pasteNeeded && sources!.Count > 0)
        {
            var (network, watts, known) = MealPasteNetwork(r);
            if (known)
            {
                return review with
                {
                    Tier = MealTier.Paste,
                    PasteNetwork = network,
                    Reason = "raw shortage or cooking labor constraint",
                    Terms = [.. review.Terms, new CandidateTerm("paste_power_w", watts)],
                };
            }
        }
        if (!benches.TryGetValue(out var rows) || !ck)
        {
            throw new MealTierFactsException();
        }
        var advanced = days >= r.TargetDays + margin || ((r.Previous == MealTier.Fine || r.Previous == MealTier.Lavish) && days >= r.TargetDays);
        var maxTier = MealTier.Simple;
        if (advanced && protein)
        {
            maxTier = MealTier.Fine;
            var lavishSurplus = days >= r.TargetDays + margin || (r.Previous == MealTier.Lavish && days > r.TargetDays);
            if (Positive(r.HighExpectations) && lavishSurplus)
            {
                maxTier = MealTier.Lavish;
            }
        }
        var seenBenches = new HashSet<string>(StringComparer.Ordinal);
        var best = 0;
        var chosen = new List<MealRecipeChoice>();
        var tierNow = review.Tier;
        foreach (var bench in rows)
        {
            if (!FoodId(bench.Id) || seenBenches.Contains(bench.Id))
            {
                throw new MealTierFactsException();
            }
            seenBenches.Add(bench.Id);
            if (bench.Butcher || !Positive(bench.Usable))
            {
                continue;
            }
            var seenRecipes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var recipe in bench.Recipes)
            {
                if (!FoodId(recipe.Name) || seenRecipes.Contains(recipe.Name))
                {
                    throw new MealTierFactsException();
                }
                seenRecipes.Add(recipe.Name);
                var (choice, tier, valid) = MealRecipeChoiceOf(bench.Id, recipe, skill, sources!);
                if (!valid || MealTierRank(tier) > MealTierRank(maxTier))
                {
                    continue;
                }
                var rank = MealTierRank(tier);
                if (rank < best)
                {
                    continue;
                }
                if (rank > best)
                {
                    chosen.Clear();
                    tierNow = tier;
                    best = rank;
                }
                chosen.Add(choice);
            }
        }
        // A bulk recipe cooks four meals per trip to the bench: with equal work per nutrition it wins, so one
        // campfire keeps up with a big colony.
        var ordered = chosen
            .OrderByDescending(a => a.NutrientEfficiency)
            .ThenBy(a => a.WorkPerNutrition)
            .ThenByDescending(a => a.Mood)
            .ThenByDescending(a => a.Bulk)
            .ThenBy(a => a.Recipe, StringComparer.Ordinal)
            .ThenBy(a => a.Bench, StringComparer.Ordinal)
            .ToList();
        review = review with { Tier = tierNow, Recipes = ordered };
        if (ordered.Count > 0)
        {
            review = review with { Reason = "qualified cook and retained ingredient sources" };
        }
        return review;
    }

    private static bool ValidMealTier(MealTier t) => t is MealTier.None or MealTier.Simple or MealTier.Fine or MealTier.Lavish or MealTier.Paste;

    internal static int MealTierRank(MealTier t) =>
        t switch
        {
            MealTier.Simple => 1,
            MealTier.Fine => 2,
            MealTier.Lavish => 3,
            _ => 0,
        };

    /// <summary>
    /// A proposed Open channel is not yet an ingredient source. Hold preserves an observed-open source; Close and
    /// Unknown never satisfy a recipe slot.
    /// </summary>
    private static (HashSet<FoodIngredientClass>? Sources, bool Protein, bool Ok) MealIngredientSources(FoodPlan p)
    {
        var sources = new HashSet<FoodIngredientClass>();
        if (!FieldPositive(p.DemandPerDay) || !FoodNumber(p.DeliveredPerDay) || double.IsNaN(p.GapPerDay) || double.IsInfinity(p.GapPerDay))
        {
            return (null, false, false);
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var protein = false;
        foreach (var e in p.Portfolio)
        {
            var c = e.Channel;
            var key = Wire.Name(c.Kind) + "/" + c.Id;
            if (!ValidFoodChannelKind(c.Kind) || !FoodId(c.Id) || seen.Contains(key) || !FoodNumber(e.DeliveredPerDay) || (e.Decision != FoodPlanDecision.Open && e.Decision != FoodPlanDecision.Hold && e.Decision != FoodPlanDecision.Close))
            {
                return (null, false, false);
            }
            seen.Add(key);
            if (e.Decision == FoodPlanDecision.Close || !Positive(c.Open()) || (e.DeliveredPerDay <= 0 && !MealStockAvailable(c)))
            {
                continue;
            }
            switch (c.Kind)
            {
                case CandidateKind.Forage:
                case CandidateKind.Crop:
                    sources.Add(FoodIngredientClass.Vegetable);
                    break;
                case CandidateKind.Hunt:
                case CandidateKind.Corpse:
                    sources.Add(FoodIngredientClass.Meat);
                    protein = true;
                    break;
                case CandidateKind.AnimalProduct:
                    sources.Add(FoodIngredientClass.AnimalProduct);
                    protein = true;
                    break;
                case CandidateKind.Fishing:
                    sources.Add(FoodIngredientClass.Meat);
                    break;
            }
        }
        return (sources, protein, true);
    }

    private static (MealRecipeChoice Choice, MealTier Tier, bool Valid) MealRecipeChoiceOf(string bench, ProductionRecipe r, int skill, HashSet<FoodIngredientClass> sources)
    {
        var choice = new MealRecipeChoice { Bench = bench, Recipe = r.Name, Bulk = r.Bulk };
        var fk = r.CookSkillFloor.TryGetValue(out var floor);
        var mk = r.Mood.TryGetValue(out var mood);
        var ek = r.NutrientEfficiency.TryGetValue(out var efficiency);
        var wk = r.WorkPerNutrition.TryGetValue(out var work);
        var powerKnown = r.NeedsPower.IsKnown;
        var ik = r.IngredientClasses.TryGetValue(out var classes);
        if (!Positive(r.Available) || !fk || floor < 0 || floor > 20 || skill < floor || !mk || !FoodNumber(mood) || !ek || !FieldPositive(efficiency) || !wk || !FoodNumber(work) || !powerKnown || !ik || !MealSlotsSupported(classes!, sources) || r.Products.Count != 1)
        {
            return (choice, MealTier.None, false);
        }
        var product = r.Products[0];
        var nk = product.Nutrition.TryGetValue(out var nutrition);
        if (!Positive(product.Edible) || !nk || !FieldPositive(nutrition))
        {
            return (choice, MealTier.None, false);
        }
        var tier = MealTier.Simple;
        if (mood >= 12)
        {
            tier = MealTier.Lavish;
        }
        else if (mood >= 5)
        {
            tier = MealTier.Fine;
        }
        return (choice with { Mood = mood, NutrientEfficiency = efficiency, WorkPerNutrition = work }, tier, true);
    }

    internal static bool MealSlotsSupported(IReadOnlyList<FoodIngredientSlot> slots, HashSet<FoodIngredientClass> sources)
    {
        if (slots.Count == 0)
        {
            return false;
        }
        foreach (var slot in slots)
        {
            if (slot.Alternatives.Count == 0 || slot.Alternatives.Count > 3)
            {
                return false;
            }
            var seen = new HashSet<FoodIngredientClass>();
            var supported = false;
            foreach (var kind in slot.Alternatives)
            {
                if (!seen.Add(kind))
                {
                    return false;
                }
                switch (kind)
                {
                    case FoodIngredientClass.Any:
                        if (slot.Alternatives.Count != 1)
                        {
                            return false;
                        }
                        supported = sources.Count > 0;
                        break;
                    case FoodIngredientClass.Meat:
                    case FoodIngredientClass.Vegetable:
                    case FoodIngredientClass.AnimalProduct:
                        supported = supported || sources.Contains(kind);
                        break;
                    default:
                        return false;
                }
            }
            if (!supported)
            {
                return false;
            }
        }
        return true;
    }

    private static (string Network, double Watts, bool Known) MealPasteNetwork(MealTierRequest r)
    {
        var ik = r.Paste.TryGetValue(out var infra);
        var ek = r.Environment.TryGetValue(out var env);
        if (!ik || !ek || !infra!.PowerW.TryGetValue(out var watts) || !FoodId(infra.Name) || !Positive(infra.Available) || !FieldPositive(watts))
        {
            return ("", 0, false);
        }
        var best = "";
        var headroom = 0.0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var network in env!.Networks)
        {
            if (!FoodId(network.Id) || seen.Contains(network.Id))
            {
                return ("", 0, false);
            }
            seen.Add(network.Id);
            if (!network.CalmNightHeadroomW().TryGetValue(out var margin) || !Positive(network.ActiveSource) || margin < watts)
            {
                continue;
            }
            if (best == "" || margin > headroom || (margin == headroom && string.CompareOrdinal(network.Id, best) < 0))
            {
                (best, headroom) = (network.Id, margin);
            }
        }
        return (best, watts, best != "");
    }

    private static (BillSelection Selection, bool Ok) SelectMealBill(
        Fact<EquatableArray<ProductionBench>> benches,
        Fact<long> colonists,
        MealTierRequest request,
        IReadOnlyList<string> ingredients
    )
    {
        MealTierReview review;
        try
        {
            review = ReviewMealTier(request, benches);
        }
        catch (MealTierFactsException)
        {
            return (new BillSelection(), false);
        }
        if (review.Tier == MealTier.Paste || review.Recipes.Count == 0)
        {
            return (new BillSelection(), false);
        }
        benches.TryGetValue(out var rows);

        var byBench = new Dictionary<string, ProductionBench>(StringComparer.Ordinal);
        var recipes = new Dictionary<(string Bench, string Recipe), ProductionRecipe>();
        var existing = new HashSet<(string Bench, string Recipe)>();
        foreach (var bench in rows)
        {
            if (bench.Bills.Count > 15)
            {
                return (new BillSelection(), false);
            }
            byBench[bench.Id] = bench;
            foreach (var recipe in bench.Recipes)
            {
                recipes[(bench.Id, recipe.Name)] = recipe;
            }
            foreach (var bill in bench.Bills)
            {
                existing.Add((bench.Id, bill.Recipe));
            }
        }
        // A standing bill of a chosen recipe that already does the work ends the review; a suspended or undersized
        // one is replaced. Whoever wrote the bill, Auto plans the tier fresh: an older-tier meal bill on any
        // cooking bench is retired by the same replacement, ownership or not.
        if (!colonists.TryGetValue(out var count))
        {
            return (new BillSelection(), false);
        }
        var wanted = new BillSelection { Ingredients = ingredients, Mode = BillMode.FoodTarget, Target = (int)(count * 3) };
        // Correct drift even when another matching bill already covers the target.
        foreach (var choice in review.Recipes)
        {
            var bench = byBench.GetValueOrDefault(choice.Bench) ?? new ProductionBench();
            bench = bench with { Recipes = [recipes.GetValueOrDefault((choice.Bench, choice.Recipe)) ?? new ProductionRecipe()] };
            var (selected, ok) = SelectProductionBill(CookFood, Known<EquatableArray<ProductionBench>>([bench]), colonists, request.RawRunwayDays, Unknown<double>(), request.TargetDays, new ProductionBillContext { Ingredients = ingredients });
            if (ok && selected.Replace != "")
            {
                return (selected, true);
            }
        }
        foreach (var choice in review.Recipes)
        {
            var bench = byBench.GetValueOrDefault(choice.Bench);
            foreach (var bill in bench?.Bills ?? [])
            {
                if (bill.Recipe == choice.Recipe && !bill.Humanlike && BillAdequate(bill, wanted))
                {
                    return (new BillSelection(), false);
                }
            }
        }
        foreach (var choice in review.Recipes)
        {
            var bench = byBench.GetValueOrDefault(choice.Bench) ?? new ProductionBench();
            bench = bench with { Recipes = [recipes.GetValueOrDefault((choice.Bench, choice.Recipe)) ?? new ProductionRecipe()] };
            var (selected, ok) = SelectProductionBill(CookFood, Known<EquatableArray<ProductionBench>>([bench]), colonists, request.RawRunwayDays, Unknown<double>(), request.TargetDays, new ProductionBillContext { Ingredients = ingredients });
            if (!ok)
            {
                if (existing.Contains((choice.Bench, choice.Recipe)))
                {
                    return (new BillSelection(), false);
                }
                continue;
            }
            if (selected.Replace == "")
            {
                var replace = "";
                foreach (var existingBench in rows)
                {
                    foreach (var old in existingBench.Bills)
                    {
                        if (old.Id != "" && old.Recipe != selected.Recipe && old.Role == RecipeRole.OrdinaryMeal && (replace == "" || string.CompareOrdinal(old.Id, replace) < 0))
                        {
                            replace = old.Id;
                        }
                    }
                }
                selected = selected with { Replace = replace };
            }
            return (selected, true);
        }
        return (new BillSelection(), false);
    }
}
