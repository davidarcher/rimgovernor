namespace RimGovernor.Brain.Policy;

/// <summary>The one reason the colony keeps a race. <c>Unset</c> is Go's empty job.</summary>
public enum HerdJob
{
    Unset = 0,

    [WireName("milk")]
    Milk,

    [WireName("wool")]
    Wool,

    [WireName("chemfuel")]
    Chemfuel,

    /// <summary>Eggs.</summary>
    [WireName("food")]
    Food,

    [WireName("haul")]
    Haul,

    [WireName("war")]
    War,

    [WireName("companion")]
    Companion,

    [WireName("none")]
    None,
}

/// <summary>
/// Every fact the herd plan derives from. Unknown facts leave the plan unchanged: a race whose yield, size or
/// capacity is unread cannot hold or be ranked for the job that needs it. The catalog is required.
/// </summary>
public sealed record HerdPlanInput
{
    public Fact<EquatableArray<UpkeepAnimal>> Animals { get; init; }
    public Fact<EquatableArray<UpkeepAnimal>> Wild { get; init; }
    public required AnimalRaceCatalog Races { get; init; }
    public Fact<double> Budget { get; init; }
    public Fact<WealthFacts> Wealth { get; init; }
    public Fact<EquatableArray<PenGrazing>> Pens { get; init; }
    public Fact<FoodPlan> Food { get; init; }

    /// <summary>Races a trader offers or a quest rewards now; they count as obtainable (the buying and reward hooks).</summary>
    public IReadOnlyList<Resource> Offers { get; init; } = [];

    /// <summary>The colony's pawn profiles; an unread roster plans no leveling.</summary>
    public Fact<EquatableArray<PawnProfile>> Handlers { get; init; }
}

/// <summary>
/// One race's place in the plan. Consumers (taming, training, sterilize, sell, buy, pens) read it; none of them
/// owns a second copy.
/// </summary>
public sealed record HerdRole
{
    public Resource Race { get; init; }

    /// <summary>The race's job: the first work job it keeps, else retiring work, else companion (a bonded animal), else none.</summary>
    public HerdJob Job { get; init; }

    /// <summary>The plan's pick for at least one job.</summary>
    public bool Preferred { get; init; }

    /// <summary>
    /// Marks a preferred race that has animals but no breeding pair: they are never culled or sold,
    /// WantMale/WantFemale name the missing sex (taming and buying), and the race has no cap.
    /// </summary>
    public bool Founder { get; init; }

    public bool WantMale { get; init; }
    public bool WantFemale { get; init; }

    /// <summary>Marks a race every one of whose jobs a better, adult, paired race now covers: surplus, no breeding pair kept.</summary>
    public bool Retiring { get; init; }

    /// <summary>
    /// Marks a race kept only for jobs a better race will take over but does not yet cover: it keeps working and
    /// stops breeding (sterilize) until it retires.
    /// </summary>
    public bool Superseded { get; init; }
}

/// <summary>One job's ranking.</summary>
public sealed record HerdJobPlan
{
    public HerdJob Job { get; init; }

    /// <summary>The obtainable races, best first; Target is the first.</summary>
    public IReadOnlyList<Resource> Ranked { get; init; } = [];

    public Resource Target { get; init; } = new("");

    /// <summary>The target race's wanted head count.</summary>
    public long Heads { get; init; }
}

/// <summary>The derived plan, recomputed every cycle and never stored.</summary>
public sealed partial record HerdPlan
{
    public IReadOnlyDictionary<Resource, HerdRole> Roles { get; init; } = new Dictionary<Resource, HerdRole>();
    public IReadOnlyDictionary<HerdJob, HerdJobPlan> Jobs { get; init; } = new Dictionary<HerdJob, HerdJobPlan>();
    public HerdPolicy Policy { get; init; } = new();

    /// <summary>
    /// The races that are herds: a fertile breeding pair or HerdMinAnimals animals kept, with a policy ceiling to
    /// size a unit from (a founder or companion has none and stays with the misc animals).
    /// </summary>
    public IReadOnlyList<Resource> Herds { get; init; } = [];

    /// <summary>
    /// The easy race tamed (and trained) to raise the best handler's Animals skill while a wanted race needs more
    /// than any handler has; empty when none is needed or available.
    /// </summary>
    public Resource Leveling { get; init; } = new("");
}

/// <summary>The herd plan's views of a routine reading.</summary>
public static class RoundsFactsHerdPlanExtensions
{
    /// <summary>The plan's facts from a routine reading.</summary>
    public static HerdPlanInput HerdPlanInput(this RoundsFacts f) =>
        new()
        {
            Animals = f.AnimalUpkeep.Animals,
            Wild = f.AnimalUpkeep.WildAnimals,
            Races = f.AnimalUpkeep.AnimalRaces,
            Budget = f.WealthBudget(),
            Wealth = f.Wealth,
            Pens = f.PenGrazing,
            Food = f.FoodPlan,
            Handlers = f.WorkProfiles,
        };

    /// <summary>The population band of the herd plan of f.</summary>
    public static HerdPolicy HerdPolicy(this RoundsFacts f) => PolicyFunctions.PlanHerd(f.HerdPlanInput()).Policy;

    /// <summary>The herd the layout's pens and barn are sized for.</summary>
    public static int PenAnimals(this RoundsFacts f) => PolicyFunctions.PlanHerd(f.HerdPlanInput()).PenAnimals();

    /// <summary>The herds by race the station graph's pens are sized for.</summary>
    public static IReadOnlyList<StationHerd> PenHerds(this RoundsFacts f) => PolicyFunctions.PlanHerd(f.HerdPlanInput()).StationHerds();

    /// <summary>The herds the layout gives units of their own.</summary>
    public static IReadOnlyList<HerdCeiling> HerdUnits(this RoundsFacts f) => PolicyFunctions.PlanHerd(f.HerdPlanInput()).HerdUnits();
}

/// <summary>Herd policy behaviour owned by the herd surplus rules.</summary>
public static class HerdPolicyExtensions
{
    /// <summary>
    /// The fertile males a race keeps for its fertile females: Keep is the count above which males are excess,
    /// Floor the count no removal goes below. A layer race below its hen target (PopulationMin) keeps enough
    /// roosters to fertilize every egg and one at target, floor and keep alike; any other race keeps one per
    /// HerdMalesPerFemales females above a pair floor of HerdPairMales. Known is false for a layer whose ratio is
    /// unknown.
    /// </summary>
    public static (long Keep, long Floor, bool Known) MalesKept(this HerdPolicy h, Resource race, long females)
    {
        if (!h.Layers.TryGetValue(race, out var layer))
        {
            return (
                Math.Max(PolicyFunctions.HerdPairMales, (females + PolicyFunctions.HerdMalesPerFemales - 1) / PolicyFunctions.HerdMalesPerFemales),
                PolicyFunctions.HerdPairMales,
                true
            );
        }
        if (!layer.HensPerRooster.TryGetValue(out var ratio))
        {
            return (0, 0, false);
        }
        var keep = PolicyFunctions.HerdPairMales;
        if (females < h.PopulationMin.GetValueOrDefault(race))
        {
            keep = Math.Max(PolicyFunctions.HerdPairMales, (long)Math.Ceiling(females / ratio));
        }
        return (keep, keep, true);
    }
}

public static partial class PolicyFunctions
{
    /// <summary>The jobs a race earns by yield or training, in the order a race's label is chosen.</summary>
    public static readonly IReadOnlyList<HerdJob> HerdWorkJobs =
    [
        HerdJob.Milk,
        HerdJob.Wool,
        HerdJob.Chemfuel,
        HerdJob.Food,
        HerdJob.Haul,
        HerdJob.War,
    ];

    /// <summary>The trainables that make a trained job (RimWorld's TrainableDefs: "Release" is the attack training).</summary>
    public const string HerdHaulTraining = "Haul";

    public const string HerdWarTraining = "Release";

    /// <summary>The breeding stock kept beyond a job's head count.</summary>
    public const long HerdSpare = HerdPairSize;

    /// <summary>The smallest ceiling the wealth budget cuts a race without a job to.</summary>
    public const long HerdUnplannedFloor = 6;

    /// <summary>The headcount at which a race with no breeding pair is still a herd with a unit of its own.</summary>
    public const long HerdMinAnimals = 5;

    private sealed class HerdRaceStats
    {
        public AnimalRace Race { get; set; } = new();

        /// <summary>Males, females and asexual count fertile animals only: they make the pair.</summary>
        public long N { get; set; }

        public long Males { get; set; }
        public long Females { get; set; }
        public long Asexual { get; set; }
        public bool Unsexed { get; set; }
        public bool Bonded { get; set; }

        /// <summary>The summed per-adult capacity by job over adult animals holding it.</summary>
        public Dictionary<HerdJob, double> Capacity { get; } = [];

        /// <summary>Whether any animal holds the job.</summary>
        public HashSet<HerdJob> Holds { get; } = [];

        public bool Paired() => !Unsexed && (Asexual > 0 || (Males > 0 && Females > 0));
    }

    /// <summary>Maps a race product to the job it serves.</summary>
    private static (HerdJob Job, bool Ok) HerdProductJob(RaceProduct p)
    {
        if (p.Kind == "milk")
        {
            return (HerdJob.Milk, true);
        }
        if (p.Kind == "wool")
        {
            return (HerdJob.Wool, true);
        }
        if (p.Kind == "eggs")
        {
            return (HerdJob.Food, true);
        }
        if (p.Kind == "spawner" && p.Def.Value == "Chemfuel")
        {
            return (HerdJob.Chemfuel, true);
        }
        return (HerdJob.Unset, false);
    }

    /// <summary>
    /// What one adult of the race gives a job per day (products) or in total (carrying capacity, combat power).
    /// Capable is whether the race can hold the job at all; Known whether its yield is read and positive.
    /// </summary>
    private static (double Value, bool Capable, bool Known) HerdPerAdult(HerdJob job, AnimalRace r)
    {
        switch (job)
        {
            case HerdJob.Haul:
            {
                var ok = r.CarryingCapacity.TryGetValue(out var v);
                return (v, r.Trainables.Contains(HerdHaulTraining), ok && v > 0);
            }
            case HerdJob.War:
            {
                var ok = r.CombatPower.TryGetValue(out var v);
                return (v, r.Trainables.Contains(HerdWarTraining), ok && v > 0);
            }
        }
        var known = true;
        var capable = false;
        var value = 0.0;
        foreach (var p in r.Products)
        {
            var (j, ok) = HerdProductJob(p);
            if (!ok || j != job)
            {
                continue;
            }
            capable = true;
            var ak = p.Amount.TryGetValue(out var amount);
            var ik = p.IntervalDays.TryGetValue(out var interval);
            if (!ak || !ik || interval <= 0)
            {
                known = false;
                continue;
            }
            value += amount / interval;
        }
        return (value, capable, known && value > 0);
    }

    /// <summary>Yield per unit of feed, body size standing for the feed an animal eats.</summary>
    private static (double Score, bool Ok) HerdScore(HerdJob job, AnimalRace r)
    {
        var (value, _, known) = HerdPerAdult(job, r);
        var sk = r.BodySize.TryGetValue(out var size);
        if (!known || !sk || size <= 0)
        {
            return (0, false);
        }
        return (value / size, true);
    }

    private static bool HerdLearned(UpkeepAnimal a, string def)
    {
        foreach (var t in a.Training)
        {
            if (t.Learned.TryGetValue(out var learned) && learned && t.Def == def)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Whether the animal holds the job, and what it adds when adult.</summary>
    private static (bool Holds, double Value) HerdHeld(HerdJob job, UpkeepAnimal a, AnimalRace race)
    {
        var (per, capable, _) = HerdPerAdult(job, race);
        var (_, known) = HerdScore(job, race);
        if (!capable || !known)
        {
            return (false, 0);
        }
        var holds = job switch
        {
            HerdJob.Haul => HerdLearned(a, HerdHaulTraining),
            HerdJob.War => HerdLearned(a, HerdWarTraining),
            _ => true,
        };
        var value = 0.0;
        if (holds && !HerdJuvenile(a))
        {
            value = per;
        }
        return (holds, value);
    }

    /// <summary>The share of the race budget the defense can hold: set only while the wealth budget has negative headroom.</summary>
    private static (double Share, bool Ok) HerdBudgetShare(double headroom, double total)
    {
        if (headroom >= 0 || total <= 0)
        {
            return (0, false);
        }
        return (Math.Max(0, (total + headroom) / total), true);
    }

    /// <summary>
    /// Derives each race's job from the colony facts. A job is wanted while any kept animal holds it: a yield
    /// (milk, wool, chemfuel, eggs) or a learned training (haul, war). Its candidates are the catalog races able
    /// to hold it that the colony can obtain (owned, tameable wild, offered), ranked by yield per body size, then
    /// milk to cows, then lower handling skill. The best is the target: the plan wants its head count. A holder
    /// that is not the target retires once the target is adult, has a breeding pair and its adult capacity covers
    /// the holder's; until then the holder keeps working. Wealth and pasture bound the result as ceilings.
    /// </summary>
    public static HerdPlan PlanHerd(HerdPlanInput input)
    {
        var roles = new Dictionary<Resource, HerdRole>();
        var jobs = new Dictionary<HerdJob, HerdJobPlan>();
        var min = new Dictionary<Resource, long>();
        var max = new Dictionary<Resource, long>();
        HerdPlan Result(HerdPolicy policy, IReadOnlyList<Resource> herds, Resource leveling) =>
            new()
            {
                Roles = roles,
                Jobs = jobs,
                Policy = policy,
                Herds = herds,
                Leveling = leveling,
            };
        var floors = FoodHerdPolicy(new HerdPolicy(), input.Food).PopulationMin;
        if (!input.Animals.TryGetValue(out var rows))
        {
            return Result(new HerdPolicy { PopulationMin = min, PopulationMax = max }, [], new Resource(""));
        }
        var stats = new Dictionary<Resource, HerdRaceStats>();
        var order = new List<Resource>();
        foreach (var a in rows)
        {
            var rk = a.Release.TryGetValue(out var release);
            var sk = a.Slaughter.TryGetValue(out var slaughter);
            if ((rk && release) || (sk && slaughter))
            {
                continue;
            }
            if (!stats.TryGetValue(a.Definition, out var s))
            {
                s = new HerdRaceStats();
                stats[a.Definition] = s;
                order.Add(a.Definition);
                _ = input.Races.Race(a.Definition, out var race);
                s.Race = race;
            }
            s.N++;
            if (a.Gender != "Male" && a.Gender != "Female" && a.Gender != "None")
            {
                s.Unsexed = true;
            }
            else if (!HerdFertile(a))
            {
                // a sterilized animal is no part of a pair
            }
            else if (a.Gender == "Male")
            {
                s.Males++;
            }
            else if (a.Gender == "Female")
            {
                s.Females++;
            }
            else
            {
                s.Asexual++;
            }
            if (a.Bonded.ValueOrDefault)
            {
                s.Bonded = true;
            }
            foreach (var job in HerdWorkJobs)
            {
                var (holds, value) = HerdHeld(job, a, s.Race);
                if (holds)
                {
                    s.Holds.Add(job);
                    s.Capacity[job] = s.Capacity.GetValueOrDefault(job) + value;
                }
            }
        }
        order.Sort();
        var obtainable = HerdObtainable(input, stats);
        var retiring = new HashSet<Resource>();
        var preferred = new HashSet<Resource>();
        var keeps = new Dictionary<Resource, HashSet<HerdJob>>();
        var superseded = new HashSet<Resource>();
        var targets = new Dictionary<Resource, long>();
        foreach (var job in HerdWorkJobs)
        {
            var holders = order.Where(def => stats[def].Holds.Contains(job)).ToList();
            if (holders.Count == 0 && !(job == HerdJob.War && HerdWarWanted(input)))
            {
                continue;
            }
            var ranked = HerdRank(job, input.Races, HerdCandidates(job, input, stats, obtainable));
            if (ranked.Count == 0)
            {
                if (holders.Count == 0)
                {
                    continue;
                }
                foreach (var def in holders)
                {
                    HerdKeep(keeps, def, job);
                }
                jobs[job] = new HerdJobPlan { Job = job, Ranked = ranked };
                continue;
            }
            var jp = new HerdJobPlan { Job = job, Ranked = ranked, Target = ranked[0] };
            preferred.Add(jp.Target);
            HerdKeep(keeps, jp.Target, job);
            var target = stats.GetValueOrDefault(jp.Target) ?? new HerdRaceStats();
            _ = input.Races.Race(jp.Target, out var targetRace);
            var (perTarget, _, _) = HerdPerAdult(job, targetRace);
            var demand = target.Capacity.GetValueOrDefault(job);
            var others = 0.0;
            var foodDemand = 0.0;
            foreach (var def in holders)
            {
                var s = stats[def];
                var (per, _, _) = HerdPerAdult(job, s.Race);
                foodDemand += floors.GetValueOrDefault(def) * per;
                if (def == jp.Target)
                {
                    continue;
                }
                others += s.Capacity.GetValueOrDefault(job);
                if (target.Paired() && target.Capacity.GetValueOrDefault(job) > 0 && target.Capacity.GetValueOrDefault(job) >= s.Capacity.GetValueOrDefault(job))
                {
                    continue;
                }
                HerdKeep(keeps, def, job);
                superseded.Add(def);
            }
            demand = Math.Max(demand, Math.Max(others, foodDemand));
            var heads = HerdPairSize;
            if (!double.IsInfinity(demand))
            {
                heads = Math.Max(heads, (long)Math.Ceiling(demand / perTarget));
            }
            jp = jp with { Heads = heads };
            targets[jp.Target] = Math.Max(targets.GetValueOrDefault(jp.Target), jp.Heads);
            jobs[job] = jp;
        }
        foreach (var def in order)
        {
            var s = stats[def];
            var role = new HerdRole
            {
                Race = def,
                Job = HerdJob.None,
                Preferred = preferred.Contains(def),
                Superseded = superseded.Contains(def) && !preferred.Contains(def),
            };
            var held = HerdWorkJobs.Where(job => s.Holds.Contains(job)).ToList();
            var kept = keeps.GetValueOrDefault(def);
            foreach (var job in HerdWorkJobs)
            {
                if (kept is not null && kept.Contains(job))
                {
                    role = role with { Job = job };
                    break;
                }
            }
            if (held.Count > 0 && (kept is null || kept.Count == 0))
            {
                role = role with { Retiring = true, Job = held[0] };
                retiring.Add(def);
            }
            else if (role.Job == HerdJob.None && s.Bonded)
            {
                role = role with { Job = HerdJob.Companion };
            }
            if (role.Preferred && s.N > 0 && !s.Unsexed && !s.Paired())
            {
                role = role with { Founder = true, WantMale = s.Males == 0, WantFemale = s.Females == 0 };
            }
            roles[def] = role;
        }
        foreach (var (def, heads) in targets)
        {
            if (!stats.ContainsKey(def))
            {
                roles[def] = new HerdRole
                {
                    Race = def,
                    Job = HerdTargetJob(jobs, def),
                    Preferred = true,
                    Founder = false,
                    WantMale = true,
                    WantFemale = true,
                };
            }
            min[def] = heads;
            max[def] = heads + HerdSpare;
        }
        var leveling = HerdLevelingRace(input, stats, targets);
        if (leveling.Value.Length != 0)
        {
            roles[leveling] = new HerdRole { Race = leveling, Job = HerdJob.None };
            min[leveling] = 1;
            max[leveling] = 1 + HerdSpare;
        }
        if (HerdBudgetCeiling(input) is { Ok: true } budget)
        {
            foreach (var def in max.Keys.ToList())
            {
                max[def] = HerdShareOf(max[def], budget.Share, HerdPairSize);
                min[def] = Math.Min(min.GetValueOrDefault(def), max[def]);
            }
            foreach (var def in order)
            {
                if (!targets.ContainsKey(def) && !retiring.Contains(def) && roles[def].Job != HerdJob.Companion)
                {
                    max[def] = HerdShareOf(stats[def].N, budget.Share, HerdUnplannedFloor);
                }
            }
        }
        var feedShort = false;
        var (ratio, ratioKnown) = HerdPastureRatio(input.Pens);
        if (ratioKnown)
        {
            feedShort = ratio < 1;
            var penned = new Dictionary<Resource, long>();
            foreach (var a in rows)
            {
                if (a.RequiresPen.TryGetValue(out var pen) && pen && !a.Herd.Predator)
                {
                    penned[a.Definition] = penned.GetValueOrDefault(a.Definition) + 1;
                }
            }
            foreach (var (def, n) in penned)
            {
                var feed = (long)Math.Floor(n * ratio);
                if (!max.TryGetValue(def, out var ceiling) || feed < ceiling)
                {
                    max[def] = feed;
                }
            }
        }
        var layers = new Dictionary<Resource, HerdLayer>();
        foreach (var def in order)
        {
            var (layer, ok) = HerdLayerOf(stats[def].Race);
            if (ok)
            {
                layers[def] = layer;
            }
        }
        var retired = new Dictionary<Resource, bool>();
        foreach (var (def, role) in roles)
        {
            if (role.Retiring)
            {
                max[def] = 0;
                min.Remove(def);
                retired[def] = true;
            }
            else if (role.Founder || role.Job == HerdJob.Companion)
            {
                max.Remove(def);
            }
        }
        var herds = new List<Resource>();
        foreach (var def in order)
        {
            if (max.TryGetValue(def, out var ceiling) && ceiling > 0 && (stats[def].Paired() || stats[def].N >= HerdMinAnimals))
            {
                herds.Add(def);
            }
        }
        var policy = new HerdPolicy
        {
            PopulationMin = min,
            PopulationMax = max,
            Retired = retired,
            FeedShort = feedShort,
            Roles = roles,
            Layers = layers,
        };
        return Result(policy, herds, leveling);
    }

    /// <summary>
    /// The easy race to tame while a wanted race's head count is short and its minimum handling skill is above
    /// what every handler has (TamerFor). It is the quickest leveller among the tameable wild races on the map
    /// that a handler already clears: lowest minimum handling skill, then lowest wildness, both read from the
    /// race catalog (an unread wildness ranks last), then definition name. Empty once a handler clears the wanted
    /// race (the leveling stops) or no such animal is on the map.
    /// </summary>
    private static Resource HerdLevelingRace(HerdPlanInput input, Dictionary<Resource, HerdRaceStats> stats, Dictionary<Resource, long> targets)
    {
        var none = new Resource("");
        if (!input.Handlers.TryGetValue(out var profiles))
        {
            return none;
        }
        var gap = false;
        foreach (var (def, heads) in targets)
        {
            _ = input.Races.Race(def, out var race);
            var known = race.MinimumHandlingSkill.TryGetValue(out var minimum);
            if (!known || (stats.TryGetValue(def, out var owned) && owned.N >= heads))
            {
                continue;
            }
            var handled = TamerFor(profiles, minimum) is not null;
            if (!handled)
            {
                gap = true;
            }
        }
        if (!gap)
        {
            return none;
        }
        input.Wild.TryGetValue(out var wild);
        var best = none;
        var bestSkill = 0;
        var bestWild = 0.0;
        foreach (var a in wild)
        {
            if (!a.Tameable.TryGetValue(out var tameable) || !tameable || HerdDangerous(a))
            {
                continue;
            }
            _ = input.Races.Race(a.Definition, out var race);
            var known = race.MinimumHandlingSkill.TryGetValue(out var minimum);
            var handled = TamerFor(profiles, minimum) is not null;
            if (!known || !handled)
            {
                continue;
            }
            if (!race.Wildness.TryGetValue(out var wildness))
            {
                wildness = double.MaxValue;
            }
            if (
                best.Value.Length == 0
                || minimum < bestSkill
                || (minimum == bestSkill && (wildness < bestWild || (wildness == bestWild && string.CompareOrdinal(a.Definition.Value, best.Value) < 0)))
            )
            {
                best = a.Definition;
                bestSkill = minimum;
                bestWild = wildness;
            }
        }
        return best;
    }

    private static void HerdKeep(Dictionary<Resource, HashSet<HerdJob>> keeps, Resource def, HerdJob job)
    {
        if (!keeps.TryGetValue(def, out var set))
        {
            set = [];
            keeps[def] = set;
        }
        set.Add(job);
    }

    private static HerdJob HerdTargetJob(Dictionary<HerdJob, HerdJobPlan> jobs, Resource def)
    {
        foreach (var job in HerdWorkJobs)
        {
            if (jobs.TryGetValue(job, out var jp) && jp.Target == def)
            {
                return job;
            }
        }
        return HerdJob.None;
    }

    private static long HerdShareOf(long n, double share, long floor) => Math.Max(floor, (long)Math.Floor(n * share));

    private static (double Share, bool Ok) HerdBudgetCeiling(HerdPlanInput input)
    {
        var bk = input.Budget.TryGetValue(out var headroom);
        var wk = input.Wealth.TryGetValue(out var w);
        if (!bk || !wk || !Finite(headroom) || !Finite(w!.Total))
        {
            return (0, false);
        }
        return HerdBudgetShare(headroom, w.Total);
    }

    /// <summary>
    /// The races the colony has or can get: owned, tameable and not dangerous in the wild census, or offered. An
    /// unread wild census, or a wild animal with unknown tame facts, offers nothing.
    /// </summary>
    private static HashSet<Resource> HerdObtainable(HerdPlanInput input, Dictionary<Resource, HerdRaceStats> owned)
    {
        var @out = new HashSet<Resource>(owned.Keys);
        foreach (var def in input.Offers)
        {
            @out.Add(def);
        }
        input.Wild.TryGetValue(out var wild);
        foreach (var a in wild)
        {
            if (a.Tameable.TryGetValue(out var tameable) && tameable && !HerdDangerous(a))
            {
                @out.Add(a.Definition);
            }
        }
        return @out;
    }

    /// <summary>
    /// Whether the colony wants a war animal it has none of: the wealth budget is read and has no negative
    /// headroom, so the defense keeps pace with the wealth the animals add. Held war animals are kept (and cut by
    /// the budget ceiling) whatever this says.
    /// </summary>
    private static bool HerdWarWanted(HerdPlanInput input) => input.Budget.TryGetValue(out var headroom) && Finite(headroom) && headroom >= 0;

    /// <summary>
    /// The races ranked for the job. War candidates the colony does not own also need a handler whose Animals
    /// skill clears the race's minimum handling skill, read from the catalog (an unread roster or minimum leaves
    /// the race out). A dangerous wild race is never tamed for war: a failed tame turns it on the handler, and
    /// nothing protects the handler.
    /// </summary>
    private static HashSet<Resource> HerdCandidates(HerdJob job, HerdPlanInput input, Dictionary<Resource, HerdRaceStats> owned, HashSet<Resource> obtainable)
    {
        if (job != HerdJob.War)
        {
            return obtainable;
        }
        input.Handlers.TryGetValue(out var profiles);
        var @out = new HashSet<Resource>();
        foreach (var def in obtainable)
        {
            _ = input.Races.Race(def, out var race);
            var known = race.MinimumHandlingSkill.TryGetValue(out var minimum);
            var handled = TamerFor(profiles, minimum) is not null;
            if (owned.ContainsKey(def) || (known && handled))
            {
                @out.Add(def);
            }
        }
        return @out;
    }

    /// <summary>Orders the obtainable races able to hold the job.</summary>
    private static List<Resource> HerdRank(HerdJob job, AnimalRaceCatalog catalog, HashSet<Resource> obtainable)
    {
        var rows = new List<(Resource Def, double Score, int Skill)>();
        foreach (var def in obtainable)
        {
            var ok = catalog.Race(def, out var race);
            if (!ok)
            {
                continue;
            }
            var (_, capable, _) = HerdPerAdult(job, race);
            if (!capable)
            {
                continue;
            }
            var (score, known) = HerdScore(job, race);
            if (!known)
            {
                continue;
            }
            if (job == HerdJob.War)
            {
                // A war animal is chosen for the fight it wins, not its feed.
                (score, _, _) = HerdPerAdult(job, race);
            }
            if (!race.MinimumHandlingSkill.TryGetValue(out var skill))
            {
                skill = int.MaxValue;
            }
            rows.Add((def, score, skill));
        }
        // The score comparison is a tolerance, not a total order, so sort the stable input (by name) with an insertion sort.
        var sorted = rows.OrderBy(r => r.Def).ToList();
        for (var i = 1; i < sorted.Count; i++)
        {
            var cur = sorted[i];
            var j = i - 1;
            while (j >= 0 && HerdRankLess(cur, sorted[j]))
            {
                sorted[j + 1] = sorted[j];
                j--;
            }
            sorted[j + 1] = cur;
        }
        return sorted.Select(r => r.Def).ToList();
    }

    private static bool HerdRankLess((Resource Def, double Score, int Skill) a, (Resource Def, double Score, int Skill) b)
    {
        if (Math.Abs(a.Score - b.Score) > 1e-9 * Math.Max(a.Score, b.Score))
        {
            return a.Score > b.Score;
        }
        if (a.Skill != b.Skill)
        {
            return a.Skill < b.Skill;
        }
        return a.Def.CompareTo(b.Def) < 0;
    }
}
