namespace RimGovernor.Brain.Policy;

// Hunt food channels. A lone animal is offered as a designation channel; a formation hunt is one channel built from the
// roster the comparison chose (HuntPlanning), so no animal count, gunner count or radius decides whether a hunt forms.
/// <summary>How a hunt candidate executes: a lone hunter's designation, or a drafted formation (HuntRequest).</summary>
public enum HuntMode
{
    Lone,
    Formation,
}

public static class HuntSquadRules
{
    /// <summary>Scales the work of a sleeping animal: it stands still, so night is a soft preference and never a gate.</summary>
    internal const double SquadSleepingWork = 0.5;

    /// <summary>Counts the colonists with a hunting weapon (WeaponDef.Hunts) who are capable of hunting.</summary>
    public static int ArmedHunters(IReadOnlyList<PawnProfile> profiles) => profiles.Count(ArmedHunter);

    /// <summary>Whether the colonist has a hunting weapon (WeaponDef.Hunts) and is capable of hunting.</summary>
    public static bool ArmedHunter(PawnProfile p) => p.Hunts && p.Capable(WorkType.Hunting, 0);

    /// <summary>The hunt's mode: a formation is the candidate that names its prey.</summary>
    public static HuntMode Mode(this SupplyCandidate c) => c.Prey.Count > 0 ? HuntMode.Formation : HuntMode.Lone;

    /// <summary>
    /// The hunt channels of the offered food prey: the chosen roster's formation channel (when there is one), then a lone
    /// channel per animal the formation does not take. A hunt yields the animal's meat and its Products.
    /// </summary>
    public static IReadOnlyList<SupplyCandidate> HuntCandidates(IReadOnlyList<AcquisitionSource> sources, int hunters, SupplyCandidate? formation)
    {
        var grouped = new HashSet<string>(formation?.Prey ?? [], StringComparer.Ordinal);
        var output = new List<SupplyCandidate>();
        if (formation is not null)
        {
            output.Add(formation);
        }
        foreach (var s in sources)
        {
            if (s.Hunt && s.Food && !s.Tree && !grouped.Contains(s.Id))
            {
                output.Add(HuntChannel(s.Id, [s], hunters));
            }
        }
        return output;
    }

    /// <summary>
    /// Prices the prey as one cycle's hunt: each animal's pursuit work (shortened by weapon reach, halved
    /// asleep, only collection when downed), the worst revenge exposure among them as risk.
    /// </summary>
    internal static SupplyCandidate HuntChannel(string id, IReadOnlyList<AcquisitionSource> prey, int gunners)
    {
        double nutrition = 0, work = 0, risk = 0, exposure = 0;
        var products = new Dictionary<Resource, double>();
        var defs = new List<Resource>();
        var designated = true;
        foreach (var s in prey)
        {
            designated = designated && s.Designated;
            nutrition += s.NutritionYield;
            work += PursuitWorkTicks(s);
            risk = Math.Max(risk, s.HuntRevengeCost());
            exposure = Math.Max(exposure, s.HuntRevengeCost() * HuntWeakness(gunners, s.HerdSize));
            foreach (var p in s.Products)
            {
                if (!products.ContainsKey(p.Def))
                {
                    defs.Add(p.Def);
                }
                products[p.Def] = products.GetValueOrDefault(p.Def) + p.Amount;
            }
        }
        // The animals in reach are all the candidate can deliver: a finite source.
        var yields = new List<CandidateYield> { CandidateRules.NutritionYield(new YieldSupply.Bounded(nutrition / FoodPlanRules.FoodHuntCycleDays, (long)Math.Ceiling(nutrition))) };
        foreach (var def in defs)
        {
            yields.Add(CandidateRules.ProductYield(new ResourceKey { Def = def }, products[def] / FoodPlanRules.FoodHuntCycleDays));
        }
        var terms = new List<CandidateTerm>
        {
            new("estimated_cycle_days", FoodPlanRules.FoodHuntCycleDays),
            new("estimated_work_ticks", work),
            new("prey", prey.Count),
            new("revenge_cost", risk),
        };
        if (prey.Count == 1)
        {
            var s = prey[0];
            terms.Add(new CandidateTerm("revenge_chance", s.RevengeChance));
            terms.Add(new CandidateTerm("herd_size", s.HerdSize));
            terms.Add(new CandidateTerm("weapon_range", s.WeaponRange));
        }
        var c = CandidateRules.FoodCandidate(
            CandidateKind.Hunt,
            id,
            yields[0].Supply,
            CandidateRules.FoodState(false, designated),
            new CandidateLead.Days(0),
            new CandidateWork.PerDay(work / FoodPlanRules.FoodHuntCycleDays));
        return c with
        {
            Source = HuntCreditRules.HuntSource,
            Yields = [.. yields],
            Risk = [new CandidateRisk(CandidateRiskKind.Revenge, Math.Min(1, exposure))],
            Terms = [.. terms],
        };
    }

    /// <summary>
    /// The squad prey the food plan opens: the union of the open squad channels' prey, sorted. It is what
    /// makes the military/combat Need unmet while nothing hostile stands.
    /// </summary>
    public static IReadOnlyList<PawnId> HuntRequest(FoodPlan? plan)
    {
        if (plan is null)
        {
            return [];
        }
        var output = new List<PawnId>();
        foreach (var e in plan.Portfolio)
        {
            if (e.Channel.Kind == CandidateKind.Hunt && e.Decision == FoodPlanDecision.Open)
            {
                foreach (var id in e.Channel.Prey)
                {
                    output.Add(new PawnId(id));
                }
            }
        }
        output.Sort();
        return output;
    }

    /// <summary>One animal's pursuit work: the hunt work shortened by weapon reach, only collection when downed, halved asleep.</summary>
    private static double PursuitWorkTicks(AcquisitionSource s)
    {
        var w = s.Downed ? FoodPlanRules.FoodForageWorkTicks : FoodPlanRules.FoodHuntWorkTicks / (1 + (s.WeaponRange / 25));
        return s.Sleeping ? w * SquadSleepingWork : w;
    }

    /// <summary>
    /// The wildlife census as the tier-2 colony model reads it (#3620): the food prey the hunt channels are offered on
    /// (<see cref="HuntCandidates"/>), one herd per race, with the pursuit work, meat nutrition and butchery products each
    /// channel prices. The hunt channels have no lead days, so a herd's arrival is when its work is done.
    /// </summary>
    public static EquatableArray<ModelHerd> Herds(IReadOnlyList<AcquisitionSource> sources) => EquatableArray.From(sources
        .Where(s => s.Hunt && s.Food && !s.Tree)
        .GroupBy(s => new Resource(s.Definition))
        .OrderBy(g => g.Key)
        .Select(g => new ModelHerd
        {
            Race = g.Key,
            Animals = g.Count(),
            Work = g.Sum(PursuitWorkTicks),
            Nutrition = g.Sum(s => s.NutritionYield),
            Products = EquatableArray.From(g.SelectMany(s => s.Products).GroupBy(p => p.Def).OrderBy(p => p.Key).Select(p => new SourceProduct(p.Key, p.Sum(x => x.Amount)))),
        }));

    /// <summary>
    /// Scales a prey's revenge exposure by how little the colony can answer it: 1 with no gunners,
    /// falling as the gunners per animal of the herd grow. A strong colony takes a risky herd as readily
    /// as a safe one; a weak one is steered to safer prey by the discount on the risky yield.
    /// </summary>
    internal static double HuntWeakness(int gunners, int herd) => 1 / (1 + (Math.Max(0, gunners) / (double)Math.Max(1, herd)));
}
