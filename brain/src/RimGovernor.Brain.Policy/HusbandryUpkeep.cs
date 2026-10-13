namespace RimGovernor.Brain.Policy;

public static partial class PolicyConstants
{
    /// <summary>
    /// Names MaintainHerd-*'s deficit: any observed, non-release/slaughter-flagged animal with an
    /// available-but-untrained trainable; any race below its food-derived population floor with a
    /// tameable wild animal on the map while the herd's feed forecast reports no shortfall; and any race
    /// above its wealth-scaled cap (PlanHerd) with a removable animal (HerdSurplusCandidates). Removal
    /// needs no opt-in: it is slaughter whenever SafeToSlaughter allows it, release only when slaughter
    /// is refused and ReleaseAllowed allows it, and never breaks the last breeding pair. Native checks
    /// eligibility again when the HusbandryIntent applies.
    /// </summary>
    public static readonly ConcernId MaintainHerd = new("MaintainHerd");
}

/// <summary>
/// The per-race population band MaintainHerd plans from. PopulationMin drives tame designations on wild
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

/// <summary>Names why RoundsHusbandryPlanner did or did not propose a training write.</summary>
public enum HusbandryPlanReason
{
    [WireName("")]
    None = 0,
    NoDeficit,
    Unknown,
}

/// <summary>
/// The one animal/method pair chosen for a write, mirroring husbandry_method's single-write-per-cycle rule
/// (a recursive training or designation write can change other requested fields, so only one is ever
/// chosen). Method is set whenever Reason is None (a choice was made); TrainableDef is only ever set for
/// HusbandryMethod.Train.
/// </summary>
public sealed record HusbandryChoice
{
    public HusbandryPlanReason Reason { get; init; }

    public PawnId Animal { get; init; }

    public HusbandryMethod? Method { get; init; }

    public string TrainableDef { get; init; } = string.Empty;

    /// <summary>The master id or follow flag ("true"/"false") of HusbandryMaster and the follow methods (HerdMasterChoice).</summary>
    public string Argument { get; init; } = string.Empty;
}

public static partial class PolicyFunctions
{
    /// <summary>The role of a race, or the zero role when the plan has none.</summary>
    internal static HerdRole HerdRoleOrZero(HerdPolicy herd, Resource race) =>
        herd.Roles.TryGetValue(race, out var role) ? role : new HerdRole(race, default, false, false, false, false, false, false);

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
            return ["Obedience", HerdHaulTraining];
        }
        if (job == HerdJob.War)
        {
            return ["Obedience", HerdWarTraining, "Rescue"];
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
    /// eligible candidate not yet designated. An unknown census, or any animal whose
    /// release/slaughter/training facts are incomplete, leaves the whole need unknown rather than
    /// silently treating it as recovered.
    /// </summary>
    public static Fact<bool> AnimalHerdDeficit(Fact<IReadOnlyList<UpkeepAnimal>> animals, Fact<IReadOnlyList<UpkeepAnimal>> wild, Fact<bool> feedShort, HerdPolicy herd)
    {
        if (!animals.TryGetValue(out var rows))
        {
            return Unknown<bool>();
        }
        var deficit = false;
        foreach (var a in rows)
        {
            var rk = a.Release.TryGetValue(out var release);
            var sk = a.Slaughter.TryGetValue(out var slaughter);
            if (!rk || !sk)
            {
                return Unknown<bool>();
            }
            if (release || slaughter)
            {
                continue;
            }
            foreach (var t in HerdTrainQueue(herd, a))
            {
                if (!t.Available.TryGetValue(out var avail))
                {
                    return Unknown<bool>();
                }
                if (!avail)
                {
                    continue;
                }
                if (!t.Learned.TryGetValue(out var learned))
                {
                    return Unknown<bool>();
                }
                if (!learned)
                {
                    deficit = true;
                }
            }
        }
        if (herd.PopulationMin.Count > 0)
        {
            if (!wild.TryGetValue(out var wildRows))
            {
                return Unknown<bool>();
            }
            var (candidates, unknown) = HerdTameCandidates(rows, wildRows, herd);
            if (unknown)
            {
                return Unknown<bool>();
            }
            if (candidates.Count > 0)
            {
                if (!feedShort.TryGetValue(out var short_))
                {
                    return Unknown<bool>();
                }
                if (!short_)
                {
                    deficit = true;
                }
            }
        }
        var (removals, removalUnknown) = HerdSurplusCandidates(rows, herd.PopulationMax, herd.FeedShort, herd);
        if (removalUnknown)
        {
            return Unknown<bool>();
        }
        if (removals.Count > 0)
        {
            deficit = true;
        }
        return Known(deficit);
    }

    /// <summary>
    /// Computes each tracked race's shortfall (shortfall = target.minimum - kept player animals - pending
    /// tame designations) and returns the best tameable wild animals of that race (HerdTameLess), capped
    /// to the shortfall. Kept excludes release/slaughter-designated animals, since those are leaving. A
    /// wild row with an unknown tameable or tame-designation fact, or a player row with unknown
    /// designation facts, makes the result unknown.
    /// The shortfall stops at the race's max (pen and feed room), and a dangerous race (HerdDangerous) is
    /// never tamed.
    /// </summary>
    internal static (IReadOnlyList<UpkeepAnimal> Candidates, bool Unknown) HerdTameCandidates(IReadOnlyList<UpkeepAnimal> rows, IReadOnlyList<UpkeepAnimal> wild, HerdPolicy herd)
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
            counts[a.Definition] = counts.GetValueOrDefault(a.Definition) + 1;
        }
        foreach (var a in wild)
        {
            if (!populationMin.ContainsKey(a.Definition))
            {
                continue;
            }
            var dk = a.Tame.TryGetValue(out var designated);
            var tk = a.Tameable.TryGetValue(out var tameable);
            if (!dk || !tk)
            {
                return ([], true);
            }
            if (designated)
            {
                counts[a.Definition] = counts.GetValueOrDefault(a.Definition) + 1;
                continue;
            }
            if (tameable && !HerdDangerous(a))
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
        return (candidates.SortedBy((a, b) =>
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
        }), false);
    }

    /// <summary>
    /// Orders races by how much the plan wants them: a preferred race first, then by job (HerdWorkJobs
    /// order), a race with no job last.
    /// </summary>
    internal static int HerdWantRank(HerdRole role)
    {
        var rank = -1;
        for (var i = 0; i < HerdWorkJobs.Count; i++)
        {
            if (HerdWorkJobs[i] == role.Job)
            {
                rank = i;
                break;
            }
        }
        if (rank < 0)
        {
            rank = HerdWorkJobs.Count;
        }
        if (!role.Preferred)
        {
            rank += HerdWorkJobs.Count + 1;
        }
        return rank;
    }

    /// <summary>
    /// Orders two wild animals of one race: the sex the plan is missing first (the founder rule), then the
    /// lower tame-fail risk, then the lower handling skill needed, then Id.
    /// </summary>
    internal static bool HerdTameLess(HerdRole role, UpkeepAnimal a, UpkeepAnimal b)
    {
        bool Wanted(UpkeepAnimal u) => (role.WantMale && u.Gender == "Male") || (role.WantFemale && u.Gender == "Female");
        var wa = Wanted(a);
        var wb = Wanted(b);
        if (wa != wb)
        {
            return wa;
        }
        var ra = a.Herd.ManhunterOnTameFail.ValueOrDefault;
        var rb = b.Herd.ManhunterOnTameFail.ValueOrDefault;
        if (ra != rb)
        {
            return ra < rb;
        }
        var sa = a.MinimumHandlingSkill.ValueOrDefault;
        var sb = b.MinimumHandlingSkill.ValueOrDefault;
        if (sa != sb)
        {
            return sa < sb;
        }
        return a.Id < b.Id;
    }

    /// <summary>
    /// The feed gate SelectHusbandryMethod's tame fallback reads from the animal feed runway: known true
    /// while any race group's stock and pasture fall short of the horizon, unknown while the projection is.
    /// </summary>
    public static Fact<bool> HerdFeedShort(Fact<AnimalFeedProjection> projection)
    {
        if (!projection.TryGetValue(out var p))
        {
            return Unknown<bool>();
        }
        return Known(p.Short());
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
    public static HusbandryChoice SelectHusbandryMethod(Fact<IReadOnlyList<UpkeepAnimal>> animals, Fact<IReadOnlyList<UpkeepAnimal>> wild, Fact<bool> feedShort, HerdPolicy herd, Fact<IReadOnlyList<PawnProfile>> handlers)
    {
        var unknownChoice = new HusbandryChoice { Reason = HusbandryPlanReason.Unknown };
        if (!animals.TryGetValue(out var rows))
        {
            return unknownChoice;
        }
        var sorted = rows.OrderBy(a => a.Id).ToList();
        foreach (var a in sorted)
        {
            if (a.Release.TryGetValue(out var release) && release)
            {
                continue;
            }
            if (a.Slaughter.TryGetValue(out var slaughter) && slaughter)
            {
                continue;
            }
            foreach (var t in HerdTrainQueue(herd, a))
            {
                var ak = t.Available.TryGetValue(out var avail);
                var lk = t.Learned.TryGetValue(out var learned);
                if (ak && avail && lk && !learned)
                {
                    if (!t.Wanted.TryGetValue(out var wanted))
                    {
                        return unknownChoice;
                    }
                    if (wanted)
                    {
                        continue;
                    }
                    return new HusbandryChoice { Animal = a.Id, Method = HusbandryMethod.Train, TrainableDef = t.Def };
                }
            }
        }
        if (herd.PopulationMin.Count > 0)
        {
            if (!wild.TryGetValue(out var wildRows))
            {
                return unknownChoice;
            }
            var (candidates, unknown) = HerdTameCandidates(rows, wildRows, herd);
            if (unknown)
            {
                return unknownChoice;
            }
            if (candidates.Count > 0)
            {
                if (!feedShort.TryGetValue(out var short_) || !handlers.TryGetValue(out var profiles))
                {
                    return unknownChoice;
                }
                foreach (var a in candidates)
                {
                    var minimum = a.MinimumHandlingSkill.ValueOrDefault;
                    var (_, ok) = TamerFor(profiles, minimum);
                    if (!short_ && ok)
                    {
                        return new HusbandryChoice { Animal = a.Id, Method = HusbandryMethod.Tame };
                    }
                }
            }
        }
        var (removals, removalUnknown) = HerdSurplusCandidates(rows, herd.PopulationMax, herd.FeedShort, herd);
        if (removalUnknown)
        {
            return unknownChoice;
        }
        if (removals.Count > 0)
        {
            return new HusbandryChoice { Animal = removals[0].Animal.Id, Method = removals[0].Method };
        }
        return new HusbandryChoice { Reason = HusbandryPlanReason.NoDeficit };
    }

    /// <summary>
    /// Requested training or a standing animal designation. Ordinary native handlers need game time, not
    /// another write or a forced job.
    /// </summary>
    public static bool HerdWorkPending(Fact<IReadOnlyList<UpkeepAnimal>> animals, Fact<IReadOnlyList<UpkeepAnimal>> wild, HerdPolicy herd)
    {
        if (wild.TryGetValue(out var wildRows))
        {
            foreach (var a in wildRows)
            {
                if (a.Tame.TryGetValue(out var tame) && tame)
                {
                    return true;
                }
            }
        }
        if (animals.TryGetValue(out var rows))
        {
            foreach (var a in rows)
            {
                if (a.Release.TryGetValue(out var release) && release)
                {
                    return true;
                }
                if (a.Slaughter.TryGetValue(out var slaughter) && slaughter)
                {
                    return true;
                }
                foreach (var t in HerdTrainQueue(herd, a))
                {
                    var ak = t.Available.TryGetValue(out var available);
                    var lk = t.Learned.TryGetValue(out var learned);
                    var wk = t.Wanted.TryGetValue(out var wanted);
                    if (ak && available && lk && !learned && wk && wanted)
                    {
                        return true;
                    }
                }
            }
        }
        return false;
    }
}
