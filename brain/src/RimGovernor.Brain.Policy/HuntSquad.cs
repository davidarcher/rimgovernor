namespace RimGovernor.Brain.Policy;

// Hunt food channels. A lone animal is offered as a designation channel; a formation hunt is one channel built from the
// roster the comparison chose (HuntPlanning), so no animal count, gunner count or radius decides whether a hunt forms.
public static partial class PolicyConstants
{
    /// <summary>Scales the work of a sleeping animal: it stands still, so night is a soft preference and never a gate.</summary>
    internal const double SquadSleepingWork = 0.5;
}

/// <summary>How a hunt candidate executes: a lone hunter's designation, or a drafted formation (HuntRequest).</summary>
public enum HuntMode
{
    Lone,
    Formation,
}

public static partial class PolicyFunctions
{
    /// <summary>Counts the colonists with a hunting weapon (WeaponDef.Hunts) who are capable of hunting.</summary>
    public static int ArmedHunters(IReadOnlyList<PawnProfile> profiles)
    {
        var n = 0;
        foreach (var p in profiles)
        {
            if (p.Hunts && p.Capable(WorkType.Hunting, 0))
            {
                n++;
            }
        }
        return n;
    }

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
        var c = FoodCandidate(CandidateKind.Hunt, id, Unknown<double>());
        var designated = true;
        foreach (var s in prey)
        {
            designated = designated && s.Designated;
            var w = FoodHuntWorkTicks / (1 + (s.WeaponRange / 25));
            if (s.Downed)
            {
                w = FoodForageWorkTicks;
            }
            if (s.Sleeping)
            {
                w *= SquadSleepingWork;
            }
            nutrition += s.NutritionYield;
            work += w;
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
        var yields = new List<CandidateYield>(c.Yields);
        // The animals in reach are all the candidate can deliver: a finite source.
        yields[0] = yields[0] with
        {
            PerDay = Known(nutrition / FoodHuntCycleDays),
            StockCap = Known((long)Math.Ceiling(nutrition)),
        };
        foreach (var def in defs)
        {
            yields.Add(new CandidateYield { Good = new ResourceKey { Def = def }, PerDay = Known(products[def] / FoodHuntCycleDays) });
        }
        var terms = new List<CandidateTerm>
        {
            new("estimated_cycle_days", FoodHuntCycleDays),
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
        var laborPerDay = Known(work / FoodHuntCycleDays);
        return c with
        {
            LeadDays = Known(0.0),
            Source = HuntSource,
            State = FoodState(Known(false), Known(designated)),
            Yields = [.. yields],
            LaborPerDay = laborPerDay,
            Risk = [new CandidateRisk(CandidateRiskKind.Revenge, Math.Min(1, exposure))],
            Terms = [.. terms],
        };
    }

    /// <summary>
    /// The squad prey the food plan opens: the union of the open squad channels' prey, sorted. It is what
    /// makes the military/combat Need unmet while nothing hostile stands.
    /// </summary>
    public static IReadOnlyList<PawnId> HuntRequest(Fact<FoodPlan> plan)
    {
        if (!plan.TryGetValue(out var p))
        {
            return [];
        }
        var output = new List<PawnId>();
        foreach (var e in p.Portfolio)
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

    /// <summary>
    /// Scales a prey's revenge exposure by how little the colony can answer it: 1 with no gunners,
    /// falling as the gunners per animal of the herd grow. A strong colony takes a risky herd as readily
    /// as a safe one; a weak one is steered to safer prey by the discount on the risky yield.
    /// </summary>
    internal static double HuntWeakness(int gunners, int herd) => 1 / (1 + (Math.Max(0, gunners) / (double)Math.Max(1, herd)));
}
