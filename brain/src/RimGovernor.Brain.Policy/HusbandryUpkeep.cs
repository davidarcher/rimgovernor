using Sex = RimGovernor.Contracts.Wire.Defs.Gender;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// The per-race population band the animals family's herd Need plans from. PopulationMin drives tame designations on wild
/// animals; PopulationMax drives surplus removal. A race missing from PopulationMax has no known cap and is
/// never culled for surplus. FeedShort (pasture below pen demand) lets juveniles be culled.
/// </summary>
public sealed record HerdPolicy
{
    public IReadOnlyDictionary<Resource, long> PopulationMin { get; init; } = new Dictionary<Resource, long>();

    public IReadOnlyDictionary<Resource, long> PopulationMax { get; init; } = new Dictionary<Resource, long>();

    /// <summary>Retired races (PlanHerd) are surplus entirely: Max is 0 and no breeding pair is kept.</summary>
    public IReadOnlyDictionary<Resource, bool> Retired { get; init; } = new Dictionary<Resource, bool>();

    public bool FeedShort { get; init; }

    /// <summary>
    /// The herd plan's role per race: taming ranks candidates by it (HerdTameLess) and training follows
    /// its job (HerdTrainOrder). Empty for a policy not built by PlanHerd.
    /// </summary>
    public IReadOnlyDictionary<Resource, HerdRole> Roles { get; init; } = new Dictionary<Resource, HerdRole>();

    /// <summary>
    /// The breeding rule of each fertilizable egg-laying race the plan observed; a race absent keeps
    /// HerdMalesPerFemales.
    /// </summary>
    public IReadOnlyDictionary<Resource, HerdLayer> Layers { get; init; } = new Dictionary<Resource, HerdLayer>();
}

/// <summary>
/// What the herd Need proposes: one Write, mirroring husbandry_method's single-write-per-cycle rule (a recursive
/// training or designation write can change other requested fields, so only one is ever chosen); NoDeficit when
/// nothing is owed; or Unread when the census or a planning input it needs is not read.
/// </summary>
public abstract record HusbandryChoice
{
    private protected HusbandryChoice() { }

    /// <summary>
    /// One write. Argument is the method's one parameter: the trainable def for Train, the master id or follow
    /// flag ("true"/"false") of HerdMasterChoice, the area for AllowedArea; empty for a designation.
    /// </summary>
    public sealed record Write(PawnId Animal, HusbandryMethod Method, string Argument = "") : HusbandryChoice;

    public sealed record NoDeficit : HusbandryChoice;

    public sealed record Unread : HusbandryChoice;
}

public static class HusbandryUpkeepRules
{
    /// <summary>The role of a race, or the zero role when the plan has none.</summary>
    internal static HerdRole HerdRoleOrZero(HerdPolicy herd, Resource race) =>
        herd.Roles.TryGetValue(race, out var role) ? role : new HerdRole();

    /// <summary>
    /// The trainables a race's job wants, in order. A hauler learns Obedience then Haul, a war animal
    /// Obedience, attack (Release) and Rescue as the race allows, any other job Obedience; a retiring or
    /// job-less race learns nothing.
    /// </summary>
    internal static IReadOnlyList<string> HerdTrainOrder(HerdPolicy herd, Resource race)
    {
        var role = HerdRoleOrZero(herd, race);
        var job = role.Job;
        if (role.Retiring || job == default || job == HerdJob.None)
        {
            return [];
        }
        if (job == HerdJob.Haul)
        {
            return ["Obedience", HerdPlanRules.HerdHaulTraining];
        }
        if (job == HerdJob.War)
        {
            return ["Obedience", HerdPlanRules.HerdWarTraining, "Rescue"];
        }
        return ["Obedience"];
    }

    /// <summary>The animal's trainables the job wants, in training order.</summary>
    internal static IReadOnlyList<HusbandryTrainable> HerdTrainQueue(HerdPolicy herd, UpkeepAnimal a)
    {
        var wanted = new List<HusbandryTrainable>();
        foreach (var def in HerdTrainOrder(herd, a.Definition))
        {
            foreach (var t in a.Training)
            {
                if (t.Def == def)
                {
                    wanted.Add(t);
                }
            }
        }
        return wanted;
    }

    /// <summary>
    /// Reports whether any observed animal still has an available, not-yet-learned trainable, any tracked
    /// race is below its minimum with a tameable wild candidate while feedShort (the herd's feed forecast
    /// reporting a shortfall) is known false, or -- only when a removal method is allowed and
    /// PopulationMax declares at least one tracked race -- any race has an observed surplus with an
    /// eligible candidate not yet designated. The deficit is unknown only while a wild candidate waits on an
    /// unread feed forecast.
    /// </summary>
    public static bool AnimalHerdDeficit(EquatableArray<UpkeepAnimal> animals, EquatableArray<UpkeepAnimal> wild, bool feedShort, HerdPolicy herd)
    {
        var deficit = false;
        foreach (var a in animals)
        {
            if (a.Release || a.Slaughter)
            {
                continue;
            }
            foreach (var t in HerdTrainQueue(herd, a))
            {
                if (t.Available && !t.Learned)
                {
                    deficit = true;
                }
            }
        }
        if (herd.PopulationMin.Count > 0)
        {
            if (HerdTameCandidates(animals, wild, herd).Count > 0)
            {
                if (!feedShort)
                {
                    deficit = true;
                }
            }
        }
        if (HerdSurplusRules.HerdSurplusCandidates(animals, herd.PopulationMax, herd.FeedShort, herd).Count > 0)
        {
            deficit = true;
        }
        return deficit;
    }

    /// <summary>
    /// Computes each tracked race's shortfall (shortfall = target.minimum - kept player animals - pending
    /// tame designations) and returns the best tameable wild animals of that race (HerdTameLess), capped
    /// to the shortfall. Kept excludes release/slaughter-designated animals, since those are leaving.
    /// The shortfall stops at the race's max (pen and feed room), and a dangerous race (HerdDangerous) is
    /// never tamed.
    /// </summary>
    internal static IReadOnlyList<UpkeepAnimal> HerdTameCandidates(IReadOnlyList<UpkeepAnimal> rows, IReadOnlyList<UpkeepAnimal> wild, HerdPolicy herd)
    {
        var populationMin = herd.PopulationMin;
        var counts = new Dictionary<Resource, long>();
        var eligible = new Dictionary<Resource, List<UpkeepAnimal>>();
        foreach (var a in rows)
        {
            if (!populationMin.ContainsKey(a.Definition))
            {
                continue;
            }
            if (a.Release || a.Slaughter)
            {
                continue;
            }
            counts[a.Definition] = counts.GetValueOrDefault(a.Definition) + 1;
        }
        foreach (var a in wild)
        {
            if (!populationMin.ContainsKey(a.Definition))
            {
                continue;
            }
            if (a.Tame)
            {
                counts[a.Definition] = counts.GetValueOrDefault(a.Definition) + 1;
                continue;
            }
            if (a.Tameable && !HerdSurplusRules.HerdDangerous(a))
            {
                if (!eligible.TryGetValue(a.Definition, out var list))
                {
                    list = [];
                    eligible[a.Definition] = list;
                }
                list.Add(a);
            }
        }
        var candidates = new List<UpkeepAnimal>();
        foreach (var (race, configured) in populationMin)
        {
            var minimum = configured;
            if (herd.PopulationMax.TryGetValue(race, out var ceiling))
            {
                minimum = Math.Min(minimum, ceiling);
            }
            var shortfall = minimum - counts.GetValueOrDefault(race);
            if (shortfall <= 0)
            {
                continue;
            }
            var role = HerdRoleOrZero(herd, race);
            var raceRows = eligible.GetValueOrDefault(race) is { } found ? found.SortedBy((x, y) => HerdTameLess(role, x, y)) : [];
            if (raceRows.Count > shortfall)
            {
                raceRows = raceRows.GetRange(0, (int)shortfall);
            }
            candidates.AddRange(raceRows);
        }
        return candidates.SortedBy((a, b) =>
        {
            var ra = HerdWantRank(HerdRoleOrZero(herd, a.Definition));
            var rb = HerdWantRank(HerdRoleOrZero(herd, b.Definition));
            if (ra != rb)
            {
                return ra < rb;
            }
            if (a.Definition != b.Definition)
            {
                return a.Definition < b.Definition;
            }
            return HerdTameLess(HerdRoleOrZero(herd, a.Definition), a, b);
        });
    }

    /// <summary>
    /// Orders races by how much the plan wants them: a preferred race first, then by job (HerdWorkJobs
    /// order), a race with no job last.
    /// </summary>
    internal static int HerdWantRank(HerdRole role)
    {
        var rank = -1;
        for (var i = 0; i < HerdPlanRules.HerdWorkJobs.Count; i++)
        {
            if (HerdPlanRules.HerdWorkJobs[i] == role.Job)
            {
                rank = i;
                break;
            }
        }
        if (rank < 0)
        {
            rank = HerdPlanRules.HerdWorkJobs.Count;
        }
        if (!role.Preferred)
        {
            rank += HerdPlanRules.HerdWorkJobs.Count + 1;
        }
        return rank;
    }

    /// <summary>
    /// Orders two wild animals of one race: the sex the plan is missing first (the founder rule), then the
    /// lower tame-fail risk, then the lower handling skill needed, then Id.
    /// </summary>
    internal static bool HerdTameLess(HerdRole role, UpkeepAnimal a, UpkeepAnimal b)
    {
        bool Wanted(UpkeepAnimal u) => (role.WantMale && u.Gender == Sex.Male) || (role.WantFemale && u.Gender == Sex.Female);
        var wa = Wanted(a);
        var wb = Wanted(b);
        if (wa != wb)
        {
            return wa;
        }
        var ra = a.Herd.ManhunterOnTameFail;
        var rb = b.Herd.ManhunterOnTameFail;
        if (ra != rb)
        {
            return ra < rb;
        }
        var sa = a.MinimumHandlingSkill;
        var sb = b.MinimumHandlingSkill;
        if (sa != sb)
        {
            return sa < sb;
        }
        return a.Id < b.Id;
    }

    /// <summary>
    /// Picks the lowest animal-Id, lowest-def-name available untrained trainable to dispatch next, trying
    /// training first the same way it always has. Only once no training candidate exists does it fall back
    /// to the first tame candidate in HerdTameLess order (the race the plan wants most, its missing sex,
    /// then the safest and easiest) for a race below its declared minimum that TamerFor finds a handler
    /// for at the animal's minimum handling skill (an unknown minimum asks only for a capable handler; an
    /// unknown roster leaves the choice unknown) -- and only while the herd's feed forecast is known not
    /// short, since a new mouth on a herd already short of feed deepens the animal feed runway's shortfall
    /// -- and only after that to the lowest-Id surplus candidate for the operator's chosen removal method
    /// -- so an operator who opts into tame or removal never loses the pre-existing training behavior.
    /// </summary>
    public static HusbandryChoice SelectHusbandryMethod(EquatableArray<UpkeepAnimal> animals, EquatableArray<UpkeepAnimal> wild, bool feedShort, HerdPolicy herd, EquatableArray<PawnProfile> handlers)
    {
        var sorted = animals.OrderBy(a => a.Id).ToList();
        foreach (var a in sorted)
        {
            if (a.Release || a.Slaughter)
            {
                continue;
            }
            foreach (var t in HerdTrainQueue(herd, a))
            {
                if (t.Available && !t.Learned)
                {
                    if (t.Wanted)
                    {
                        continue;
                    }
                    return new HusbandryChoice.Write(a.Id, HusbandryMethod.Train, t.Def);
                }
            }
        }
        if (herd.PopulationMin.Count > 0)
        {
            var candidates = HerdTameCandidates(animals, wild, herd);
            if (candidates.Count > 0)
            {
                var profiles = handlers;
                foreach (var a in candidates)
                {
                    var minimum = a.MinimumHandlingSkill;
                    var ok = PawnRules.TamerFor(profiles, minimum) is not null;
                    if (!feedShort && ok)
                    {
                        return new HusbandryChoice.Write(a.Id, HusbandryMethod.Tame);
                    }
                }
            }
        }
        var removals = HerdSurplusRules.HerdSurplusCandidates(animals, herd.PopulationMax, herd.FeedShort, herd);
        if (removals.Count > 0)
        {
            return new HusbandryChoice.Write(removals[0].Animal.Id, removals[0].Method);
        }
        return new HusbandryChoice.NoDeficit();
    }
}
