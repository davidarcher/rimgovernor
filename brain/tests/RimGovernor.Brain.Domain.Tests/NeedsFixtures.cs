using RimGovernor.Contracts.Wire.Observations;

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
        Deterioration = null, ResearchBenches = [], Corpses = [], Deathrest = null,
    };

    /// <summary>The read of no pawns at tick 0.</summary>
    public static NeedsForecastRead Empty => Read(0);

    /// <summary>An eligible pawn <paramref name="id"/>.</summary>
    public static NeedsForecastPawn Pawn(string id = "pawn") => new()
    {
        Pawn = id, State = "eligible", Location = new(0, 0), CurrentService = null, VoluntaryControlBlockers = [],
        LocalHour = 12, Awake = true, BedThoughts = new BedThoughtOnset.None(), SleepThreshold = .75, WakeThreshold = 1, SleepBlocked = false, SleepBlockedUntilTicks = 0,
        SleepWhileStarving = false, Mood = null, ComfortableMin = 16, ComfortableMax = 26, SleepComfortableMin = 0, SleepComfortableMax = 0,
        Sleep = null, Needs = [], Targets = [], Unsupported = [], Outdoors = new NeedLevel.Lacks(), OutdoorsDisabled = false,
        OwnedBed = "", OwnedBedRoom = null, Ascetic = false, RivalRooms = [], ThoughtStages = [], Apparel = null,
        Consciousness = 1, Moving = 1, Manipulation = 1, Sight = 1, Talking = 1,
        MalnutritionSeverity = 0, FoodPoisoningSeverity = 0, HypothermiaSeverity = 0, HeatstrokeSeverity = 0,
        PainShockThreshold = .8, HealthScale = 1, IncomingDamageFactor = 1, InjuryHealingFactor = 1, ResearchSpeed = 1, PlantWorkSpeed = 1,
        Disease = Disease, PartLoss = PartLoss, CorpseThoughts = [], SightingTicks = 0, Upkeep = Upkeep, Deathrest = null,
    };

    /// <summary>No work speeds, no incapable work, no food and no mood.</summary>
    public static PawnUpkeepRead Upkeep => new() { Speeds = [], IncapableWork = [], FoodPerDay = 0, Mood = null };

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
    public static ThoughtLasting Lasting(double duration, bool fades, int stackLimit = 0) => new ThoughtLasting.Memory(duration, fades, stackLimit);

    /// <summary>A situational thought's lasting.</summary>
    public static readonly ThoughtLasting Situational = new ThoughtLasting.Situational();

    /// <summary>A bed's room with no care factors (a costed bed, indoors, every factor 0).</summary>
    public static NeedBedRoom BedRoom(string role, int stage, bool prisoners) => new(role, stage, prisoners)
    {
        Costless = false, PsychologicallyOutdoors = false, InfectionChanceFactor = 0, SurgerySuccessFactor = 0, ImmunityGainSpeedFactor = 0,
    };

    /// <summary>The apparel workers' pawn facts with no kind-required apparel, no requirements and no minimum quality.</summary>
    public static NeedApparel Apparel(RimGovernor.Contracts.Wire.Defs.Gender gender, bool legs, bool safe) =>
        new(gender, legs, safe) { KindRequired = [], Requirements = [], MinimumQuality = RimGovernor.Contracts.Wire.Defs.QualityCategory.Awful };

    /// <summary>An indoor roofed cell (thin constructed roof).</summary>
    public static readonly NeedCell Indoors = new(true, false, false, false, false);

    /// <summary>A target at the origin's indoor roofed cell, no meal thoughts, bed or deterioration.</summary>
    public static NeedTarget Target(string id, string kind, string resource, int count, double travel, NeedRecovery recovery) =>
        new(id, kind, resource, count, travel, recovery) { MealThoughts = [], Location = new(0, 0), Unsupported = [], Cell = Indoors, Bed = null, Deterioration = null };

    /// <summary>A food target eaten in <paramref name="service"/> ticks restoring <paramref name="gain"/> a unit.</summary>
    public static NeedTarget Food(string id, string resource, int count, double travel, double service, double gain) =>
        Target(id, "food", resource, count, travel, new NeedRecovery.Ingest(service, gain));

    /// <summary>A rest or joy target gaining <paramref name="gain"/> a tick.</summary>
    public static NeedTarget Service(string id, string kind, double travel, double gain) =>
        Target(id, kind, "", 1, travel, new NeedRecovery.Rate(gain));
}
