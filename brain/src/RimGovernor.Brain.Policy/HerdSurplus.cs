namespace RimGovernor.Brain.Policy;

/// <summary>
/// The per-animal facts MaintainHerd sizes and culls by. The census fills age, life expectancy, sickness,
/// adulthood, veneration and tame danger natively; MeatNutrition, FeedPerDay and Product merge in from the colony
/// food channels by animal ID; SlaughterBarred and EatingBarred merge in from the ideoligion
/// (<see cref="PolicyFunctions.ApplyHerdPrecepts"/>). Unknown age or sickness ranks an animal as ordinary (not old,
/// not sick, adult); an unknown SlaughterBarred leaves removal, sale and slaughter unplanned, an unknown
/// EatingBarred food slaughter. (Go <c>HerdFacts</c>.)
/// </summary>
public sealed record HerdFacts
{
    public Fact<double> AgeYears { get; init; }
    public Fact<double> LifeExpectancy { get; init; }
    public Fact<double> ManhunterOnTameFail { get; init; }
    public Fact<double> MeatNutrition { get; init; }
    public Fact<double> FeedPerDay { get; init; }
    public Fact<bool> Sick { get; init; }
    public Fact<bool> Adult { get; init; }
    public Fact<bool> SlaughterBarred { get; init; }
    public Fact<bool> EatingBarred { get; init; }
    public Fact<bool> Venerated { get; init; }

    /// <summary>Predator and Product retain recorded snapshot fields; herd sizing does not consume them. Product identifies milk, wool, chemfuel or egg producers.</summary>
    public bool Predator { get; init; }

    public bool Product { get; init; }

    /// <summary>The game ticks until a pregnant animal gives birth.</summary>
    public Fact<long> TicksToBirth { get; init; }

    /// <summary>The ticks until the next life stage begins, unknown in the last stage.</summary>
    public Fact<long> TicksToNextLifeStage { get; init; }

    /// <summary>The index of the animal's current life stage in the race's LifeStages.</summary>
    public Fact<int> LifeStageIndex { get; init; }

    /// <summary>Whether slaughter or eating the animal is barred by the player ideo or the precept read is missing.</summary>
    internal bool FoodBarredOrUnknown()
    {
        var sk = SlaughterBarred.TryGetValue(out var slaughter);
        var ek = EatingBarred.TryGetValue(out var eating);
        return slaughter || eating || !sk || !ek;
    }
}

/// <summary>
/// The breeding rule of one egg-laying race whose eggs can be fertilized, derived from the catalog:
/// HensPerRooster is the hens one rooster keeps laying fertilized eggs. Unknown when any of the facts is.
/// (Go <c>HerdLayer</c>.)
/// </summary>
public sealed record HerdLayer
{
    public Fact<double> HensPerRooster { get; init; }
}

/// <summary>A removal the herd plan lists: the animal and how it leaves. (Go <c>herdRemoval</c>.)</summary>
internal sealed record HerdRemoval(UpkeepAnimal Animal, HusbandryMethod Method);

public static partial class PolicyFunctions
{
    /// <summary>
    /// Breeding pair MaintainHerd never culls below: one male and two females of each race, so a race can still
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
    internal static bool HerdFertile(UpkeepAnimal a) => !a.Sterilized.ValueOrDefault;

    /// <summary>The layer rule of a race, not ok for a race that does not lay fertilizable eggs.</summary>
    internal static (HerdLayer Layer, bool Ok) HerdLayerOf(AnimalRace race)
    {
        foreach (var p in race.Products)
        {
            if (p.Kind != "eggs" || string.IsNullOrEmpty(p.FertilizedDef.Value))
            {
                continue;
            }
            var mk = race.MateMtbHours.TryGetValue(out var mtb);
            var ck = p.Amount.TryGetValue(out var count);
            var ik = p.IntervalDays.TryGetValue(out var interval);
            if (!mk || !ck || !ik || mtb <= 0 || count <= 0 || interval <= 0 || p.FertilizationCountMax <= 0)
            {
                return (new HerdLayer { HensPerRooster = Unknown<double>() }, true);
            }
            var matingsPerDay = 24 / mtb;
            var matingsPerHenDay = count / interval / p.FertilizationCountMax;
            return (new HerdLayer { HensPerRooster = Known(matingsPerDay / matingsPerHenDay) }, true);
        }
        return (new HerdLayer(), false);
    }

    internal static bool HerdOld(UpkeepAnimal a)
    {
        var ak = a.Herd.AgeYears.TryGetValue(out var age);
        var lk = a.Herd.LifeExpectancy.TryGetValue(out var life);
        return ak && lk && life > 0 && age > HerdOldFraction * life;
    }

    internal static bool HerdJuvenile(UpkeepAnimal a) => a.Herd.Adult.TryGetValue(out var adult) && !adult;

    /// <summary>A predator, or a race whose failed tame turns manhunter at least HerdTameDanger of the time. Such a race is never tamed.</summary>
    internal static bool HerdDangerous(UpkeepAnimal a) => a.Herd.Predator || a.Herd.ManhunterOnTameFail.ValueOrDefault >= HerdTameDanger;

    /// <summary>Pasture supply over pen demand; not ok when any pen fact is unknown, or no pen has demand.</summary>
    internal static (double Ratio, bool Ok) HerdPastureRatio(Fact<EquatableArray<PenGrazing>> pens)
    {
        if (!pens.TryGetValue(out var rows))
        {
            return (0, false);
        }
        double supply = 0.0;
        double demand = 0.0;
        foreach (var p in rows)
        {
            var dk = p.DemandPerDay.TryGetValue(out var d);
            var gk = p.PasturePerDay.TryGetValue(out var g);
            var sk = p.StoredNutrition.TryGetValue(out var s);
            if (!dk || !gk || !sk || !FoodNumber(d) || !FoodNumber(g) || !FoodNumber(s))
            {
                return (0, false);
            }
            supply += g + s / HerdStoredFeedDays;
            demand += d;
        }
        if (demand <= 0)
        {
            return (0, false);
        }
        return (supply / demand, true);
    }

    /// <summary>The work trainables whose loss costs the colony labor: hauling, rescue and attack ("Release" in RimWorld's TrainableDefs).</summary>
    private static readonly HashSet<string> HerdTrainables = new(StringComparer.Ordinal) { "Haul", "Rescue", "Release" };

    internal static bool HerdTrained(UpkeepAnimal a)
    {
        foreach (var t in a.Training)
        {
            if (t.Learned.TryGetValue(out var learned) && learned && HerdTrainables.Contains(t.Def))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Slaughter whenever SafeToSlaughter allows it (it refuses a bonded animal) and the player ideo's precepts
    /// neither penalise nor forbid it (SlaughterBarred), else release when native allows that, else none. Not ok
    /// when a needed native fact is unknown.
    /// </summary>
    internal static (HusbandryMethod? Method, bool Ok) HerdRemovalMethod(UpkeepAnimal a)
    {
        if (!a.SafeToSlaughter().TryGetValue(out var slaughter))
        {
            return (null, false);
        }
        if (!a.Herd.SlaughterBarred.TryGetValue(out var barred))
        {
            return (null, false);
        }
        if (slaughter && !barred)
        {
            return (HusbandryMethod.Slaughter, true);
        }
        if (!a.ReleaseAllowed().TryGetValue(out var release))
        {
            return (null, false);
        }
        if (release)
        {
            return (HusbandryMethod.Release, true);
        }
        return (null, true);
    }

    /// <summary>Ranks feed cost per unit of meat; unknown costs rank last among their tier.</summary>
    internal static double HerdFeedPerMeat(UpkeepAnimal a)
    {
        var fk = a.Herd.FeedPerDay.TryGetValue(out var feed);
        var mk = a.Herd.MeatNutrition.TryGetValue(out var meat);
        if (!fk || !mk || meat <= 0)
        {
            return -1;
        }
        return feed / meat;
    }

    private static int HerdTier(UpkeepAnimal a)
    {
        var sick = a.Herd.Sick.ValueOrDefault;
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
    /// a breeding pair of its own sex (or whose sex is unknown) is kept, except in a retired race, which keeps no
    /// pair. A sterilized animal is no part of the pair and is removed freely. Any tracked animal with an unknown
    /// designation or eligibility fact makes the result unknown.
    /// </summary>
    internal static (IReadOnlyList<HerdRemoval> Removals, bool Unknown) HerdSurplusCandidates(
        IReadOnlyList<UpkeepAnimal> rows,
        IReadOnlyDictionary<Resource, long> limits,
        bool juveniles,
        HerdPolicy herd
    )
    {
        var kept = new Dictionary<Resource, long>();
        var sexes = new Dictionary<Resource, Dictionary<string, long>>();
        var eligible = new Dictionary<Resource, List<HerdRemoval>>();
        foreach (var a in rows)
        {
            if (!limits.ContainsKey(a.Definition))
            {
                continue;
            }
            var rk = a.Release.TryGetValue(out var release);
            var sk = a.Slaughter.TryGetValue(out var slaughter);
            if (!rk || !sk)
            {
                return ([], true);
            }
            if (release || slaughter)
            {
                continue;
            }
            kept[a.Definition] = kept.GetValueOrDefault(a.Definition) + 1;
            if (!sexes.TryGetValue(a.Definition, out var bySex))
            {
                bySex = new Dictionary<string, long>(StringComparer.Ordinal);
                sexes[a.Definition] = bySex;
            }
            if (HerdFertile(a))
            {
                bySex[a.Gender] = bySex.GetValueOrDefault(a.Gender) + 1;
            }
            var (method, ok) = HerdRemovalMethod(a);
            if (!ok)
            {
                return ([], true);
            }
            if (method is { } m && (juveniles || !HerdJuvenile(a) || HerdOld(a)))
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
            var bySex = sexes.GetValueOrDefault(race) ?? new Dictionary<string, long>(StringComparer.Ordinal);
            var (keepMales, floorMales, mk) = herd.MalesKept(race, bySex.GetValueOrDefault("Female"));
            if (!mk)
            {
                return ([], true);
            }
            var excessMales = bySex.GetValueOrDefault("Male") - keepMales;
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
                if (a.Gender == "Male" && HerdTier(a) != 0 && excess.Count < excessMales)
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
                else if ((g == "Male" && bySex.GetValueOrDefault(g) > floorMales) || (g == "Female" && bySex.GetValueOrDefault(g) > HerdPairFemales))
                {
                    bySex[g] = bySex.GetValueOrDefault(g) - 1;
                }
                else if (g == "None")
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
        return (@out.OrderBy(r => r.Animal.Id).ToList(), false);
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
