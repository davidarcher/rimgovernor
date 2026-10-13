namespace RimGovernor.Brain.Policy;

// A squad hunt takes a group of nearby wild animals, mixed species
// allowed, with every drafted ranged colonist, at least SquadHuntMinGunners. A group is worth a squad at
// SquadHuntMinPrey animals, or at any single animal a lone hunter must not
// designate (Retaliates); the revenge cap (MaxHuntRevengeChance) binds only
// lone designation hunters.
public static partial class PolicyConstants
{
    public const int SquadHuntMinPrey = 3;

    /// <summary>The smallest squad worth drafting; there is no upper bound.</summary>
    public const int SquadHuntMinGunners = 3;

    /// <summary>The linkage distance, in cells, that joins two animals into one group: a threshold, not a cap.</summary>
    internal const double SquadHuntRadius = 12.0;

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
    /// <summary>
    /// Scales a squad hunt's work for the current weather's accuracy multiplier (WeatherDef.accuracyMultiplier,
    /// the factor on ranged hit chance): half of the extra shots the lower hit chance costs, so a hunt
    /// is mildly dearer and never gated. An unknown weather, and a multiplier of 1 or more, leave it unchanged.
    /// </summary>
    public static double SquadWeatherWork(Fact<double> accuracy)
    {
        if (accuracy.TryGetValue(out var hit) && hit > 0 && hit < 1)
        {
            return 1 + (((1 / hit) - 1) / 2);
        }
        return 1;
    }

    /// <summary>Counts the colonists a squad hunt may draft: a hunting weapon (WeaponDef.Hunts) and capable of hunting.</summary>
    public static int SquadGunners(IReadOnlyList<PawnProfile> profiles)
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

    /// <summary>
    /// Clusters the standing, food-yielding squad prey (single linkage within SquadHuntRadius) and keeps
    /// the groups worth a squad, each sorted by id, groups by first id.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<AcquisitionSource>> SquadPreyGroups(IReadOnlyList<AcquisitionSource> sources)
    {
        var prey = sources
            .Where(s => s.SquadPrey() && s.Food && !s.Downed)
            .OrderBy(s => s.Id, StringComparer.Ordinal)
            .ToList();
        var group = new int[prey.Count];
        for (var i = 0; i < group.Length; i++)
        {
            group[i] = i;
        }
        int Find(int i)
        {
            while (group[i] != i)
            {
                group[i] = group[group[i]];
                i = group[i];
            }
            return i;
        }
        for (var i = 0; i < prey.Count; i++)
        {
            for (var j = i + 1; j < prey.Count; j++)
            {
                var dx = (double)(prey[i].Cell.X - prey[j].Cell.X);
                var dz = (double)(prey[i].Cell.Z - prey[j].Cell.Z);
                if (Math.Sqrt((dx * dx) + (dz * dz)) <= SquadHuntRadius)
                {
                    group[Find(j)] = Find(i);
                }
            }
        }
        var byRoot = new Dictionary<int, List<AcquisitionSource>>();
        var roots = new List<int>();
        for (var i = 0; i < prey.Count; i++)
        {
            var r = Find(i);
            if (!byRoot.TryGetValue(r, out var members))
            {
                roots.Add(r);
                members = [];
                byRoot[r] = members;
            }
            members.Add(prey[i]);
        }
        var output = new List<IReadOnlyList<AcquisitionSource>>();
        foreach (var r in roots)
        {
            var g = byRoot[r];
            var worth = g.Count >= SquadHuntMinPrey;
            foreach (var s in g)
            {
                worth = worth || s.Retaliates();
            }
            if (worth)
            {
                output.Add(g);
            }
        }
        return output;
    }

    /// <summary>The hunt's mode: a formation is the candidate that names its prey.</summary>
    public static HuntMode Mode(this SupplyCandidate c) => c.Prey.Count > 0 ? HuntMode.Formation : HuntMode.Lone;

    /// <summary>
    /// The hunt channels of the offered food prey, one per animal or per group: a group worth a squad
    /// (SquadPreyGroups) is one formation channel <c>squad:&lt;first id&gt;</c>, every other animal a lone
    /// channel named by its id. A grouped animal is credited once, in its group's channel.
    /// With fewer than SquadHuntMinGunners gunners a group with no retaliating animal stays lone
    /// channels; a group with one is a Hold formation: it yields nothing and carries a needs_gunners
    /// term, the gunners it lacks, and the nutrition it would deliver. Bad weather (rain, snow, fog)
    /// mildly raises the work of a formation. A hunt yields the animal's meat and its Products.
    /// </summary>
    public static IReadOnlyList<SupplyCandidate> HuntCandidates(IReadOnlyList<AcquisitionSource> sources, int gunners, Fact<double> weatherAccuracy)
    {
        var grouped = new HashSet<string>(StringComparer.Ordinal);
        var output = new List<SupplyCandidate>();
        foreach (var g in SquadPreyGroups(sources))
        {
            if (gunners < SquadHuntMinGunners && !AnyRetaliates(g))
            {
                continue; // a formation nobody can field: the animals stay lone channels
            }
            foreach (var s in g)
            {
                grouped.Add(s.Id);
            }
            output.Add(HuntChannel("squad:" + g[0].Id, g, true, SquadWeatherWork(weatherAccuracy), gunners));
        }
        foreach (var s in sources)
        {
            if (s.Hunt && s.Food && !s.Tree && !grouped.Contains(s.Id))
            {
                output.Add(HuntChannel(s.Id, [s], false, 1, gunners));
            }
        }
        return output;
    }

    /// <summary>Reports whether a lone hunter must not designate some animal of the group.</summary>
    internal static bool AnyRetaliates(IReadOnlyList<AcquisitionSource> g)
    {
        foreach (var s in g)
        {
            if (s.Retaliates())
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Prices the prey as one cycle's hunt: each animal's pursuit work (shortened by weapon reach, halved
    /// asleep, only collection when downed) scaled by weather, the worst revenge exposure among them as risk.
    /// </summary>
    internal static SupplyCandidate HuntChannel(string id, IReadOnlyList<AcquisitionSource> prey, bool formation, double weather, int gunners)
    {
        double nutrition = 0, work = 0, risk = 0, exposure = 0;
        var products = new Dictionary<Resource, double>();
        var defs = new List<Resource>();
        var c = FoodCandidate(CandidateKind.Hunt, id, Unknown<double>());
        var preyIds = new List<string>();
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
            work += w * weather;
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
            if (formation)
            {
                preyIds.Add(s.Id);
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
        if (formation && gunners < SquadHuntMinGunners)
        {
            terms.Add(new CandidateTerm("needs_gunners", SquadHuntMinGunners - gunners));
            terms.Add(new CandidateTerm("held_nutrition_per_day", nutrition / FoodHuntCycleDays));
            yields = [yields[0] with { PerDay = Known(0.0) }];
            laborPerDay = Known(0.0);
        }
        return c with
        {
            LeadDays = Known(0.0),
            Source = HuntSource,
            State = FoodState(Known(false), Known(designated)),
            Prey = EquatableArray.From(preyIds),
            Yields = [.. yields],
            LaborPerDay = laborPerDay,
            Risk = [new CandidateRisk(CandidateRiskKind.Revenge, Math.Min(1, exposure))],
            Terms = [.. terms],
        };
    }

    /// <summary>
    /// The squad prey the food plan opens: the union of the open squad channels' prey, sorted. It is what
    /// raises the hunt origin of the ActiveCombat incident while nothing hostile stands.
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
