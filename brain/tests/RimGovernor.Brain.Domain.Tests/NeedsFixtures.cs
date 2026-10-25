using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The needs forecast read's test rows (#3781): every member required, so a test states only what it varies. A pawn is an eligible
/// healthy adult at the origin at noon, awake, with the vanilla sleep thresholds and comfortable range, no needs, targets or mood; a
/// target is an indoor roofed cell at the origin.
/// </summary>
internal static class NeedsFixtures
{
    /// <summary>The read at <paramref name="tick"/> of <paramref name="pawns"/>, clear weather, no benches, corpses or deathrest.</summary>
    public static NeedsForecastRead Read(long tick, params NeedsForecastPawn[] pawns) => new()
    {
        Tick = new(tick), Pawns = [.. pawns], WeatherThought = "", WeatherThoughtStages = 0, RainRate = 0, MapArea = 0,
        Deterioration = null, ResearchBenches = [], Corpses = [], Deathrest = null, Prison = NoPrison, School = NoSchool,
        Babies = NoBabies,
    };

    /// <summary>The baby fit read of no baby, at the game's fits (MentalFitGenerator, MentalStates_BabyFits.xml; Biotech, 1.7).</summary>
    public static BabyFitsRead NoBabies => new()
    {
        Geometry = null,
        Social = new()
        {
            CheckIntervalTicks = 60, RetryIntervalTicks = 91, MinInteractionTicks = 320,
            QuietMtbTicks = 22000, NormalMtbTicks = 6600, SuperActiveMtbTicks = 550,
            OpinionFactor = [new(-100, 6), new(-50, 4), new(-25, 2), new(0, 1), new(50, .1), new(100, 0)],
            Jobs = [], Pawns = [], MoodDefs = [],
        },
        CheckIntervalTicks = 150, ClamorIntervalTicks = 151, RecoveryAwakeTicks = 600, SleepDisturbanceIntervalTicks = 300,
        Play = new()
        {
            FallPerTick = .00025 / 150, GainPerTick = .0002,
            Activities = [new("PlayStatic", BabyPlayKind.Static), new("PlayWalking", BabyPlayKind.Walking), new("PlayToys", BabyPlayKind.Toys)],
            Buildings = [new("ToyBox", "WoodLog", false, 1.25), new("BabyDecoration", "", true, 1.1)],
        },
        Fits =
        [
            new() { Def = "Crying", OpinionThought = "BabyCriedSocial", MtbDaysByMood = [new(0, .083), new(.25, .166), new(.5, 1), new(.75, 4), new(1, 8)], DurationTicks = 1200, Heard = true },
            new() { Def = "Giggling", OpinionThought = "BabyGiggledSocial", MtbDaysByMood = [new(0, 8), new(.25, 4), new(.5, 1), new(.75, .5), new(1, .25)], DurationTicks = 1200, Heard = true },
        ],
        Babies = [],
    };

    /// <summary>The recruitment read of no warden and no prisoner, at the game's curves (InteractionWorker_RecruitAttempt, ConversionTuning, JobDriver_ConvertPrisoner.NumTalks, 1.7).</summary>
    public static PrisonRecruitRead NoPrison => new()
    {
        MoodCurve = [new(0, .2), new(.5, 1), new(1, 1.5)], OpinionCurve = [new(-100, .5), new(0, 1), new(100, 1.5)],
        MaxInteractionsPerDay = 2, ConvertTalks = 3, CertaintyDrift = [new(0.2, 0.01), new(0.5, 0.02), new(0.8, 0.03)], Rapport = Rapport, Chitchat = new("Chitchat", 10, 1, 300, -1), RecruitInspiration = "Inspired_Recruitment", TamingInspiration = "Inspired_Taming",
        Wardens = [], Prisoners = [],
    };

    /// <summary>BuildRapport's RapportBuilt (Thoughts_Memory_Social.xml, 1.7): 20 days, stacking 300 in all and 50 a pawn, multiplier 1, fading from 70% of its duration.</summary>
    public static RapportRead Rapport => new("RapportBuilt", 20 * 60000, 300, 50, 1, 0.7);

    /// <summary>The lesson read of no child, no desk and no teacher, at the game's rates and desires (Biotech, 1.7).</summary>
    public static SchoolRead NoSchool => new()
    {
        FallPerTick = 3E-06, RefillPerTick = 1.2E-05, Lessontaking = "Lessontaking", LessonXpPerTick = 0.25,
        Desires =
        [
            new("Skydreaming", 1, true), new("Radiotalking", 1, false), new("Floordrawing", 1, true), new("NatureRunning", 1, true),
            new("Reading", 1, true), new("Workwatching", 1, true), new("Lessontaking", 2.5, true),
        ],
        StandingDesks = 0, Teacher = "", Children = [],
    };

    /// <summary>The read of no pawns at tick 0.</summary>
    public static NeedsForecastRead Empty => Read(0);

    /// <summary>An eligible pawn <paramref name="id"/>.</summary>
    public static NeedsForecastPawn Pawn(string id = "pawn") => new()
    {
        Pawn = id, State = "eligible", ViolenceCapable = true, Location = new(0, 0), CurrentService = null, VoluntaryControlBlockers = [], RoomUses = new([], []),
        LocalHour = 12, Awake = true, BedThoughts = new BedThoughtOnset.None(), SleepThreshold = .75, WakeThreshold = 1, SleepBlocked = false, SleepBlockedUntilTicks = 0,
        SleepWhileStarving = false, Mood = null, ComfortableMin = 16, ComfortableMax = 26, SleepComfortableMin = 0, SleepComfortableMax = 0,
        Sleep = null, LyingIn = null, Needs = [], Targets = [], Unsupported = [], Outdoors = new NeedLevel.Lacks(), OutdoorsDisabled = false,
        OwnedBed = "", OwnedBedRoom = null, OwnedRoom = null, Ascetic = false, RivalRooms = [], ThoughtStages = [], Apparel = null,
        Consciousness = 1, Moving = 1, Manipulation = 1, Sight = 1, Talking = 1,
        MalnutritionSeverity = 0, FoodPoisoningSeverity = 0, HypothermiaSeverity = 0, HeatstrokeSeverity = 0,
        PainShockThreshold = .8, HealthScale = 1, IncomingDamageFactor = 1, InjuryHealingFactor = 1, ResearchSpeed = 1, PlantWorkSpeed = 1,
        Disease = Disease, PartLoss = PartLoss, CorpseThoughts = [], SightingTicks = 0, Upkeep = Upkeep, Deathrest = null,
    };

    /// <summary>No work speeds, no incapable work, no food and no mood.</summary>
    public static PawnUpkeepRead Upkeep => new() { Speeds = [], SkillSpeeds = [], ChildWork = null, IncapableWork = [], FoodPerDay = 0, Mood = null };

    /// <summary>Immunity at 1, no contracts, whole blood pumping, breathing and filtration, and no affected parts.</summary>
    public static PawnDiseaseFacts Disease => new()
    {
        ImmunityGainSpeed = 1, Contracts = [], BloodPumping = 1, Breathing = 1, BloodFiltration = 1, Parts = [], Replacements = [],
    };

    /// <summary>A body with no part a raid hit can destroy (#3847): no part rows, no replacements.</summary>
    public static PawnPartLossFacts PartLoss => new() { InstantKillChance = 1, Parts = [], Replacements = [] };

    /// <summary>A mood need at 0.5 aimed at 0.5, the vanilla seeker rates, minor break at 0.35, no conditional thoughts or memories.</summary>
    public static NeedMood Mood => new()
    {
        Level = .5, Target = .5, UnclampedTarget = .5, RisePerTick = .12 / 2500, FallPerTick = .08 / 2500, FreezesWhileAsleep = false,
        BreakThreshold = .35, Conditional = new ConditionalMood.Modeled([]), Memories = [],
    };

    /// <summary>A need <paramref name="kind"/> with no unserved gain and no unsupported changes.</summary>
    public static NeedRate Rate(string kind, double level, double fall, double threshold) =>
        new(kind, level, fall, threshold) { UnservedGainPerTick = 0, GainPerTickPerEffectiveness = 0, Unsupported = [] };

    /// <summary>A memory that expires after <paramref name="remaining"/> ticks, or a permanent one when null.</summary>
    public static NeedMemory Memory(string id, double offset, double? remaining, bool fades, int stackLimit = 0) =>
        new(id, offset, remaining is { } r ? new MemoryLife.Expires(r) : new MemoryLife.Permanent(), fades) { StackLimit = stackLimit };

    /// <summary>A memory thought's lasting: its duration and whether it fades.</summary>
    public static ThoughtLasting Lasting(double duration, bool fades, int stackLimit = 0) => new ThoughtLasting.Memory(duration, fades, stackLimit, 1, false);

    /// <summary>A situational thought's lasting.</summary>
    public static readonly ThoughtLasting Situational = new ThoughtLasting.Situational();

    /// <summary>A bed's room with no care factors (a costed bed, indoors, every factor 0).</summary>
    public static NeedBedRoom BedRoom(string role, int stage, bool prisoners) => new(role, stage, prisoners)
    {
        Costless = false, PsychologicallyOutdoors = false, InfectionChanceFactor = 0, SurgerySuccessFactor = 0, ImmunityGainSpeedFactor = 0,
    };

    /// <summary>The apparel workers' pawn facts with no kind-required apparel, no requirements and no minimum quality.</summary>
    public static NeedApparel Apparel(RimGovernor.Brain.GameFacts.Defs.Gender gender, bool legs, bool safe) =>
        new(gender, legs, safe) { KindRequired = [], Requirements = [], MinimumQuality = RimGovernor.Brain.GameFacts.Defs.QualityCategory.Awful };

    /// <summary>An indoor roofed cell (thin constructed roof).</summary>
    public static readonly NeedCell Indoors = new(true, false, false, false, false);

    /// <summary>A target at the origin's indoor roofed cell, no meal thoughts, bed or deterioration.</summary>
    public static NeedTarget Target(string id, string kind, string resource, int count, double travel, NeedRecovery recovery) =>
        new(id, kind, resource, count, travel, recovery) { MealThoughts = [], Location = new(0, 0), Unsupported = [], Cell = Indoors, Bed = null, Deterioration = null,
            Stock = kind == "food" ? new NeedStock.Stacked(count, new FoodFreshness.Never()) : new NeedStock.NotStacked(), MaxPawns = 1 };

    /// <summary>A food target eaten in <paramref name="service"/> ticks restoring <paramref name="gain"/> a unit.</summary>
    public static NeedTarget Food(string id, string resource, int count, double travel, double service, double gain) =>
        Target(id, "food", resource, count, travel, new NeedRecovery.Ingest(service, gain)) with { Meal = new(32, true) };

    /// <summary>A rest or joy target gaining <paramref name="gain"/> a tick.</summary>
    public static NeedTarget Service(string id, string kind, double travel, double gain) =>
        Target(id, kind, "", 1, travel, new NeedRecovery.Rate(gain)) with
        { JoySession = kind == "joy" ? new NeedJoySession(GameTime.TicksPerDay, false, true) : null };
}
