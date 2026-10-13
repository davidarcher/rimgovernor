using RimGovernor.Brain.Domain;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// One native trait row: the TraitDef name and, for spectrum traits (Industriousness, NaturalMood,
/// SpeedOffset, ...), its degree. Effects are the trait's typed effects, resolved from the definition catalog
/// when the pawn is read (DefinitionCatalog.TraitEffects).
/// </summary>
public sealed record PawnTrait
{
    public string Name { get; init; } = "";

    public int Degree { get; init; }

    public TraitEffects Effects { get; init; } = new();
}

/// <summary>
/// What the planner and the other pawn-facing goals read from a pawn's traits, typed so no policy matches
/// trait names itself. A trait's effects are derived from its catalog row when the pawn is read
/// (DefinitionCatalog.TraitEffects: stat offsets, disabled work, needs and ingestion thoughts and hunger
/// rate), plus the sociability the game applies in code (TraitSociable).
/// </summary>
public sealed record TraitEffects
{
    /// <summary>The summed WorkSpeedGlobal offset (Industrious +0.35, Slothful -0.35, Very neurotic +0.40).</summary>
    public double WorkSpeed { get; init; }

    /// <summary>The summed GlobalLearningFactor offset (FastLearner and TooSmart +0.75 each, SlowLearner -0.75).</summary>
    public double LearnRate { get; init; }

    /// <summary>The summed MoveSpeed offset (Jogger +0.4, Slowpoke -0.2).</summary>
    public double MoveSpeed { get; init; }

    /// <summary>Rests twice as fast: a shorter Sleep block suffices.</summary>
    public bool QuickSleeper { get; init; }

    /// <summary>NightOwl wants the pawn awake 23h-6h and asleep by day.</summary>
    public bool NightShift { get; init; }

    /// <summary>Brawler forbids a ranged weapon and so the Hunting owner role.</summary>
    public bool MeleeOnly { get; init; }

    /// <summary>Tough, Nimble, Brawler prefer the melee line.</summary>
    public bool FrontLine { get; init; }

    /// <summary>Careful shooter, trigger-happy prefer the ranged line.</summary>
    public bool RearRanged { get; init; }

    /// <summary>
    /// The work types the trait's disabled work tags and disabled work types take away (Pyromaniac:
    /// Firefighter).
    /// </summary>
    public IReadOnlyList<WorkType> DisabledWork { get; init; } = [];

    /// <summary>Pyromaniac also orders break containment away from flammable stores.</summary>
    public bool Pyromaniac { get; init; }

    /// <summary>
    /// Orders warden, recruiter and trader candidates: Kind +1, Abrasive -1 (never warden or trader while
    /// another candidate exists).
    /// </summary>
    public int Sociable { get; init; }

    /// <summary>
    /// A warden who executes a prisoner without the mood loss of a prisoner dying (the trait nullifies that
    /// thought).
    /// </summary>
    public bool Execution { get; init; }

    /// <summary>A surgeon who harvests without a mood loss.</summary>
    public bool SurgeonSafe { get; init; }

    /// <summary>Wears a dead man's apparel without the mood loss (each trait nullifies the thought).</summary>
    public bool TaintFree { get; init; }

    /// <summary>A pawn who butchers a human without the mood loss (the trait nullifies the butchered-humanlike thought).</summary>
    public bool HumanButcher { get; init; }

    // Apparel and diet flags the gear and food planners consume.
    public bool Nudist { get; init; }

    public bool Ascetic { get; init; }

    public bool Cannibal { get; init; }

    public bool Gourmand { get; init; }

    /// <summary>The DrugDesire degree: 1 interest, 2 fascination, -1 teetotaler.</summary>
    public int ChemicalInterest { get; init; }

    // Room provisioning flags: Undergrounder wants no windows or outdoors, Greedy an impressive room,
    // Jealous no better room than his.
    public bool Undergrounder { get; init; }

    public bool Greedy { get; init; }

    public bool Jealous { get; init; }

    /// <summary>Combines two effect sets: offsets and counts sum, flags join.</summary>
    public TraitEffects Add(TraitEffects o) => this with
    {
        WorkSpeed = WorkSpeed + o.WorkSpeed,
        LearnRate = LearnRate + o.LearnRate,
        MoveSpeed = MoveSpeed + o.MoveSpeed,
        QuickSleeper = QuickSleeper || o.QuickSleeper,
        NightShift = NightShift || o.NightShift,
        MeleeOnly = MeleeOnly || o.MeleeOnly,
        FrontLine = FrontLine || o.FrontLine,
        RearRanged = RearRanged || o.RearRanged,
        Pyromaniac = Pyromaniac || o.Pyromaniac,
        Sociable = Sociable + o.Sociable,
        Execution = Execution || o.Execution,
        SurgeonSafe = SurgeonSafe || o.SurgeonSafe,
        TaintFree = TaintFree || o.TaintFree,
        HumanButcher = HumanButcher || o.HumanButcher,
        Nudist = Nudist || o.Nudist,
        Ascetic = Ascetic || o.Ascetic,
        Cannibal = Cannibal || o.Cannibal,
        Gourmand = Gourmand || o.Gourmand,
        ChemicalInterest = ChemicalInterest + o.ChemicalInterest,
        Undergrounder = Undergrounder || o.Undergrounder,
        Greedy = Greedy || o.Greedy,
        Jealous = Jealous || o.Jealous,
        DisabledWork = DisabledWork.Concat(o.DisabledWork).Distinct().OrderBy(w => Wire.Name(w), StringComparer.Ordinal).ToList(),
    };

    /// <summary>Names the effect flags that are set, in field order, and a No&lt;WorkType&gt; for each disabled work type.</summary>
    public IReadOnlyList<string> Flags()
    {
        var result = new List<string>();
        void Add(bool set, string name)
        {
            if (set)
            {
                result.Add(name);
            }
        }
        Add(QuickSleeper, nameof(QuickSleeper));
        Add(NightShift, nameof(NightShift));
        Add(MeleeOnly, nameof(MeleeOnly));
        Add(FrontLine, nameof(FrontLine));
        Add(RearRanged, nameof(RearRanged));
        Add(Pyromaniac, nameof(Pyromaniac));
        Add(Execution, nameof(Execution));
        Add(SurgeonSafe, nameof(SurgeonSafe));
        Add(TaintFree, nameof(TaintFree));
        Add(HumanButcher, nameof(HumanButcher));
        Add(Nudist, nameof(Nudist));
        Add(Ascetic, nameof(Ascetic));
        Add(Cannibal, nameof(Cannibal));
        Add(Gourmand, nameof(Gourmand));
        Add(Undergrounder, nameof(Undergrounder));
        Add(Greedy, nameof(Greedy));
        Add(Jealous, nameof(Jealous));
        foreach (var work in DisabledWork)
        {
            result.Add("No" + Wire.Name(work));
        }
        return result;
    }
}

/// <summary>
/// One skill as the planner scores it: the effective level (with aptitude offsets, what the game applies),
/// the stored level native keeps beneath it (what learning raises) and the passion.
/// </summary>
public sealed record ProfileSkill
{
    public string Name { get; init; } = "";

    public int Level { get; init; }

    public int Stored { get; init; }

    public string Passion { get; init; } = "";

    public bool Disabled { get; init; }

    /// <summary>The passion learning multiplier (no passion 0.35, minor 1.0, major 1.5) scaled by the pawn's global learning offset.</summary>
    public double LearnFactor(TraitEffects effects)
    {
        if (Disabled)
        {
            return 0;
        }
        var rate = Passion switch
        {
            "Minor" => 1.0,
            "Major" => 1.5,
            _ => 0.35,
        };
        return rate * (1 + effects.LearnRate);
    }
}

/// <summary>
/// Who a pawn is for assignment purposes: skills, traits and backstory incapabilities as one typed view over
/// the routine read.
/// </summary>
public sealed record PawnProfile
{
    public PawnId Id { get; init; }

    public IReadOnlyDictionary<string, ProfileSkill> Skills { get; init; } = new Dictionary<string, ProfileSkill>();

    public IReadOnlyList<PawnTrait> Traits { get; init; } = [];

    public TraitEffects Effects { get; init; } = new();

    public IReadOnlyDictionary<WorkType, bool> Incapable { get; init; } = new Dictionary<WorkType, bool>();

    /// <summary>
    /// The skill each work type is scored by, from the pawn's work rows (the WorkTypeDef's first relevant
    /// skill); a work type absent or unskilled has none.
    /// </summary>
    public IReadOnlyDictionary<WorkType, string> WorkSkill { get; init; } = new Dictionary<WorkType, string>();

    /// <summary>The biological age in years.</summary>
    public double Age { get; init; }

    /// <summary>
    /// A pre-adult developmental stage (Newborn, Baby, Child) read from the Biotech facts. A colony without
    /// Biotech has no Biotech block and no child pawns: native never spawns one under 14 there, so Child is
    /// false.
    /// </summary>
    public bool Child { get; init; }

    /// <summary>The pawn's Biotech facts: life stage, genes and the rest; unknown without Biotech.</summary>
    public Fact<PawnBiotech> Biotech { get; init; }

    /// <summary>The active genes' typed effects; zero when unknown. Their stat modifiers are already folded into Effects.</summary>
    public GeneEffects Genes { get; init; } = new();

    /// <summary>The race's minimum age per work type for a child; a work type absent has no minimum.</summary>
    public IReadOnlyDictionary<WorkType, int> WorkMinAge { get; init; } = new Dictionary<WorkType, int>();

    /// <summary>Whether the pawn's primary weapon is ranged.</summary>
    public bool Ranged { get; init; }

    /// <summary>Whether the primary weapon hunts (WeaponDef.Hunts).</summary>
    public bool Hunts { get; init; }

    /// <summary>The current InspirationDef defName; known "" is none and unknown stays distinct from none.</summary>
    public Fact<string> Inspiration { get; init; }

    /// <summary>The hours per day the timetable assigns to Work or Anything (WorkPawn.WorkHoursPerDay); unknown when the schedule is.</summary>
    public Fact<int> WorkHours { get; init; }

    /// <summary>The pawn's skill row; a skill absent from the read is level 0 and never learns (a disabled skill).</summary>
    public ProfileSkill Skill(string name) =>
        Skills.TryGetValue(name, out var s) ? s : new ProfileSkill { Name = name, Disabled = true };

    /// <summary>
    /// The pawn's row of the skill the work type is scored by; a work type without one (unskilled, or absent
    /// from the pawn's work rows) reads as a disabled skill.
    /// </summary>
    public ProfileSkill SkillFor(WorkType work) => Skill(WorkSkill.GetValueOrDefault(work, ""));

    /// <summary>
    /// Reports whether the pawn can do the work type at all: not backstory-disabled, not trait-forbidden and,
    /// for skilled work, the skill not disabled and at or above the floor.
    /// </summary>
    public bool Capable(WorkType work, int floor)
    {
        if (Incapable.GetValueOrDefault(work) || Forbidden(work))
        {
            return false;
        }
        var skill = WorkSkill.GetValueOrDefault(work, "");
        if (skill.Length == 0)
        {
            return true;
        }
        var s = Skill(skill);
        return !s.Disabled && s.Level >= floor;
    }

    /// <summary>The hard trait rule: a Pyromaniac never fights fire, a Brawler never hunts (ranged), an Abrasive pawn never wardens.</summary>
    public bool Forbidden(WorkType work)
    {
        if (WorkMinAge.TryGetValue(work, out var min) && Age < min)
        {
            return true;
        }
        if (Effects.DisabledWork.Contains(work))
        {
            return true;
        }
        return work switch
        {
            WorkType.Hunting => Effects.MeleeOnly,
            WorkType.Warden => Effects.Sociable < 0,
            _ => false,
        };
    }

    /// <summary>Lists the work types Forbidden refuses for this pawn, in natural-priority order, for the dossier.</summary>
    public IReadOnlyList<WorkType> ForbiddenWork()
    {
        var result = new List<WorkType>();
        foreach (var work in new[] { WorkType.Firefighter, WorkType.Warden, WorkType.Hunting })
        {
            if (Forbidden(work))
            {
                result.Add(work);
            }
        }
        var young = new List<WorkType>();
        foreach (var work in WorkMinAge.Keys)
        {
            if (Forbidden(work) && !result.Contains(work))
            {
                young.Add(work);
            }
        }
        foreach (var work in Effects.DisabledWork)
        {
            if (!result.Contains(work) && !young.Contains(work))
            {
                young.Add(work);
            }
        }
        result.AddRange(young.OrderBy(w => Wire.Name(w), StringComparer.Ordinal));
        return result;
    }
}

public static class WorkPawnProfileExtensions
{
    /// <summary>
    /// How many hours of the pawn's timetable are Work or Anything. Unknown when the schedule is unread, is
    /// not 24 hours, or holds an assignment this does not classify.
    /// </summary>
    public static Fact<int> WorkHoursPerDay(this WorkPawn w)
    {
        if (!w.Schedule.TryGetValue(out var rows) || rows.Count != 24)
        {
            return Unknown<int>();
        }
        var n = 0;
        foreach (var s in rows)
        {
            switch (s)
            {
                case PolicyConstants.ScheduleWork:
                case PolicyConstants.ScheduleAnything:
                    n++;
                    break;
                case PolicyConstants.ScheduleSleep:
                case PolicyConstants.ScheduleJoy:
                case PolicyConstants.ScheduleMeditate:
                    break;
                default:
                    return Unknown<int>();
            }
        }
        return Known(n);
    }
}

public static partial class PolicyFunctions
{
    /// <summary>
    /// Reads the profile from a WorkPawn; unknown traits, incapable rows or age leave those parts empty
    /// rather than making the profile unknown, since the planner degrades to skill-only ordering without them.
    /// </summary>
    public static PawnProfile BuildProfile(WorkPawn pawn)
    {
        var skillsByName = new Dictionary<string, ProfileSkill>(StringComparer.Ordinal);
        var incapable = new Dictionary<WorkType, bool>();
        var workSkill = new Dictionary<WorkType, string>();
        var effects = new TraitEffects();
        IReadOnlyList<PawnTrait> traitRows = [];
        var age = 0.0;
        var genesEffects = new GeneEffects();
        var child = false;
        IReadOnlyDictionary<WorkType, int> workMinAge = new Dictionary<WorkType, int>();
        if (pawn.Work.TryGetValue(out var rows))
        {
            foreach (var row in rows)
            {
                workSkill[row.Work] = row.Skill;
            }
        }
        if (pawn.Skills.TryGetValue(out var skills))
        {
            foreach (var s in skills)
            {
                skillsByName[s.Name] = new ProfileSkill { Name = s.Name, Level = s.Level, Stored = s.Stored, Passion = s.Passion, Disabled = s.Disabled };
            }
        }
        if (pawn.Traits.TryGetValue(out var traits))
        {
            traitRows = [.. traits];
            foreach (var t in traits)
            {
                effects = effects.Add(t.Effects);
            }
        }
        if (pawn.Incapable.TryGetValue(out var incapableWork))
        {
            foreach (var w in incapableWork)
            {
                incapable[w] = true;
            }
        }
        if (pawn.Age.TryGetValue(out var a))
        {
            age = a;
        }
        if (pawn.Biotech.TryGetValue(out var bt))
        {
            if (bt.Effects.TryGetValue(out var genes))
            {
                genesEffects = genes;
                effects = effects with
                {
                    WorkSpeed = genes.Apply("WorkSpeedGlobal", effects.WorkSpeed),
                    LearnRate = genes.Apply("GlobalLearningFactor", effects.LearnRate),
                };
            }
            var (isChild, known) = bt.IsChild();
            if (known)
            {
                child = isChild;
            }
            if (bt.WorkMinAges.TryGetValue(out var ages))
            {
                workMinAge = ages;
            }
        }
        pawn.Ranged.TryGetValue(out var ranged);
        pawn.Hunts.TryGetValue(out var hunts);
        return new PawnProfile
        {
            Id = pawn.Id,
            Skills = skillsByName,
            Traits = traitRows,
            Effects = effects,
            Incapable = incapable,
            WorkSkill = workSkill,
            Age = age,
            Child = child,
            Biotech = pawn.Biotech,
            Genes = genesEffects,
            WorkMinAge = workMinAge,
            Ranged = ranged,
            Hunts = hunts,
            Inspiration = pawn.Inspiration,
            WorkHours = pawn.WorkHoursPerDay(),
        };
    }

    /// <summary>Builds every pawn's profile in ID order.</summary>
    public static IReadOnlyList<PawnProfile> Profiles(IReadOnlyList<WorkPawn> pawns) =>
        [.. pawns.Select(BuildProfile).OrderBy(p => p.Id)];
}
