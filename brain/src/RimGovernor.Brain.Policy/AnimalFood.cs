using O = RimGovernor.Contracts.Wire.Observations;
namespace RimGovernor.Brain.Policy;


/// <summary>
/// What an animal eats that the colony's food math counts: Plants at PerDay (its race's adult feed scaled by its
/// life stage's hunger), or NoPlants for a race that eats no plants or has no food need.
/// </summary>
public abstract record AnimalFeed
{
    private protected AnimalFeed() { }

    public sealed record NoPlants : AnimalFeed;

    public sealed record Plants(double PerDay) : AnimalFeed;
}

/// <summary>How often a race renews itself: Every so many days (its egg interval, else its gestation), or Never.</summary>
public abstract record AnimalBreeding
{
    private protected AnimalBreeding() { }

    public sealed record Never : AnimalBreeding;

    public sealed record Every(double Days) : AnimalBreeding;
}

/// <summary>One animal whose meat the colony eats: the nutrition its live meat yields, what it eats and how often its race renews.</summary>
public sealed record SlaughterFoodAnimal
{
    public required O.FoodSlaughterAnimal Observed { get; init; }

    public PawnId ID => new(Observed.PawnId);

    public double MeatNutrition { get; init; }

    public AnimalFeed Feed { get; init; } = new AnimalFeed.NoPlants();

    public AnimalBreeding Breeding { get; init; } = new AnimalBreeding.Never();
}

public static class AnimalFoodExtensions
{
    /// <summary>
    /// The slaughter exclusion: a downed, mentally broken, pregnant, mastered, colonist-bonded or
    /// release-designated animal, or one the slaughter designator refuses, is protected.
    /// </summary>
    public static bool SafeToSlaughter(this UpkeepAnimal a) =>
        !a.Release && !a.State.Protected() && a.State.SlaughterDesignatable;

    /// <summary>
    /// The release exclusion policy owns: the slaughter protections (downed, mentally broken, pregnant,
    /// mastered, colonist-bonded) or an existing release or slaughter designation protect an animal, and
    /// SafeToRelease is Designator_ReleaseAnimalToWild's own acceptance.
    /// </summary>
    public static bool ReleaseAllowed(this UpkeepAnimal a) =>
        !a.Release && !a.Slaughter && !a.State.Protected() && a.SafeToRelease;

    /// <summary>A downed, mentally broken, pregnant, mastered or colonist-bonded animal.</summary>
    private static bool Protected(this O.AnimalState f) =>
        f.Downed || f.InMentalState || f.Pregnancy is not O.AnimalPregnancy.NotPregnant || f.MasterId.Length != 0 || f.ColonistBonded;
}

public static class AnimalFoodRules
{
    public static double HayNutritionNeed(EquatableArray<PenGrazing> rows, double gap)
    {
        var days = gap;
        if (!FoodForecastRules.FoodNumber(days))
        {
            throw new RequiredFactException("hay nutrition inputs");
        }

        var need = 0.0;
        foreach (var p in rows)
        {
            if (!FoodForecastRules.FoodNumber(p.DemandPerDay) || !FoodForecastRules.FoodNumber(p.PasturePerDay) || !FoodForecastRules.FoodNumber(p.StoredNutrition))
            {
                throw new RequiredFactException("hay nutrition inputs");
            }

            need += Math.Max(0, (days * (p.DemandPerDay - p.PasturePerDay)) - p.StoredNutrition);
        }

        return need;
    }

    /// <summary>
    /// Sizes a hay field without declaring hay human-edible; the caller sites it in the layout plan's field
    /// blocks. Existing hay capacity is subtracted by the caller; unknown or nonnegative grazing balance never
    /// opens a field.
    /// </summary>
    public static bool PlanHayField(double need, CropChoice crop, CropClimate climate, out FieldPlan plan)
    {
        var n = need;
        var yield = crop.HarvestNutrition;
        var days = crop.GrowDays;
        var sowing = climate.SowingOutdoors();
        var season = climate.DaysRemaining;
        var available = crop.Available;
        if (
            !FoodForecastRules.FoodNumber(n)
            || n <= 0
            || !SocialCropsRules.IsHayCrop(crop)
            || !FieldCapacityRules.FieldPositive(yield)
            || !FieldCapacityRules.FieldPositive(days)
            || !sowing
            || season < days * CalendarRules.FieldCycles
            || !available
        )
        {
            plan = new FieldPlan();
            return false;
        }

        crop = crop with { Edible = false};
        plan = new FieldPlan { Crop = crop, Needed = (int)Math.Ceiling(n / yield) };
        return true;
    }

    private sealed record ScoredSlaughter(SlaughterFoodAnimal Animal, double Nutrition, double Efficiency, double Reproduction);

    /// <summary>
    /// Offers one safe animal above the protected population as a one-shot channel: its meat is the stock cap,
    /// lead 0, the slaughter work upfront. Order uses meat per feed/day first, then shorter reproduction
    /// interval. The ledger still charges the ordinary slaughter work and can decline the offer.
    /// </summary>
    public static IReadOnlyList<SupplyCandidate> SlaughterFoodChannels(
        IEnumerable<SlaughterFoodAnimal> rows,
        EquatableArray<UpkeepAnimal> animals,
        HerdPolicy herd
    )
    {
        var pending = new HashSet<PawnId>();

        // Re-price existing orders too. These copies are planning offers only; fresh destructive admission
        // still requires SafeToSlaughter.
        var observed = new List<UpkeepAnimal>();
        foreach (var original in animals)
        {
            var a = original;
            if (a.Slaughter)
            {
                pending.Add(a.Id);
                a = a with { Pawn = a.Pawn with { AnimalState = new O.PawnAnimalState.Animal(a.State with { Slaughter = false, SlaughterDesignatable = true }) } };
            }

            observed.Add(a);
        }

        var floors = HerdSurplusRules.HerdFoodLimits(observed, herd);
        var candidates = HerdSurplusRules.HerdSurplusCandidates(observed, floors, herd.FeedShort, herd);
        var safe = new Dictionary<PawnId, bool>();
        foreach (var r in candidates)
        {
            safe[r.Animal.Id] = r.Method == HusbandryMethod.Slaughter && !r.Animal.Herd.FoodBarred();
        }

        var counts = new Dictionary<Resource, long>();
        foreach (var a in observed)
        {
            if (!a.Release)
            {
                counts[a.Definition] = counts.GetValueOrDefault(a.Definition) + 1;
            }
        }

        foreach (var a in observed)
        {
            if (
                pending.Contains(a.Id)
                && !a.Release
                && counts.GetValueOrDefault(a.Definition) > floors.GetValueOrDefault(a.Definition)
                && !a.Herd.FoodBarred()
            )
            {
                safe[a.Id] = true;
            }
        }

        var choices = new List<ScoredSlaughter>();
        foreach (var a in rows)
        {
            // Only a plant eater that breeds is scored: meat per daily feed, then the shorter renewal.
            if (!safe.GetValueOrDefault(a.ID) || a.Feed is not AnimalFeed.Plants { PerDay: var feed } || a.Breeding is not AnimalBreeding.Every { Days: var days }
                || !FieldCapacityRules.FieldPositive(a.MeatNutrition) || !FieldCapacityRules.FieldPositive(feed) || !FieldCapacityRules.FieldPositive(days))
            {
                continue;
            }

            var n = a.MeatNutrition;
            choices.Add(new ScoredSlaughter(a, n, n / feed, days));
        }

        choices = PolicySort.ByLess(
            choices,
            (a, b) =>
            {
                if (pending.Contains(a.Animal.ID) != pending.Contains(b.Animal.ID))
                {
                    return pending.Contains(a.Animal.ID);
                }

                if (a.Efficiency != b.Efficiency)
                {
                    return a.Efficiency > b.Efficiency;
                }

                if (a.Reproduction != b.Reproduction)
                {
                    return a.Reproduction < b.Reproduction;
                }

                return a.Animal.ID < b.Animal.ID;
            }
        );
        if (choices.Count == 0)
        {
            return [];
        }

        var best = choices[0];

        // JobDriver_Slaughter.SlaughterDuration is 180 ticks. Butchering and hauling remain separate native
        // jobs, as for the hunt channel.
        var meat = Math.Floor(best.Nutrition);
        if (meat < 1)
        {
            return [];
        }

        var c = CandidateRules.FoodCandidate(CandidateKind.Slaughter, "slaughter:" + best.Animal.ID, new YieldSupply.Stock((long)meat), CandidateState.Closed, new CandidateLead.Days(0), new CandidateWork.PerDay(0));
        c = c with
        {
            UpfrontCost = c.UpfrontCost with { LaborTicks = 180.0 },
            Terms = EquatableArray.From([new CandidateTerm("meat_per_daily_feed", best.Efficiency), new CandidateTerm("reproduction_days", best.Reproduction)]),
        };
        return [c];
    }

    public static HusbandryChoice FoodSlaughterChoice(FoodPlan plan, EquatableArray<UpkeepAnimal> animals, HerdPolicy herd)
    {
        var safe = HerdSurplusRules.HerdSurplusCandidates(animals, HerdSurplusRules.HerdFoodLimits(animals, herd), herd.FeedShort, herd);
        foreach (var e in plan.Portfolio)
        {
            if (e.Channel.Kind != CandidateKind.Slaughter || e.Decision != FoodPlanDecision.Open || !e.Channel.Id.StartsWith("slaughter:", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var r in safe)
            {
                if (r.Method == HusbandryMethod.Slaughter && e.Channel.Id == "slaughter:" + r.Animal.Id)
                {
                    return new HusbandryChoice.Write(r.Animal.Id, HusbandryMethod.Slaughter);
                }
            }
        }

        return new HusbandryChoice.NoDeficit();
    }
}
