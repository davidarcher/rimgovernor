namespace RimGovernor.Brain.Policy;


/// <summary>
/// A native WorkTypeDef defName as ListPawns reports it. It is an enum over the defs the
/// vanilla game and the supported DLC define; None is the empty string.
/// </summary>
public enum WorkType
{
    [WireName("")]
    None,

    // The routine goals' dispatch types.
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

    // The Anomaly study work type.
    [WireName("DarkStudy")]
    DarkStudy,

    // Backstory-incapable "Violent" row.
    [WireName("Violent")]
    Violent,

    // The mod's own work type (Defs/WorkTypeDefs/RimGovernorTraining.xml): range training and sparring.
    [WireName("RimGovernorTraining")]
    Training,

    /// <summary>A WorkTypeDef this build does not name (another mod's); the observation reads it as this value instead of failing the pass. It is never written back.</summary>
    [WireName("Unrecognized")]
    Unrecognized,
}

public sealed record WorkPriority
{
    public required RimGovernor.Brain.GameFacts.Observations.WorkSetting Observed { get; init; }
    public WorkType Work => WorkRules.WorkTypeOf(Observed.DefName);
    public int Priority => Observed.Priority;
    public bool Disabled => Observed.Disabled;

    /// <summary>A proposed work setting before it is written to the game.</summary>
    public static WorkPriority Planned(WorkType work, int priority, bool disabled = false) => new()
    {
        Observed = new() { DefName = Wire.Name(work), Priority = priority, Disabled = disabled },
    };

    /// <summary>The work type's first relevant skill ("" for unskilled work), read from the WorkTypeDef when the row is read; empty on a row the planner writes back.</summary>
    public string Skill { get; init; } = "";

    /// <summary>The natural priority, read from the WorkTypeDef when the row is read (the higher Order is filled first); zero on a row the planner writes back.</summary>
    public int Order { get; init; }
}

/// <summary>
/// A colonist's work settings as the planner reads them: <see cref="NeverWorks"/> for a pawn the game never gives
/// work; <see cref="Uninitialized"/> for a working pawn whose work tracker the game has not initialized, which has
/// no rows to plan or write yet; <see cref="Works"/> with the game's manual-priorities mode (false is checkbox mode,
/// which native has not yet switched) and the rows of the work types this build names.
/// </summary>
public abstract record PawnWorkTable
{
    private PawnWorkTable()
    {
    }

    public sealed record NeverWorks : PawnWorkTable;

    public sealed record Uninitialized : PawnWorkTable;

    public sealed record Works(RimGovernor.Brain.GameFacts.Observations.PawnWork.Works Observed, EquatableArray<WorkPriority> Rows) : PawnWorkTable
    {
        public bool Manual => Observed.ManualPriorities;
    }

    /// <summary>The rows of a working pawn with an initialized tracker; none otherwise.</summary>
    public EquatableArray<WorkPriority> Priorities() => this is Works works ? works.Rows : [];
}

/// <summary>A pawn's Biotech facts: <see cref="Active"/> with the row and its catalog-resolved effects, or <see cref="Inactive"/> without Biotech.</summary>
public abstract record PawnBiotechStatus
{
    private PawnBiotechStatus()
    {
    }

    public sealed record Active(PawnBiotech Facts) : PawnBiotechStatus;

    public sealed record Inactive : PawnBiotechStatus;
}

/// <summary>
/// Every free colonist from a complete native pawn census. Missing or contradictory native data fails decoding.
/// </summary>
public sealed record WorkPawnsRead(EquatableArray<WorkPawn> Pawns);

public sealed record WorkPawn
{
    public required RimGovernor.Brain.GameFacts.Observations.PawnState Observed { get; init; }
    public PawnId Id => new(Observed.Pawn.Id ?? throw new RequiredFactException("work pawn id"));
    public bool ConstructionAble => Observed.ConstructionCapable;
    public bool Available => !Observed.Dead && !Observed.Downed && !Observed.Drafted && Observed.MentalState is null
        && !(Biotech is PawnBiotechStatus.Active { Facts: var facts } && facts.IsDeathresting());
    public required PawnWorkTable Work { get; init; }
    public bool Applies => Work is not PawnWorkTable.NeverWorks;
    public required bool Ranged { get; init; }
    public required bool Hunts { get; init; }
    public EquatableArray<RimGovernor.Brain.GameFacts.Observations.Skill> Skills => Observed.Biography.Skills;
    public EquatableArray<RimGovernor.Brain.GameFacts.Observations.SkillStatCurve> SkillStatCurves => Observed.Biography.SkillStatCurves;
    public EquatableArray<RimGovernor.Brain.GameFacts.Observations.WorkSpeedRead> WorkSpeeds => Observed.Biography.WorkSpeeds;
    public required EquatableArray<PawnTrait> Traits { get; init; }
    public EquatableArray<WorkType> Incapable => EquatableArray.From(Observed.Biography.IncapableWorkTypes.Select(WorkRules.WorkTypeOf));
    public double Age => Observed.Biography.BiologicalAgeYears;
    public EquatableArray<string> Schedule => EquatableArray.From(Observed.Settings.Schedule.Select(slot => slot.AssignmentDefName));
    public bool FoodPolicyRespected => Observed.Settings.FoodPolicyRespected;
    public PawnCare Care => Observed.Settings.Care;
    public PawnPolicyInputs PolicyInputs => Observed.Settings.PolicyInputs;
    public PawnBodyStats Body => Observed.Settings.Body;
    public PawnJob Job => new() { Observed = Observed.Job };
    public required PawnBiotechStatus Biotech { get; init; }
    public bool ViolenceCapable => !Observed.Biography.DisabledWorkTags.Contains("Violent");
    public double BloodLoss => Observed.Health.BloodLoss;
    public required double SeriousBloodLoss { get; init; }
    public double Health => Observed.Health.SummaryFraction;
    public NeedLevel Rest => Observed.Needs.Rest;
    public NeedLevel Mood => Observed.Needs.Mood;
    public FoodNeed Food => Observed.Needs.Food;
    public double BreakThreshold => Observed.Needs.BreakThresholdMinor;
    public bool HighExpectations => Observed.Social.HighExpectations;
    public int PsylinkLevel => Observed.Needs.PsylinkLevel;
    /// <summary>The pawn's hostility inputs; null for a pawn without care settings, which has no configurable response.</summary>
    public HostilityPawn? HostilityPawn() =>
        Care is not PawnCare.Configured ? null : new(this);
}

/// <summary>
/// The native thing or cell a pawn's job works: <see cref="None"/> for a job with no target, a <see cref="Spawned"/>
/// thing at its cell, a <see cref="Held"/> thing something carries, or a <see cref="Ground"/> cell.
/// </summary>
public abstract record JobTarget
{
    private JobTarget()
    {
    }

    public sealed record None : JobTarget;

    public sealed record Spawned(string Id, Cell At) : JobTarget;

    public sealed record Held(string Id) : JobTarget;

    public sealed record Ground(Cell At) : JobTarget;

    /// <summary>The target thing's id; "" for a cell or no target.</summary>
    public string Thing => this is Spawned s ? s.Id : this is Held h ? h.Id : "";

    /// <summary>The cell the job works: a spawned thing's or the target cell. False for a held thing or no target.</summary>
    public bool CellOf(out Cell cell)
    {
        cell = this is Spawned s ? s.At : this is Ground g ? g.At : default;
        return this is Spawned or Ground;
    }
}

/// <summary>
/// One pawn's current job as the census saw it. Def is the JobDef name, empty for a pawn with no job; Work is the
/// WorkTypeDef of the work giver that issued it, None for a job no giver issued (a forced order, rest, a meal,
/// wandering).
/// </summary>
public sealed record PawnJob
{
    public required RimGovernor.Brain.GameFacts.Observations.JobEvidence Observed { get; init; }
    public string Def => Observed.DefName ?? "";
    public WorkType Work => WorkRules.WorkTypeOf(Observed.WorkTypeDefName);
    public JobTarget Target => Observed.DefName is null ? new JobTarget.None() : TargetOf(Observed);

    private static JobTarget TargetOf(RimGovernor.Brain.GameFacts.Observations.JobEvidence job)
    {
        var target = job.TargetA?.Target ?? throw new RequiredFactException($"job {job.DefName}: targetA");
        return target switch
        {
            RimGovernor.Brain.GameFacts.Observations.TargetRefTarget.EntityCase { Value.Id: { Length: > 0 } id, Place: RimGovernor.Brain.GameFacts.Observations.EntityPlace.OnMap at } => new JobTarget.Spawned(id, at.Cell),
            RimGovernor.Brain.GameFacts.Observations.TargetRefTarget.EntityCase { Value.Id: { Length: > 0 } id, Place: RimGovernor.Brain.GameFacts.Observations.EntityPlace.Unplaced } => new JobTarget.Held(id),
            RimGovernor.Brain.GameFacts.Observations.TargetRefTarget.CellCase cell => new JobTarget.Ground(cell.Value),
            RimGovernor.Brain.GameFacts.Observations.TargetRefTarget.UnavailableCase => new JobTarget.None(),
            var invalid => throw new RequiredFactException($"job {job.DefName}: invalid targetA {invalid}"),
        };
    }
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

/// <summary>Whether the roster planner's assignments cover the colony's work, as the review's planning pass read it.</summary>
public enum WorkCoverageState
{
    /// <summary>The planner did not plan this review (no census, an invalid read): nothing is known of the coverage.</summary>
    Unread,

    /// <summary>The planner planned and every wanted owner was found.</summary>
    Matched,

    /// <summary>The planner planned and work is uncovered, or a construction skill choice is owed.</summary>
    Unmatched,
}

/// <summary>The planner's per-work-type coverage rows as the review read them: <see cref="Read"/> once PlanWork planned, <see cref="Unread"/> when it did not.</summary>
public abstract record WorkRosterRead
{
    private WorkRosterRead()
    {
    }

    /// <summary>The state of a review the planner did not run for.</summary>
    public static readonly WorkRosterRead NotRead = new Unread();

    public sealed record Read(EquatableArray<WorkCoverage> Coverage) : WorkRosterRead;

    public sealed record Unread : WorkRosterRead;
}

/// <summary>
/// What a ReviewState pass records of the roster planner for presentation: the coverage census and
/// every work pawn's typed profile as of Tick. Presentation only; no planner reads it back.
/// </summary>
public sealed record WorkRosterReport
{
    public Tick Tick { get; init; }

    public EquatableArray<WorkCoverage> Coverage { get; init; }

    public EquatableArray<PawnProfile> Profiles { get; init; }

    /// <summary>The construction helper record: helpers, unmet ready work and the reason spare capacity is or is not used. The next review reads it back for hysteresis.</summary>
    public ConstructionHelpRecord? Help { get; init; }
}

/// <summary>Whether <see cref="WorkRules.PlanWork"/> planned, or why it waits.</summary>
public enum WorkDecisionState
{
    Planned,

    /// <summary>A pawn's work tab is still in checkbox mode: native has not yet switched it to numbered priorities.</summary>
    CheckboxMode,

}

public sealed record WorkDecision
{
    public IReadOnlyList<PawnWorkAssignment> Assignments { get; init; } = [];

    /// <summary>Whether the planner planned; an unplanned decision carries no assignments, coverage or match.</summary>
    public required WorkDecisionState State { get; init; }

    public bool Planned => State == WorkDecisionState.Planned;

    /// <summary>Whether the pawns' readback priorities already equal the plan.</summary>
    public required bool Matches { get; init; }

    public IReadOnlyList<WorkCoverage> Coverage { get; init; } = [];

    /// <summary>The construction helper decision; null without WorkDemand.Help.</summary>
    public ConstructionHelpRecord? Help { get; init; }
}

/// <summary>The colony census the baseline owner table scales by. The default value asks for the baseline alone.</summary>
public sealed record WorkDemand
{
    public NativeFoodAssessment Food { get; init; } = new NativeFoodAssessment.Unassessed("food_assessment_not_provided");
    /// <summary>Work types of currently observed native harvest/cooking options; priorities do not target a particular job or promise its output.</summary>
    public IReadOnlyList<WorkType> FoodWork { get; init; } = [];

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

    /// <summary>The player's unmet skill goals (#3508): each pawn also owns every work type of its skill it is able for, beside the owners fitness picks.</summary>
    public IReadOnlyList<SkillGoal> Training { get; init; } = [];
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

public static partial class WorkRules
{
    /// <summary>The loot census count of unforbidden, safe stacks with storage headroom that makes a haul backlog.</summary>
    private const int HaulBacklogStacks = 20;

    /// <summary>The skill level below which a pawn is not capable of the work at all: a cook under 5 poisons meals, a doctor under 4 botches surgery, a constructor under 4 fails builds and wastes materials.</summary>
    private static int WorkFloor(WorkType work) =>
        work is WorkType.Cooking ? 5 : work is WorkType.Doctor or WorkType.Construction ? 4 : 0;

    // Pinned work is always priority 1 for every pawn (native defaults).
    private static bool PinnedWork(WorkType work) => work is WorkType.Firefighter or WorkType.Patient or WorkType.BedRest or WorkType.PatientBedRest or WorkType.Childcare;

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
    /// incumbency), a next-best backup when the type has demand and, under numbered
    /// priorities, every capable pawn at 3 or 4, never what a trait forbids. A temporary disease rest hold zeroes a
    /// resting pawn's work. Governor owns every priority: no player edit is exempt. This is a proposal/readback
    /// comparison, never permission to change pawn settings. Throws on an invalid row.
    /// </summary>
    public static WorkDecision PlanWork(IReadOnlyList<WorkPawn> pawns, IReadOnlyList<WorkRequirement> required, WorkDemand demand)
    {
        DiseaseRestRules.ValidateDiseaseRest(demand.Resting);
        var resting = new HashSet<PawnId>();
        foreach (var row in demand.Resting)
        {
            resting.Add(row.Pawn);
        }
        var requirements = new Dictionary<WorkType, WorkRequirement>();
        foreach (var entry in required)
        {
            if (!AdmissionRules.ValidResource(new Resource(Wire.Name(entry.Work))) || (entry.Skill.Length != 0 && !AdmissionRules.ValidResource(new Resource(entry.Skill))) || entry.Minimum < 0 || entry.Minimum > 1000)
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
        var numbered = true;
        foreach (var pawn in pawns)
        {
            if (!AdmissionRules.ValidResource(new Resource(pawn.Id.Value)) || !seen.Add(pawn.Id))
            {
                throw new DomainException("invalid work pawn");
            }
            // A pawn that is unavailable, never works or whose work tracker is not initialized has no rows to plan.
            if (!pawn.Available || pawn.Work is not PawnWorkTable.Works { Manual: var manual, Rows: var work })
            {
                continue;
            }

            // A checkbox-mode readback (native not yet flipped) waits: no plan until it is numbered.
            if (!manual)
            {
                numbered = false;
                continue;
            }
            var names = new HashSet<string>();
            foreach (var s in pawn.Skills)
            {
                if (!AdmissionRules.ValidResource(new Resource(s.DefName)) || s.Level < 0 || s.Level > 1000)
                {
                    throw new DomainException("invalid work skill");
                }
                if (!names.Add(s.DefName))
                {
                    throw new DomainException("duplicate work skill");
                }
            }
            var w = new WorkWorker(pawn, PawnRules.BuildProfile(pawn));
            foreach (var value in work)
            {
                if (!AdmissionRules.ValidResource(new Resource(Wire.Name(value.Work))) || value.Priority < 0 || value.Priority > 4)
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
        if (!numbered)
        {
            return new WorkDecision { State = WorkDecisionState.CheckboxMode, Matches = false };
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
        var currentWork = workers.ToDictionary(w => w.Pawn.Id, w => ForecastCurrentWork.ForecastCurrentWorkOf(w.Pawn));
        // Protect only ordinary work that remains demanded and eligible. Needs/forced jobs are not productive
        // owner roles; medical holds and changed requirements still win. This reserves a primary role, not a day.
        // Current useful native work carries opportunity cost and retains its demanded role.
        var ongoing = new Dictionary<PawnId, WorkType>();
        foreach (var w in workers)
        {
            var current = currentWork[w.Pawn.Id];
            if (current.Work is var work
                && types.Contains(work) && DemandOf(work) > 0 && Able(w, work)
                && w.Work[work].Priority > 0)
                ongoing.Add(w.Pawn.Id, work);
        }
        var coverageRows = new List<WorkCoverage>();
        var owners = new Dictionary<WorkType, List<WorkWorker>>();
        foreach (var work in types)
        {
            var want = DemandOf(work);
            var coverage = new WorkCoverage { Work = work, Demand = want };
            var candidates = new List<WorkCandidate>();
            var workOwners = new List<WorkWorker>();
            owners[work] = workOwners;
            var backupChosen = false;
            foreach (var w in workers)
            {
                if (!Able(w, work))
                {
                    continue;
                }
                coverage = coverage with { Capable = coverage.Capable + 1 };
                candidates.Add(new WorkCandidate(w, Fitness(w, work), w.Profile.Skill(SkillOf(work)).Level));
            }
            if (coverage.Capable == 0 && want > 0)
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
                var aContinuing = ongoing.GetValueOrDefault(a.Worker.Pawn.Id) == work;
                var bContinuing = ongoing.GetValueOrDefault(b.Worker.Pawn.Id) == work;
                if (aContinuing != bContinuing) return aContinuing;
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
            for (var i = 0; i < candidates.Count && workOwners.Count < want; i++)
            {
                var c = candidates[i];
                if (ongoing.TryGetValue(c.Worker.Pawn.Id, out var protectedWork) && protectedWork != work
                    && rowOrder[work] <= rowOrder[protectedWork]) continue;
                c.Worker.Owns[work] = 1;
                c.Worker.Load++;
                workOwners.Add(c.Worker);
                coverage = coverage with { Owners = coverage.Owners + 1 };
            }
            // A skill goal's pawn trains by doing the work (#3508): it owns the type too, whatever the demand, and the
            // owners fitness picked keep it. The floors stay: a pawn below one is not able and does not own the work.
            foreach (var goal in demand.Training)
            {
                if (goal.Skill != SkillOf(work) || workers.FirstOrDefault(w => w.Pawn.Id == goal.Pawn) is not { } trainee
                    || trainee.Owned(work) == 1 || !Able(trainee, work))
                {
                    continue;
                }
                trainee.Owns[work] = 1;
                trainee.Load++;
                workOwners.Add(trainee);
                coverage = coverage with { Owners = coverage.Owners + 1 };
            }

            // Secondaries: the next-best backup when the type has demand.
            for (var i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (c.Worker.Owned(work) == 1)
                {
                    continue;
                }
                if (want > 0 && !backupChosen)
                {
                    c.Worker.Owns[work] = 2;
                    backupChosen = true;
                }
            }
            coverageRows.Add(coverage);
        }
        ConstructionHelpRecord? help = null;
        if (demand.Help is not null)
        {
            // Helpers: sub-floor pawns the native floor admits, at the lowest rank; owners are not helpers.
            var nativeFloor = demand.Help.Floor;
            var rec = ConstructionHelpersRules.PlanConstructionHelp(
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
        var matches = true;
        var assignments = new List<PawnWorkAssignment>();
        foreach (var w in workers)
        {
            var foodWorker = demand.Food.NeedsResponse && demand.FoodWork.Any(work => Able(w, work));
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
                // Adjust ordinary native preferences under a concrete food risk. Do not cancel
                // the current job or displace pinned needs/care or the doctor's primary role.
                if (foodWorker && !PinnedWork(name) && name != WorkType.Doctor && priority > 0)
                {
                    var wasPrimary = priority == 1;
                    if (demand.FoodWork.Contains(name) && Able(w, name)) priority = 1;
                    else if (priority == 1) priority = 2;
                    var coverageIndex = coverageRows.FindIndex(row => row.Work == name);
                    if (coverageIndex >= 0 && wasPrimary != (priority == 1))
                    {
                        var coverage = coverageRows[coverageIndex];
                        coverageRows[coverageIndex] = coverage with { Owners = coverage.Owners + (priority == 1 ? 1 : -1) };
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
                priorities.Add(WorkPriority.Planned(name, priority));
                matches = matches && observed.Priority == priority;
            }
            priorities.Sort((a, b) => string.CompareOrdinal(Wire.Name(a.Work), Wire.Name(b.Work)));
            assignments.Add(new PawnWorkAssignment(w.Pawn.Id, priorities));
        }
        return new WorkDecision
        {
            State = WorkDecisionState.Planned,
            Matches = matches,
            Assignments = assignments,
            Coverage = coverageRows,
            Help = help,
        };
    }

    /// <summary>The planner's hostility response of a configured pawn's wire response; native always names one.</summary>
    public static HostilityResponse HostilityOf(RimGovernor.Brain.GameFacts.Operations.HostilityResponse response)
    {
        switch (response)
        {
            case RimGovernor.Brain.GameFacts.Operations.HostilityResponse.Ignore:
                return HostilityResponse.Ignore;
            case RimGovernor.Brain.GameFacts.Operations.HostilityResponse.Attack:
                return HostilityResponse.Attack;
            case RimGovernor.Brain.GameFacts.Operations.HostilityResponse.Flee:
                return HostilityResponse.Flee;
        }
        throw new DomainException($"hostility response {response} names no mode");
    }

    /// <summary>The care tier of a configured pawn's wire medical care; native always names one.</summary>
    public static MedicalCare MedicalCareOf(RimGovernor.Brain.GameFacts.Operations.MedicalCare care)
    {
        switch (care)
        {
            case RimGovernor.Brain.GameFacts.Operations.MedicalCare.NoCare:
                return MedicalCare.None;
            case RimGovernor.Brain.GameFacts.Operations.MedicalCare.NoMedicine:
                return MedicalCare.NoMeds;
            case RimGovernor.Brain.GameFacts.Operations.MedicalCare.HerbalOrWorse:
                return MedicalCare.Herbal;
            case RimGovernor.Brain.GameFacts.Operations.MedicalCare.NormalOrWorse:
                return MedicalCare.Normal;
            case RimGovernor.Brain.GameFacts.Operations.MedicalCare.Best:
                return MedicalCare.Best;
        }
        throw new DomainException($"medical care {care} names no tier");
    }

    private static IReadOnlyList<string> SortedSkills(PawnProfile p) => [.. p.Skills.Keys.Order(StringComparer.Ordinal)];

    /// <summary>
    /// Derives the planner's demand census from the routine facts: field cells scale growers, pending blueprints
    /// (the definitions the review is building) want a second constructor and a prisoner wants a warden. Unknown
    /// facts fall back to the baseline.
    /// </summary>
    public static WorkDemand WorkDemand(ReviewFacts facts, bool building)
    {
        var jobs = HerdPlanRules.PlanHerd(facts.HerdPlanInput()).Jobs;
        var demand = new WorkDemand
        {
            Food = NativeFoodAssessment.Of(facts.FoodDays, facts.FoodPlan, facts.FoodReserve),
            Construction = building,
            Arming = HuntPrerequisiteRules.HuntArming(facts.FoodPlan),
            Handling = jobs.ContainsKey(HerdJob.Milk) || jobs.ContainsKey(HerdJob.Wool),
            Training = facts.GoalTraining.Items,
        };
        demand = demand with { GrowingCells = facts.GrowingCells };
        demand = demand with { Prisoners = facts.Population.Prisoners.Count };
        var stacks = 0;
        foreach (var row in facts.EventLoot)
        {
            if (!row.Forbidden && row.IsSafe && row.StorageHeadroom > 0)
            {
                stacks++;
            }
        }
        return demand with { HaulBacklog = stacks >= HaulBacklogStacks,
            FoodWork = demand.Food.NeedsResponse && facts.AvailableNeeds.Contains(FoodRules.FoodSupplyNeed) ? ReadyFoodWork(facts) : [] };
    }

    private static WorkType[] ReadyFoodWork(ReviewFacts facts)
    {
        if (facts.ForecastObserved.Coupled is not { } read) return [];
        var givers = read.Food.Options.Where(o => o.HarvestAllowed && o.Count > 0).Select(o => o.WorkGiver)
            .Concat(read.FoodConversions.Where(o => o.Cooking is RimGovernor.Brain.GameFacts.Observations.FoodCooking.Cook)
                .Select(o => o.WorkGiver));
        var work = new HashSet<WorkType>();
        foreach (var giver in givers)
        {
            // Without the catalog's native mapping this option cannot justify a priority change.
            if (!facts.WorkSpeeds.GiverWork.TryGetValue(giver, out var definition)) continue;
            var type = WorkTypeOf(definition);
            if (type is not (WorkType.None or WorkType.Unrecognized)) work.Add(type);
        }
        return work.Order().ToArray();
    }

    /// <summary>
    /// The WorkSettingsIntent work rows an assignment needs on a pawn: the exact priorities the readback does not
    /// already hold. Ok is false when the pawn's readback lacks a planned work type, has no initialized work rows or
    /// native has not yet switched the pawn to numbered priorities: wait.
    /// </summary>
    public static (IReadOnlyList<WorkSetting> Changed, bool Ok) WorkChanges(WorkPawn pawn, PawnWorkAssignment assignment)
    {
        if (pawn.Work is not PawnWorkTable.Works { Manual: true, Rows: var current })
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

    /// <summary>Every field an undispatched assignment writes must still be wanted. Additional fresh changes do not invalidate an unchanged field.</summary>
    public static bool WorkAssignmentStale(WorkAssignment assignment, IReadOnlyDictionary<PawnId, WorkAssignment> wanted)
    {
        if (!wanted.TryGetValue(assignment.Pawn, out var now)) return true;
        if (assignment.HasSchedule && !assignment.Schedule.SequenceEqual(now.Schedule)) return true;
        if (assignment.HasArea && (!now.HasArea || assignment.AreaClear != now.AreaClear || assignment.Area != now.Area)) return true;
        var priorities = now.Settings.ToDictionary(s => s.Definition, s => s.Priority, StringComparer.Ordinal);
        return assignment.Settings.Any(s => !priorities.TryGetValue(s.Definition, out var priority) || priority != s.Priority);
    }
}
