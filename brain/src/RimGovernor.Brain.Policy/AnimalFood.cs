namespace RimGovernor.Brain.Policy;

// Go: animal_food.go

/// <summary>
/// Compares a seasonal horizon with the native pen's worst-season pasture rate and stored feed. It does not add
/// grass to the human food ledger.
/// </summary>
public sealed record PenGrazing
{
    public string ID { get; init; } = "";

    public Fact<double> DemandPerDay { get; init; }

    public Fact<double> PasturePerDay { get; init; }

    public Fact<double> StoredNutrition { get; init; }
}

/// <summary>Native's raw per-animal flags the slaughter exclusion reads; Designatable is Designator_Slaughter's own acceptance.</summary>
public sealed record SlaughterFacts
{
    public Fact<bool> Downed { get; init; }

    public Fact<bool> InMentalState { get; init; }

    public Fact<bool> Pregnant { get; init; }

    public Fact<bool> Mastered { get; init; }

    public Fact<bool> ColonistBonded { get; init; }

    public Fact<bool> Designatable { get; init; }
}

public sealed record SlaughterFoodAnimal
{
    public PawnId ID { get; init; }

    public Resource Race { get; init; }

    public Fact<double> MeatNutrition { get; init; }

    public Fact<double> FeedPerDay { get; init; }

    public Fact<double> ReproductionDays { get; init; }
}

public static class AnimalFoodExtensions
{
    /// <summary>
    /// The slaughter exclusion: a downed, mentally broken, pregnant, mastered, colonist-bonded or
    /// release-designated animal, or one the slaughter designator refuses, is protected. A known blocker decides
    /// even when another fact is unknown; otherwise unknown facts leave it unknown.
    /// </summary>
    public static Fact<bool> SafeToSlaughter(this UpkeepAnimal a)
    {
        var f = a.SlaughterFacts;
        var unknown = false;
        foreach (var blocker in new[] { f.Downed, f.InMentalState, f.Pregnant, f.Mastered, f.ColonistBonded, a.Release })
        {
            if (!blocker.TryGetValue(out var v))
            {
                unknown = true;
            }
            else if (v)
            {
                return Known(false);
            }
        }

        if (!f.Designatable.TryGetValue(out var designatable))
        {
            unknown = true;
        }
        else if (!designatable)
        {
            return Known(false);
        }

        return unknown ? Unknown<bool>() : Known(true);
    }

    /// <summary>
    /// The release exclusion policy owns: the slaughter protections (downed, mentally broken, pregnant,
    /// mastered, colonist-bonded) or an existing release or slaughter designation protect an animal, and
    /// SafeToRelease is Designator_ReleaseAnimalToWild's own acceptance. A known blocker decides even when
    /// another fact is unknown.
    /// </summary>
    public static Fact<bool> ReleaseAllowed(this UpkeepAnimal a)
    {
        var f = a.SlaughterFacts;
        var unknown = false;
        foreach (var blocker in new[] { f.Downed, f.InMentalState, f.Pregnant, f.Mastered, f.ColonistBonded, a.Release, a.Slaughter })
        {
            if (!blocker.TryGetValue(out var v))
            {
                unknown = true;
            }
            else if (v)
            {
                return Known(false);
            }
        }

        if (!a.SafeToRelease.TryGetValue(out var safe))
        {
            unknown = true;
        }
        else if (!safe)
        {
            return Known(false);
        }

        return unknown ? Unknown<bool>() : Known(true);
    }
}

public static partial class PolicyFunctions
{
    public static Fact<double> HayNutritionNeed(Fact<EquatableArray<PenGrazing>> pens, Fact<double> gap)
    {
        var known = pens.TryGetValue(out var rows);
        var dk0 = gap.TryGetValue(out var days);
        if (!known || !dk0 || !FoodNumber(days))
        {
            return Unknown<double>();
        }

        var need = 0.0;
        foreach (var p in rows)
        {
            var dk = p.DemandPerDay.TryGetValue(out var d);
            var yk = p.PasturePerDay.TryGetValue(out var y);
            var sk = p.StoredNutrition.TryGetValue(out var s);
            if (!dk || !yk || !sk || !FoodNumber(d) || !FoodNumber(y) || !FoodNumber(s))
            {
                return Unknown<double>();
            }

            need += Math.Max(0, (days * (d - y)) - s);
        }

        return Known(need);
    }

    /// <summary>
    /// Sizes a hay field without declaring hay human-edible; the caller sites it in the layout plan's field
    /// blocks. Existing hay capacity is subtracted by the caller; unknown or nonnegative grazing balance never
    /// opens a field.
    /// </summary>
    public static bool PlanHayField(Fact<double> need, CropChoice crop, CropClimate climate, out FieldPlan plan)
    {
        var nk = need.TryGetValue(out var n);
        var yk = crop.HarvestNutrition.TryGetValue(out var yield);
        var dk = crop.GrowDays.TryGetValue(out var days);
        var sk = climate.SowingOutdoors().TryGetValue(out var sowing);
        var ck = climate.DaysRemaining.TryGetValue(out var season);
        var ak = crop.Available.TryGetValue(out var available);
        if (
            !nk
            || !FoodNumber(n)
            || n <= 0
            || !IsHayCrop(crop)
            || !yk
            || !FieldPositive(yield)
            || !dk
            || !FieldPositive(days)
            || !sk
            || !sowing
            || !ck
            || season < days * FieldCycles
            || !ak
            || !available
        )
        {
            plan = new FieldPlan();
            return false;
        }

        crop = crop with { Edible = Known(false) };
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
        Fact<EquatableArray<UpkeepAnimal>> animals,
        HerdPolicy herd
    )
    {
        if (!animals.TryGetValue(out var observedFact))
        {
            return [];
        }

        var pending = new HashSet<PawnId>();

        // Re-price existing orders too. These copies are planning offers only; fresh destructive admission
        // still requires SafeToSlaughter.
        var observed = new List<UpkeepAnimal>();
        foreach (var original in observedFact)
        {
            var a = original;
            if (a.Slaughter.TryGetValue(out var slaughter) && slaughter)
            {
                pending.Add(a.ID);
                a = a with { Slaughter = Known(false), SlaughterFacts = a.SlaughterFacts with { Designatable = Known(true) } };
            }

            observed.Add(a);
        }

        var floors = HerdFoodLimits(observed, herd);
        var (candidates, unknown) = HerdSurplusCandidates(observed, floors, herd.FeedShort, herd);
        if (unknown)
        {
            return [];
        }

        var safe = new Dictionary<PawnId, bool>();
        foreach (var r in candidates)
        {
            safe[r.Animal.ID] = r.Method == HusbandryMethod.Slaughter && !r.Animal.Herd.FoodBarredOrUnknown();
        }

        var counts = new Dictionary<Resource, long>();
        foreach (var a in observed)
        {
            if (a.Release.TryGetValue(out var release) && !release)
            {
                counts[a.Definition] = counts.GetValueOrDefault(a.Definition) + 1;
            }
        }

        foreach (var a in observed)
        {
            if (
                pending.Contains(a.ID)
                && a.Release.TryGetValue(out var release)
                && !release
                && counts.GetValueOrDefault(a.Definition) > floors.GetValueOrDefault(a.Definition)
                && !a.Herd.FoodBarredOrUnknown()
            )
            {
                safe[a.ID] = true;
            }
        }

        var choices = new List<ScoredSlaughter>();
        foreach (var a in rows)
        {
            var nk = a.MeatNutrition.TryGetValue(out var n);
            var fk = a.FeedPerDay.TryGetValue(out var feed);
            var dk = a.ReproductionDays.TryGetValue(out var days);
            if (!safe.GetValueOrDefault(a.ID) || !nk || !fk || !dk || !FieldPositive(n) || !FieldPositive(feed) || !FieldPositive(days))
            {
                continue;
            }

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

        var c = FoodCandidate(CandidateKind.Slaughter, "slaughter:" + best.Animal.ID, Unknown<double>());
        var yields = c.Yields.Items.ToBuilder();
        yields[0] = yields[0] with { StockCap = Known((long)meat) };
        c = c with
        {
            Yields = yields.ToImmutable(),
            LaborPerDay = Known(0.0),
            UpfrontCost = c.UpfrontCost with { LaborTicks = Known(180.0) },
            LeadDays = Known(0.0),
            State = FoodState(Known(false), Known(false)),
            Terms = EquatableArray.From([new CandidateTerm("meat_per_daily_feed", best.Efficiency), new CandidateTerm("reproduction_days", best.Reproduction)]),
        };
        return [c];
    }

    public static HusbandryChoice FoodSlaughterChoice(Fact<FoodPlan> plan, Fact<EquatableArray<UpkeepAnimal>> animals, HerdPolicy herd)
    {
        var pk = plan.TryGetValue(out var p);
        var rk = animals.TryGetValue(out var rows);
        if (!pk || !rk)
        {
            return new HusbandryChoice { Reason = HusbandryReason.Unknown };
        }

        var (safe, unknown) = HerdSurplusCandidates(rows, HerdFoodLimits(rows, herd), herd.FeedShort, herd);
        if (unknown)
        {
            return new HusbandryChoice { Reason = HusbandryReason.Unknown };
        }

        foreach (var e in p.Portfolio)
        {
            if (e.Channel.Kind != CandidateKind.Slaughter || e.Decision != FoodPlanDecision.Open || !e.Channel.ID.StartsWith("slaughter:", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var r in safe)
            {
                if (r.Method == HusbandryMethod.Slaughter && e.Channel.ID == "slaughter:" + r.Animal.ID)
                {
                    return new HusbandryChoice { Animal = r.Animal.ID, Method = HusbandryMethod.Slaughter };
                }
            }
        }

        return new HusbandryChoice { Reason = HusbandryReason.NoDeficit };
    }
}
