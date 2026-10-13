namespace RimGovernor.Brain.Policy;

// Go: work_assignment.go.

/// <summary>
/// A native WorkTypeDef defName as ListPawns reports it. Go's string type becomes an enum over the defs the
/// vanilla game and the supported DLC define; None is Go's empty string.
/// </summary>
public enum WorkType
{
    [WireName("")]
    None,

    // The routine goals' dispatch types (Go labor.go consts).
    [WireName("Construction")]
    Construction,

    [WireName("Research")]
    Research,

    [WireName("Mining")]
    Mining,

    [WireName("PlantCutting")]
    PlantCutting,

    [WireName("Crafting")]
    Crafting,

    [WireName("Hauling")]
    Hauling,

    [WireName("Cleaning")]
    Cleaning,

    [WireName("BasicWorker")]
    Basic,

    [WireName("Handling")]
    Handling,

    [WireName("Cooking")]
    Cooking,

    [WireName("Firefighter")]
    Firefighter,

    [WireName("Tailoring")]
    Tailoring,

    [WireName("Smithing")]
    Smithing,

    // work_assignment.go consts.
    [WireName("Doctor")]
    Doctor,

    [WireName("Warden")]
    Warden,

    [WireName("Hunting")]
    Hunting,

    [WireName("Fishing")]
    Fishing,

    [WireName("Growing")]
    Growing,

    [WireName("Art")]
    Art,

    [WireName("Patient")]
    Patient,

    [WireName("PatientBedRest")]
    PatientBedRest,

    // Pinned types the planner names as literals.
    [WireName("BedRest")]
    BedRest,

    [WireName("Childcare")]
    Childcare,

    // The Anomaly study work type (Go entity_study.go WorkDarkStudy).
    [WireName("DarkStudy")]
    DarkStudy,

    // Backstory-incapable "Violent" row (Go gear_loadout.go compares the wire name).
    [WireName("Violent")]
    Violent,
}

public sealed record WorkSkill
{
    public string Name { get; init; } = "";

    public int Level { get; init; }

    public string Passion { get; init; } = "";

    public bool Disabled { get; init; }

    /// <summary>The native stored level beneath aptitude offsets; zero when the read did not carry it.</summary>
    public int Stored { get; init; }
}

public sealed record WorkPriority
{
    public WorkType Work { get; init; }

    public int Priority { get; init; }

    public bool Disabled { get; init; }

    /// <summary>The work type's first relevant skill ("" for unskilled work), read from the WorkTypeDef when the row is read; empty on a row the planner writes back.</summary>
    public string Skill { get; init; } = "";

    /// <summary>The natural priority, read from the WorkTypeDef when the row is read (the higher Order is filled first); zero on a row the planner writes back.</summary>
    public int Order { get; init; }
}

public sealed record WorkPawn
{
    public PawnId Id { get; init; }

    /// <summary>Native finishing work capability, independent of draft, temporary mental state, schedule or current job.</summary>
    public Fact<bool> ConstructionAble { get; init; }

    public Fact<bool> Available { get; init; }

    public Fact<bool> Applies { get; init; }

    public Fact<bool> Manual { get; init; }

    public Fact<bool> Ranged { get; init; }

    public Fact<bool> Hunts { get; init; }

    public Fact<EquatableArray<WorkSkill>> Skills { get; init; }

    public Fact<EquatableArray<WorkPriority>> Work { get; init; }

    /// <summary>Traits, Incapable (backstory work types) and Age feed the pawn profile; unknown rows degrade the planner to skill-only ordering rather than making the review unknown.</summary>
    public Fact<EquatableArray<PawnTrait>> Traits { get; init; }

    public Fact<EquatableArray<WorkType>> Incapable { get; init; }

    public Fact<double> Age { get; init; }

    /// <summary>The current timetable, one TimeAssignmentDef per hour (hour 0 first); unknown when the read carried no complete timetable.</summary>
    public Fact<EquatableArray<string>> Schedule { get; init; }

    public Fact<FoodRestriction> FoodRestriction { get; init; }

    /// <summary>The MedicalCareCategory name from the settings block (carry tier); unknown when the read did not carry it.</summary>
    public Fact<string> MedicalCare { get; init; }

    /// <summary>The per-pawn policy planner inputs.</summary>
    public Fact<PawnPolicyInputs> PolicyInputs { get; init; }

    /// <summary>The pawn's effective eat trigger (food level, 0.30 vanilla); unknown where the read did not carry it.</summary>
    public Fact<double> EatTrigger { get; init; }

    /// <summary>The native body stats block (temperatures, move speed, hediff severities); unknown where the read did not carry it.</summary>
    public Fact<PawnBody> Body { get; init; }

    /// <summary>The pawn's current job; unknown when the read carried no job block.</summary>
    public Fact<PawnJob> Job { get; init; }

    /// <summary>The current InspirationDef defName; known "" is no inspiration, unknown is a read that did not carry it.</summary>
    public Fact<string> Inspiration { get; init; }

    /// <summary>The pawn's Biotech facts; unknown without Biotech.</summary>
    public Fact<PawnBiotech> Biotech { get; init; }

    /// <summary>The Assign-tab response (unknown where the pawn has none to configure).</summary>
    public Fact<HostilityResponse> Hostility { get; init; }

    /// <summary>Violence from the disabled work tags.</summary>
    public Fact<bool> ViolenceCapable { get; init; }

    public Fact<double> BloodLoss { get; init; }

    /// <summary>The catalog's serious BloodLoss severity; unknown without a catalog.</summary>
    public Fact<double> SeriousBloodLoss { get; init; }

    public Fact<double> Health { get; init; }

    /// <summary>playerSettings.selfTend; unknown when the read carried no care settings.</summary>
    public Fact<bool> SelfTend { get; init; }

    /// <summary>Need levels from the pawn needs block; unknown when the read carried no readable needs.</summary>
    public Fact<double> Rest { get; init; }

    public Fact<double> Joy { get; init; }

    public Fact<double> Mood { get; init; }

    /// <summary>The minor mental break threshold from the needs block.</summary>
    public Fact<double> BreakThreshold { get; init; }

    /// <summary>The social block's expectations pressure (mood tier).</summary>
    public Fact<bool> HighExpectations { get; init; }

    /// <summary>Psyfocus (0-1): known only for a psycaster; unknown without Royalty, a psylink or needs.</summary>
    public Fact<double> Psyfocus { get; init; }

    public Fact<double> PsyfocusTarget { get; init; }

    public Fact<int> PsylinkLevel { get; init; }

    /// <summary>The pawn's hostility inputs.</summary>
    public HostilityPawn HostilityPawn() =>
        new()
        {
            ID = Id,
            Current = Hostility,
            ViolenceCapable = ViolenceCapable,
            Age = Age,
            BloodLoss = BloodLoss,
            SeriousBloodLoss = SeriousBloodLoss,
            Health = Health,
            Job = Job,
        };
}

/// <summary>The native thing or cell a pawn's job works on.</summary>
public sealed record JobTarget
{
    public string Thing { get; init; } = "";

    public Fact<Cell> Cell { get; init; }
}

/// <summary>
/// One pawn's current job as the census saw it. Def is the JobDef name, empty for a pawn with no job; Work is the
/// WorkTypeDef of the work giver that issued it, None for a job no giver issued (a forced order, rest, a meal,
/// wandering).
/// </summary>
public sealed record PawnJob
{
    public string Def { get; init; } = "";

    public WorkType Work { get; init; }

    /// <summary>The thing or cell the job works; unknown from a producer that carried none, a known default JobTarget for a job with no target.</summary>
    public Fact<JobTarget> Target { get; init; }
}

public sealed record WorkRequirement
{
    public WorkType Work { get; init; }

    public string Skill { get; init; } = "";

    public int Minimum { get; init; }
}

public sealed record PawnWorkAssignment(PawnId Pawn, IReadOnlyList<WorkPriority> Priorities);

/// <summary>
/// The planner's per-work-type census: how many owners the colony wanted, how many it found and how many pawns
/// could do the work at all, so a goal can say "no capable miner" instead of stalling.
/// </summary>
public sealed record WorkCoverage
{
    public WorkType Work { get; init; }

    public int Demand { get; init; }

    public int Owners { get; init; }

    public int Capable { get; init; }
}

/// <summary>
/// A skill above 10 that no owner or secondary assignment exercises; the planner gives it a maintenance slot when
/// the pawn owns nothing else.
/// </summary>
public sealed record DecayingSkill(PawnId Pawn, string Skill, int Level);

/// <summary>
/// What a Rounds pass records of the roster planner for presentation: the coverage census, the decaying skills and
/// every work pawn's typed profile as of Tick. Presentation only; no planner reads it back.
/// </summary>
public sealed record WorkRosterReport
{
    public Tick Tick { get; init; }

    public EquatableArray<WorkCoverage> Coverage { get; init; }

    public EquatableArray<DecayingSkill> Decaying { get; init; }

    public EquatableArray<PawnProfile> Profiles { get; init; }

    /// <summary>The construction helper record: helpers, unmet ready work and the reason spare capacity is or is not used. The next review reads it back for hysteresis.</summary>
    public ConstructionHelpRecord? Help { get; init; }
}

public sealed record WorkDecision
{
    public IReadOnlyList<PawnWorkAssignment> Assignments { get; init; } = [];

    public Fact<bool> Capacity { get; init; }

    public Fact<bool> Matches { get; init; }

    public IReadOnlyList<WorkCoverage> Coverage { get; init; } = [];

    public IReadOnlyList<DecayingSkill> Decaying { get; init; } = [];

    /// <summary>The construction helper decision; null without WorkDemand.Help.</summary>
    public ConstructionHelpRecord? Help { get; init; }
}

/// <summary>The colony census the baseline owner table scales by. The default value asks for the baseline alone.</summary>
public sealed record WorkDemand
{
    /// <summary>Temporarily replaces the roster with medical rest until immunity.</summary>
    public IReadOnlyList<DiseaseRest> Resting { get; init; } = [];

    /// <summary>The sown/sowable field area; one grower per 150 cells beyond the first.</summary>
    public long GrowingCells { get; init; }

    /// <summary>Whether blueprints or frames are pending; the second constructor is wanted only then.</summary>
    public bool Construction { get; init; }

    /// <summary>The prisoner count; a warden is wanted only with one.</summary>
    public int Prisoners { get; init; }

    /// <summary>Enables construction helpers; null plans none.</summary>
    public ConstructionHelp? Help { get; init; }

    /// <summary>Raises Hauling and Cleaning from 4 to 3.</summary>
    public bool HaulBacklog { get; init; }

    /// <summary>Whether the herd plan holds a milk or wool job: a kept animal yields only to a handler, and the gather speed and yield stats scale with the Animals skill, so the best Animals pawn owns Handling.</summary>
    public bool Handling { get; init; }

    /// <summary>Whether the food plan opened a hunt that waits on a hunter's weapon (HuntArming): Hunting is then assignable to an unarmed colonist before the weapon exists, so the hunter-aware armory demand sees whom to arm.</summary>
    public bool Arming { get; init; }
}

/// <summary>One pawn in the planner's working set.</summary>
internal sealed class WorkWorker(WorkPawn pawn, PawnProfile profile)
{
    public WorkPawn Pawn { get; } = pawn;

    public PawnProfile Profile { get; } = profile;

    public Dictionary<WorkType, WorkPriority> Work { get; } = [];

    public int Load { get; set; }

    /// <summary>The planned priority per work type.</summary>
    public Dictionary<WorkType, int> Owns { get; } = [];

    /// <summary>Marks an unarmed colonist the plan is arming: Hunting is assignable before the weapon exists.</summary>
    public bool Arming { get; set; }

    public int Owned(WorkType work) => Owns.GetValueOrDefault(work);
}

internal sealed record WorkCandidate(WorkWorker Worker, double Fitness, int Level);

public static partial class PolicyFunctions
{
    /// <summary>The loot census count of unforbidden, safe stacks with storage headroom that makes a haul backlog.</summary>
    private const int HaulBacklogStacks = 20;

    /// <summary>The skill level below which a pawn is not capable of the work at all: a cook under 5 poisons meals, a doctor under 4 botches surgery, a constructor under 4 fails builds and wastes materials.</summary>
    private static int WorkFloor(WorkType work) =>
        work switch
        {
            WorkType.Cooking => 5,
            WorkType.Doctor or WorkType.Construction => 4,
            _ => 0,
        };

    // Pinned work is always priority 1 for every pawn (native defaults).
    private static bool PinnedWork(WorkType work) => work is WorkType.Firefighter or WorkType.Patient or WorkType.BedRest or WorkType.PatientBedRest or WorkType.Childcare;

    // The set whose missing owner is a capacity deficit (the work goal's Capacity fact): the roles a colony cannot run without.
    private static bool CoreWork(WorkType work) => work is WorkType.Doctor or WorkType.Cooking or WorkType.Construction or WorkType.Growing;

    private static bool BasicWork(WorkType work) => work is WorkType.Hauling or WorkType.Cleaning or WorkType.Basic;

    // The owner count the colony always wants per work type, before the review's requirements. Core roles that keep
    // a colony alive are always wanted; the rest are wanted when a natural specialist exists.
    internal static int BaselineDemand(WorkType work, int pawns, WorkDemand demand)
    {
        switch (work)
        {
            case WorkType.Doctor or WorkType.Construction or WorkType.PlantCutting or WorkType.Hunting:
                var n = 1;
                if (work == WorkType.Construction && demand.Construction && pawns >= 6)
                {
                    n = 2;
                }
                if (work == WorkType.Hunting && pawns >= 4)
                {
                    n = 2;
                }
                return n;
            case WorkType.Cooking:
                return 1 + (pawns / 8);
            case WorkType.Growing:
                return 1 + (int)(demand.GrowingCells / 150);
            case WorkType.Warden:
                if (demand.Prisoners > 0)
                {
                    return 1;
                }
                break;
            case WorkType.Handling:
                if (demand.Handling)
                {
                    return 1;
                }
                break;
        }
        return 0;
    }

    /// <summary>PlanWork with the baseline demand alone.</summary>
    public static WorkDecision AssignWork(IReadOnlyList<WorkPawn> pawns, IReadOnlyList<WorkRequirement> required) => PlanWork(pawns, required, new WorkDemand());

    /// <summary>
    /// The roster planner: every work type native reports gets owners by fitness (level, passion, trait work speed,
    /// incumbency), growth secondaries (a passion within five levels of the weakest owner) and, under numbered
    /// priorities, every capable pawn at 3 or 4, never what a trait forbids. A temporary disease rest hold zeroes a
    /// resting pawn's work. Governor owns every priority: no player edit is exempt. This is a proposal/readback
    /// comparison, never permission to change pawn settings. Throws on an invalid row.
    /// </summary>
    public static WorkDecision PlanWork(IReadOnlyList<WorkPawn> pawns, IReadOnlyList<WorkRequirement> required, WorkDemand demand)
    {
        ValidateDiseaseRest(demand.Resting);
        var resting = new HashSet<PawnId>();
        foreach (var row in demand.Resting)
        {
            resting.Add(row.Pawn);
        }
        var requirements = new Dictionary<WorkType, WorkRequirement>();
        foreach (var entry in required)
        {
            if (!ValidResource(new Resource(Wire.Name(entry.Work))) || (entry.Skill.Length != 0 && !ValidResource(new Resource(entry.Skill))) || entry.Minimum < 0 || entry.Minimum > 1000)
            {
                throw new DomainException("invalid work requirement");
            }
            if (!requirements.TryAdd(entry.Work, entry))
            {
                throw new DomainException("invalid work requirement");
            }
        }
        var workers = new List<WorkWorker>();
        var seen = new HashSet<PawnId>();
        var known = true;
        foreach (var pawn in pawns)
        {
            if (!ValidResource(new Resource(pawn.Id.Value)) || !seen.Add(pawn.Id))
            {
                throw new DomainException("invalid work pawn");
            }
            var ak = pawn.Available.TryGetValue(out var available);
            var pk = pawn.Applies.TryGetValue(out var applies);
            if ((ak && !available) || (pk && !applies))
            {
                continue;
            }
            if (!ak || !pk)
            {
                known = false;
                continue;
            }
            var mk = pawn.Manual.TryGetValue(out var manual);
            var sk = pawn.Skills.TryGetValue(out var skills);
            var wk = pawn.Work.TryGetValue(out var work);
            var rk = pawn.Ranged.IsKnown;

            // A checkbox-mode readback (native not yet flipped) is unknown.
            if (!mk || !manual || !sk || !wk || !rk)
            {
                known = false;
                continue;
            }
            var names = new HashSet<string>();
            foreach (var s in skills)
            {
                if (!ValidResource(new Resource(s.Name)) || s.Level < 0 || s.Level > 1000)
                {
                    throw new DomainException("invalid work skill");
                }
                if (!names.Add(s.Name))
                {
                    throw new DomainException("duplicate work skill");
                }
            }
            var w = new WorkWorker(pawn, BuildProfile(pawn));
            foreach (var value in work)
            {
                if (!ValidResource(new Resource(Wire.Name(value.Work))) || value.Priority < 0 || value.Priority > 4)
                {
                    throw new DomainException("invalid work priority");
                }
                if (!w.Work.TryAdd(value.Work, value))
                {
                    throw new DomainException("duplicate work type");
                }
            }
            workers.Add(w);
        }
        if (!known)
        {
            return new WorkDecision();
        }
        workers.Sort((a, b) => a.Pawn.Id.CompareTo(b.Pawn.Id));

        // Every work type native reports, in WorkTypeDef natural-priority order (the order the planner fills owners
        // in, so the scarcest roles pick first), ties by name; the skill of each is its row's.
        var rowSkill = new Dictionary<WorkType, string>();
        var rowOrder = new Dictionary<WorkType, int>();
        var types = new List<WorkType>();
        foreach (var w in workers)
        {
            foreach (var (name, row) in w.Work)
            {
                if (!rowOrder.ContainsKey(name) && !PinnedWork(name) && !BasicWork(name))
                {
                    types.Add(name);
                }
                rowOrder[name] = row.Order;
                rowSkill[name] = row.Skill;
            }
        }
        types.Sort((a, b) =>
        {
            if (rowOrder[a] != rowOrder[b])
            {
                return rowOrder[a] > rowOrder[b] ? -1 : 1;
            }
            return string.CompareOrdinal(Wire.Name(a), Wire.Name(b));
        });

        // Able lists who can do a work type at all, at or above its floor (a requirement raises the floor); the
        // incumbent bonus keeps owners stable across reviews when fitness is close.
        string SkillOf(WorkType work) => requirements.TryGetValue(work, out var r) && r.Skill.Length != 0 ? r.Skill : rowSkill.GetValueOrDefault(work, "");
        int FloorOf(WorkType work)
        {
            var floor = WorkFloor(work);
            if (requirements.TryGetValue(work, out var r) && r.Minimum > floor)
            {
                floor = r.Minimum;
            }
            return floor;
        }
        bool AbleAt(WorkWorker w, WorkType work, int floor)
        {
            if (resting.Contains(w.Pawn.Id) || !w.Work.TryGetValue(work, out var row) || row.Disabled || w.Profile.Forbidden(work) || w.Profile.Incapable.GetValueOrDefault(work))
            {
                return false;
            }
            if (work == WorkType.Hunting && !w.Profile.Ranged && !w.Arming)
            {
                return false;
            }
            var skill = SkillOf(work);
            if (skill.Length == 0)
            {
                return true;
            }
            var s = w.Profile.Skill(skill);
            return !s.Disabled && s.Level >= floor;
        }
        bool Able(WorkWorker w, WorkType work) => AbleAt(w, work, FloorOf(work));
        double Fitness(WorkWorker w, WorkType work)
        {
            var f = 10 * w.Profile.Effects.WorkSpeed;
            var skill = SkillOf(work);
            if (skill.Length != 0)
            {
                var s = w.Profile.Skill(skill);
                f += s.Level;
                switch (s.Passion)
                {
                    case "Minor":
                        f += 2;
                        break;
                    case "Major":
                        f += 4;
                        break;
                }
            }
            switch (work)
            {
                case WorkType.Hunting:
                    f += 5 * w.Profile.Effects.MoveSpeed;
                    break;
                case WorkType.Warden:
                    f += 5 * (double)w.Profile.Effects.Sociable;
                    if (w.Profile.Effects.Execution)
                    {
                        f++;
                    }
                    break;
                case WorkType.Doctor:
                    if (w.Profile.Effects.SurgeonSafe)
                    {
                        f++;
                    }
                    break;
            }
            if (w.Work[work].Priority == 1)
            {
                f += 1.5;
            }
            return f - (3 * (double)w.Load);
        }
        var pawnCount = workers.Count;
        if (demand.Arming)
        {
            // Arm the best unarmed colonists the baseline Hunting demand still lacks an armed owner for.
            var missing = BaselineDemand(WorkType.Hunting, pawnCount, demand);
            var unarmed = new List<WorkWorker>();
            foreach (var w in workers)
            {
                if (w.Profile.Ranged)
                {
                    if (Able(w, WorkType.Hunting))
                    {
                        missing--;
                    }
                    continue;
                }
                w.Arming = true;
                if (Able(w, WorkType.Hunting))
                {
                    unarmed.Add(w);
                }
                w.Arming = false;
            }
            var arming = unarmed.OrderByDescending(w => Fitness(w, WorkType.Hunting)).ToList();
            for (var i = 0; i < missing && i < arming.Count; i++)
            {
                arming[i].Arming = true;
            }
        }
        int DemandOf(WorkType work)
        {
            var n = BaselineDemand(work, pawnCount, demand);
            if (requirements.ContainsKey(work) && n == 0)
            {
                n = 1;
            }
            if (n == 0 && SkillOf(work).Length != 0)
            {
                // A natural specialist (level 6 or a passion) makes the type worth an owner even without a standing
                // need, so nobody with a skill is parked on hauling.
                foreach (var w in workers)
                {
                    var s = w.Profile.Skill(SkillOf(work));
                    if (Able(w, work) && (s.Level >= 6 || s.Passion.Length != 0))
                    {
                        return 1;
                    }
                }
            }
            return n;
        }
        var capacity = true;
        var coverageRows = new List<WorkCoverage>();
        var decaying = new List<DecayingSkill>();
        var owners = new Dictionary<WorkType, List<WorkWorker>>();
        foreach (var work in types)
        {
            var want = DemandOf(work);
            var coverage = new WorkCoverage { Work = work, Demand = want };
            var candidates = new List<WorkCandidate>();
            foreach (var w in workers)
            {
                if (!Able(w, work))
                {
                    continue;
                }
                coverage = coverage with { Capable = coverage.Capable + 1 };
                candidates.Add(new WorkCandidate(w, Fitness(w, work), w.Profile.Skill(SkillOf(work)).Level));
            }
            if (candidates.Count == 0 && want > 0)
            {
                // Nobody clears the safety floor: the best pawn the review's own minimum admits still owns the work
                // rather than leaving a core role (a cook under 5) empty; Capable stays zero so the coverage row says so.
                var floor = requirements.TryGetValue(work, out var r) ? r.Minimum : 0;
                foreach (var w in workers)
                {
                    if (AbleAt(w, work, floor))
                    {
                        candidates.Add(new WorkCandidate(w, Fitness(w, work), w.Profile.Skill(SkillOf(work)).Level));
                    }
                }
            }
            bool Less(WorkCandidate a, WorkCandidate b)
            {
                // Construction owners by raw level: a failed build wastes the materials, so speed and load never
                // outrank skill there. A hunter who also grows (or cooks) loses the field.
                if (work == WorkType.Construction && a.Level != b.Level)
                {
                    return a.Level > b.Level;
                }
                if (work == WorkType.Hunting)
                {
                    var ac = a.Worker.Owned(WorkType.Growing) == 1 || a.Worker.Owned(WorkType.Cooking) == 1;
                    var bc = b.Worker.Owned(WorkType.Growing) == 1 || b.Worker.Owned(WorkType.Cooking) == 1;
                    if (ac != bc)
                    {
                        return !ac;
                    }
                }
                if (a.Fitness != b.Fitness)
                {
                    return a.Fitness > b.Fitness;
                }
                if (a.Worker.Load != b.Worker.Load)
                {
                    return a.Worker.Load < b.Worker.Load;
                }
                return a.Worker.Pawn.Id.CompareTo(b.Worker.Pawn.Id) < 0;
            }
            candidates.Sort((a, b) => Less(a, b) ? -1 : Less(b, a) ? 1 : 0);
            var workOwners = new List<WorkWorker>();
            owners[work] = workOwners;
            for (var i = 0; i < candidates.Count && i < want; i++)
            {
                var c = candidates[i];
                c.Worker.Owns[work] = 1;
                c.Worker.Load++;
                workOwners.Add(c.Worker);
                coverage = coverage with { Owners = coverage.Owners + 1 };
            }
            if (want > 0 && coverage.Owners == 0 && (requirements.ContainsKey(work) || CoreWork(work)))
            {
                capacity = false;
            }

            // Secondaries: the next-best backup when the type has demand, and every passion within five levels of
            // the weakest owner so it trains beside the specialist (growth).
            var weakest = -1;
            foreach (var o in workOwners)
            {
                var l = o.Profile.Skill(SkillOf(work)).Level;
                if (weakest < 0 || l < weakest)
                {
                    weakest = l;
                }
            }
            for (var i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (c.Worker.Owned(work) == 1)
                {
                    continue;
                }
                var passion = SkillOf(work).Length != 0 && c.Worker.Profile.Skill(SkillOf(work)).Passion.Length != 0;
                if ((want > 0 && i == workOwners.Count) || (passion && weakest >= 0 && c.Level >= weakest - 5))
                {
                    c.Worker.Owns[work] = 2;
                }
            }
            coverageRows.Add(coverage);
        }
        ConstructionHelpRecord? help = null;
        if (demand.Help is not null)
        {
            // Helpers: sub-floor pawns the native floor admits, at the lowest rank; owners are not helpers.
            var nativeFloor = demand.Help.Floor;
            var rec = PlanConstructionHelp(
                demand.Help,
                workers,
                owners.GetValueOrDefault(WorkType.Construction) ?? [],
                w => w.Owned(WorkType.Construction) == 0 && !Able(w, WorkType.Construction) && AbleAt(w, WorkType.Construction, nativeFloor));
            var byId = workers.ToDictionary(w => w.Pawn.Id);
            foreach (var id in rec.Helpers)
            {
                byId[id].Owns[WorkType.Construction] = 4;
            }
            help = rec;
        }
        if (demand.Construction)
        {
            // Apprentices: a passionate pawn one level under the floor builds at the lowest rank while builds wait,
            // so the passion trains it past the floor instead of leaving two owners to build alone.
            foreach (var w in workers)
            {
                if (w.Owned(WorkType.Construction) != 0 || Able(w, WorkType.Construction))
                {
                    continue;
                }
                if (w.Profile.SkillFor(WorkType.Construction).Passion.Length != 0 && AbleAt(w, WorkType.Construction, FloorOf(WorkType.Construction) - 1))
                {
                    w.Owns[WorkType.Construction] = 4;
                }
            }
        }

        // Decay: a skill above 10 no owner or secondary slot exercises; a pawn owning nothing keeps it exercised at 2.
        foreach (var w in workers)
        {
            foreach (var name in SortedSkills(w.Profile))
            {
                var s = w.Profile.Skills[name];
                if (s.Level <= 10 || s.Disabled)
                {
                    continue;
                }
                var exercised = false;
                var maintain = WorkType.None;
                foreach (var work in types)
                {
                    if (SkillOf(work) != name)
                    {
                        continue;
                    }
                    var p = w.Owned(work);
                    if (p is 1 or 2)
                    {
                        exercised = true;
                    }
                    if (maintain == WorkType.None && Able(w, work))
                    {
                        maintain = work;
                    }
                }
                if (exercised || maintain == WorkType.None)
                {
                    continue;
                }
                if (w.Load == 0)
                {
                    w.Owns[maintain] = 2;
                    continue;
                }
                decaying.Add(new DecayingSkill(w.Pawn.Id, name, s.Level));
            }
        }
        var matches = true;
        var assignments = new List<PawnWorkAssignment>();
        foreach (var w in workers)
        {
            var priorities = new List<WorkPriority>();
            foreach (var (name, observed) in w.Work)
            {
                if (observed.Disabled)
                {
                    continue;
                }
                var priority = 0;
                if (PinnedWork(name))
                {
                    if (!w.Profile.Forbidden(name))
                    {
                        priority = 1;
                    }
                }
                else if (BasicWork(name))
                {
                    // Hauling and Cleaning sit below skilled work at 4, and rise to 3 only during a haul backlog; the research owner keeps them at 4.
                    priority = 3;
                    if (name != WorkType.Basic && (!demand.HaulBacklog || w.Owned(WorkType.Research) == 1))
                    {
                        priority = 4;
                    }
                }
                else
                {
                    priority = w.Owned(name);
                    if (priority == 0 && Able(w, name))
                    {
                        // Everyone capable: 3 with a working level, 4 while still low.
                        priority = 4;
                        var skill = SkillOf(name);
                        if (skill.Length == 0 || w.Profile.Skill(skill).Level >= 8)
                        {
                            priority = 3;
                        }
                    }
                }
                if (resting.Contains(w.Pawn.Id))
                {
                    priority = 0;
                    if (name is WorkType.Patient or WorkType.PatientBedRest or WorkType.BedRest)
                    {
                        priority = 1;
                    }
                }
                priorities.Add(new WorkPriority { Work = name, Priority = priority });
                matches = matches && observed.Priority == priority;
            }
            priorities.Sort((a, b) => string.CompareOrdinal(Wire.Name(a.Work), Wire.Name(b.Work)));
            assignments.Add(new PawnWorkAssignment(w.Pawn.Id, priorities));
        }
        return new WorkDecision
        {
            Capacity = Known(capacity),
            Matches = Known(matches),
            Assignments = assignments,
            Coverage = coverageRows,
            Decaying = decaying,
            Help = help,
        };
    }

    private static IReadOnlyList<string> SortedSkills(PawnProfile p) => [.. p.Skills.Keys.Order(StringComparer.Ordinal)];

    /// <summary>
    /// Derives the planner's demand census from the routine facts: field cells scale growers, pending blueprints
    /// (the definitions the review is building) want a second constructor and a prisoner wants a warden. Unknown
    /// facts fall back to the baseline.
    /// </summary>
    public static WorkDemand RoundsWorkDemand(RoundsFacts facts, bool building)
    {
        var jobs = PlanHerd(facts.HerdPlanInput()).Jobs;
        var demand = new WorkDemand
        {
            Construction = building,
            Arming = HuntArming(facts.FoodPlan),
            Handling = jobs.ContainsKey(HerdJob.Milk) || jobs.ContainsKey(HerdJob.Wool),
        };
        if (facts.GrowingCells.TryGetValue(out var cells))
        {
            demand = demand with { GrowingCells = cells };
        }
        if (facts.Prisoners.TryGetValue(out var prisoners))
        {
            demand = demand with { Prisoners = prisoners.Count };
        }
        if (facts.EventLoot.TryGetValue(out var loot))
        {
            var stacks = 0;
            foreach (var row in loot)
            {
                if (!row.Forbidden && row.SafetyKnown && row.SafeToHaul && row.StorageHeadroom.TryGetValue(out var room) && room > 0)
                {
                    stacks++;
                }
            }
            demand = demand with { HaulBacklog = stacks >= HaulBacklogStacks };
        }
        return demand;
    }

    /// <summary>
    /// The WorkSettingsIntent work rows an assignment needs on a pawn: the exact priorities the readback does not
    /// already hold. Ok is false when the pawn's readback lacks a planned work type or native has not yet switched
    /// the pawn to numbered priorities: unknown, wait.
    /// </summary>
    public static (IReadOnlyList<WorkSetting> Changed, bool Ok) WorkChanges(WorkPawn pawn, PawnWorkAssignment assignment)
    {
        var mk = pawn.Manual.TryGetValue(out var manual);
        var ck = pawn.Work.TryGetValue(out var current);
        if (!mk || !ck || !manual)
        {
            return ([], false);
        }
        var values = new Dictionary<WorkType, int>();
        foreach (var row in current)
        {
            values[row.Work] = row.Priority;
        }
        var changed = new List<WorkSetting>();
        foreach (var setting in assignment.Priorities)
        {
            if (!values.TryGetValue(setting.Work, out var old))
            {
                return ([], false);
            }
            if (old != setting.Priority)
            {
                changed.Add(new WorkSetting(Wire.Name(setting.Work), setting.Priority));
            }
        }
        return (changed, true);
    }
}
