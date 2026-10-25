using Sex = RimGovernor.Brain.GameFacts.Defs.Gender;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// The catalog and policy inputs the herd Need sizes and culls by. Native age, sickness, adulthood,
/// veneration and life stages remain on UpkeepAnimal.State. Feed uses race and life stage; MeatNutrition merges in
/// from the colony food channels by animal ID; SlaughterBarred and EatingBarred merge in from the ideoligion
/// (<see cref="HerdPreceptsRules.ApplyHerdPrecepts"/>). A barred slaughter leaves removal, sale and slaughter to a
/// release, a barred meal food slaughter.
/// </summary>
public sealed record HerdFacts
{
    public double LifeExpectancy { get; init; }
    public double ManhunterOnTameFail { get; init; }

    /// <summary>The nutrition the colony eats from the animal's live meat; zero for an animal whose meat the colony does not eat.</summary>
    public double MeatNutrition { get; init; }

    public AnimalFeed Feed { get; init; } = new AnimalFeed.NoPlants();

    public bool SlaughterBarred { get; init; }
    public bool EatingBarred { get; init; }

    public bool Predator { get; init; }

    /// <summary>Whether slaughter or eating the animal is barred by the player ideo.</summary>
    internal bool FoodBarred() => SlaughterBarred || EatingBarred;
}

/// <summary>
/// The breeding rule of one egg-laying race whose eggs can be fertilized, derived from the catalog:
/// HensPerRooster is the hens one rooster keeps laying fertilized eggs.
/// </summary>
public sealed record HerdLayer
{
    public double HensPerRooster { get; init; }
}

/// <summary>A removal the herd plan lists: the animal and how it leaves.</summary>
internal sealed record HerdRemoval(UpkeepAnimal Animal, HusbandryMethod Method);

public static class HerdSurplusRules
{
    /// <summary>
    /// Breeding pair the herd Need never culls below: one male and two females of each race, so a race can still
    /// recover by breeding after any removal. Only fertile animals count toward it (<see cref="HerdFertile"/>).
    /// </summary>
    public const long HerdPairMales = 1;

    public const long HerdPairFemales = 2;
    public const long HerdPairSize = HerdPairMales + HerdPairFemales;

    /// <summary>Of RaceProps.lifeExpectancy marks an animal as old.</summary>
    internal const double HerdOldFraction = 0.8;

    /// <summary>One breeding male per this many females; males beyond max(1, ceil(females/5)) are excess.</summary>
    internal const long HerdMalesPerFemales = 5;

    /// <summary>Spreads stored pen feed over one quadrum, the span the worst-quadrum pasture rate describes.</summary>
    internal const double HerdStoredFeedDays = 15.0;

    /// <summary>The RaceProps.manhunterOnTameFailChance at or above which a race is dangerous to tame.</summary>
    internal const double HerdTameDanger = 0.2;

    /// <summary>Whether the animal can still breed: anything not read as sterilized, so a census without the fact counts every animal.</summary>
    internal static bool HerdFertile(UpkeepAnimal a) => !a.Sterilized;

    /// <summary>The layer rule of a race, not ok for a race that does not lay fertilizable eggs or whose catalog breeding facts give no rooster ratio (it keeps males like any other race).</summary>
    internal static (HerdLayer Layer, bool Ok) HerdLayerOf(AnimalRace race)
    {
        foreach (var p in race.Products)
        {
            if (p.Kind != "eggs" || string.IsNullOrEmpty(p.FertilizedDef.Value))
            {
                continue;
            }
            var mtb = race.MateMtbHours;
            var count = p.Amount;
            var interval = p.IntervalDays;
            if (mtb <= 0 || count <= 0 || interval <= 0 || p.FertilizationCountMax <= 0)
            {
                return (new HerdLayer(), false);
            }
            var matingsPerDay = 24 / mtb;
            var matingsPerHenDay = count / interval / p.FertilizationCountMax;
            return (new HerdLayer { HensPerRooster = matingsPerDay / matingsPerHenDay }, true);
        }
        return (new HerdLayer(), false);
    }

    internal static bool HerdOld(UpkeepAnimal a)
    {
        var life = a.Herd.LifeExpectancy;
        return life > 0 && a.State.AgeYears > HerdOldFraction * life;
    }

    internal static bool HerdJuvenile(UpkeepAnimal a) => !a.State.Adult;

    /// <summary>A predator, or a race whose failed tame turns manhunter at least HerdTameDanger of the time. Such a race is never tamed.</summary>
    internal static bool HerdDangerous(UpkeepAnimal a) => a.Herd.Predator || a.Herd.ManhunterOnTameFail >= HerdTameDanger;

    /// <summary>Pasture supply over pen demand; not ok when a pen figure is not a finite food number, or no pen has demand.</summary>
    internal static (double Ratio, bool Ok) HerdPastureRatio(EquatableArray<PenGrazing> rows)
    {
        double supply = 0.0;
        double demand = 0.0;
        foreach (var p in rows)
        {
            if (!FoodForecastRules.FoodNumber(p.DemandPerDay) || !FoodForecastRules.FoodNumber(p.PasturePerDay) || !FoodForecastRules.FoodNumber(p.StoredNutrition))
            {
                return (0, false);
            }
            supply += p.PasturePerDay + p.StoredNutrition / HerdStoredFeedDays;
            demand += p.DemandPerDay;
        }
        if (demand <= 0)
        {
            return (0, false);
        }
        return (supply / demand, true);
    }

    /// <summary>The work trainables whose loss costs the colony labor: hauling, rescue and attack ("Release" in RimWorld's TrainableDefs).</summary>
    internal static readonly HashSet<string> HerdTrainables = new(StringComparer.Ordinal) { "Haul", "Rescue", "Release" };

    internal static bool HerdTrained(UpkeepAnimal a)
    {
        foreach (var t in a.Training)
        {
            if (t.Learned && HerdTrainables.Contains(t.DefName))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Slaughter whenever SafeToSlaughter allows it (it refuses a bonded animal) and the player ideo's precepts
    /// neither penalise nor forbid it (SlaughterBarred), else release when native allows that, else none.
    /// </summary>
    internal static HusbandryMethod? HerdRemovalMethod(UpkeepAnimal a)
    {
        if (a.SafeToSlaughter() && !a.Herd.SlaughterBarred)
        {
            return HusbandryMethod.Slaughter;
        }
        if (a.ReleaseAllowed())
        {
            return HusbandryMethod.Release;
        }
        return null;
    }

    /// <summary>Ranks plant feed per unit of eaten meat; an animal without both ranks last among its tier.</summary>
    internal static double HerdFeedPerMeat(UpkeepAnimal a)
    {
        if (a.Herd.Feed is not AnimalFeed.Plants { PerDay: var feed } || a.Herd.MeatNutrition <= 0)
        {
            return -1;
        }
        return feed / a.Herd.MeatNutrition;
    }

    private static int HerdTier(UpkeepAnimal a)
    {
        var sick = a.State.Sick;
        if (HerdOld(a) || sick)
        {
            return 0;
        }
        if (HerdJuvenile(a))
        {
            return 4;
        }
        if (HerdTrained(a))
        {
            return 3;
        }
        return 2;
    }

    /// <summary>
    /// Lists, per race in limits, the animals to remove so the kept count (not release/slaughter-designated)
    /// falls to the limit. Cull order: old (past 80% of life expectancy) or sick first; then males beyond one per
    /// five females; then untrained adults by highest feed per meat; then trained adults; juveniles only when
    /// juveniles is set (feed short). Ties go to the lowest ID. An animal whose removal would leave its race under
    /// a breeding pair of its own sex is kept, except in a retired race, which keeps no
    /// pair. A sterilized animal is no part of the pair and is removed freely.
    /// </summary>
    internal static IReadOnlyList<HerdRemoval> HerdSurplusCandidates(
        IReadOnlyList<UpkeepAnimal> rows,
        IReadOnlyDictionary<Resource, long> limits,
        bool juveniles,
        HerdPolicy herd
    )
    {
        var kept = new Dictionary<Resource, long>();
        var sexes = new Dictionary<Resource, Dictionary<Sex, long>>();
        var eligible = new Dictionary<Resource, List<HerdRemoval>>();
        foreach (var a in rows)
        {
            if (!limits.ContainsKey(a.Definition))
            {
                continue;
            }
            if (a.Release || a.Slaughter)
            {
                continue;
            }
            kept[a.Definition] = kept.GetValueOrDefault(a.Definition) + 1;
            if (!sexes.TryGetValue(a.Definition, out var bySex))
            {
                bySex = [];
                sexes[a.Definition] = bySex;
            }
            if (HerdFertile(a))
            {
                bySex[a.Gender] = bySex.GetValueOrDefault(a.Gender) + 1;
            }
            if (HerdRemovalMethod(a) is { } m &&(juveniles || !HerdJuvenile(a) || HerdOld(a)))
            {
                if (!eligible.TryGetValue(a.Definition, out var list))
                {
                    list = [];
                    eligible[a.Definition] = list;
                }
                list.Add(new HerdRemoval(a, m));
            }
        }
        var @out = new List<HerdRemoval>();
        foreach (var (race, limit) in limits)
        {
            var surplus = kept.GetValueOrDefault(race) - limit;
            var raceRows = eligible.GetValueOrDefault(race) ?? [];
            var bySex = sexes.GetValueOrDefault(race) ?? [];
            var (keepMales, floorMales) = herd.MalesKept(race, bySex.GetValueOrDefault(Sex.Female));
            var excessMales = bySex.GetValueOrDefault(Sex.Male) - keepMales;
            var ranked = raceRows
                .OrderBy(r => HerdTier(r.Animal))
                .ThenByDescending(r => HerdFeedPerMeat(r.Animal))
                .ThenBy(r => r.Animal.Id)
                .ToList();
            // Excess males jump ahead of every tier but old/sick, in the order already ranked.
            var excess = new HashSet<PawnId>();
            foreach (var r in ranked)
            {
                var a = r.Animal;
                if (a.Gender == Sex.Male && HerdTier(a) != 0 && excess.Count < excessMales)
                {
                    excess.Add(a.Id);
                }
            }
            int Rank(UpkeepAnimal a)
            {
                var t = HerdTier(a);
                if (t == 0)
                {
                    return 0;
                }
                return excess.Contains(a.Id) ? 1 : t;
            }
            ranked = ranked.OrderBy(r => Rank(r.Animal)).ToList();
            foreach (var r in ranked)
            {
                if (surplus <= 0)
                {
                    break;
                }
                var g = r.Animal.Gender;
                if (herd.Retired.GetValueOrDefault(race) || !HerdFertile(r.Animal))
                {
                    // no pair to keep
                }
                else if ((g == Sex.Male && bySex.GetValueOrDefault(Sex.Male) > floorMales) || (g == Sex.Female && bySex.GetValueOrDefault(Sex.Female) > HerdPairFemales))
                {
                    bySex[g]--;
                }
                else if (g == Sex.None)
                {
                    // asexual race: no pair to keep
                }
                else
                {
                    continue;
                }
                @out.Add(r);
                surplus--;
            }
        }
        return @out.OrderBy(r => r.Animal.Id).ToList();
    }

    /// <summary>Keeps max(floor, breeding pair) of every observed race, none of a retired one: food slaughter never cuts below either.</summary>
    internal static Dictionary<Resource, long> HerdFoodLimits(IReadOnlyList<UpkeepAnimal> rows, HerdPolicy herd)
    {
        var limits = new Dictionary<Resource, long>();
        foreach (var a in rows)
        {
            limits[a.Definition] = herd.Retired.GetValueOrDefault(a.Definition)
                ? 0
                : Math.Max(herd.PopulationMin.GetValueOrDefault(a.Definition), HerdPairSize);
        }
        return limits;
    }
}
