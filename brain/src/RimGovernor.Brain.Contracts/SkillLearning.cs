using System.Text.Json.Serialization;
using RimGovernor.Brain.Domain;

namespace RimGovernor.Contracts.Wire.Observations;

/// <summary>Read-only native skill mechanics at one frame (#3783): every member read, the practice a skill admits a named state.</summary>
public sealed record SkillLearning
{
    public required bool Disabled { get; init; }
    public required bool TrackerAllowsLearning { get; init; }
    public required int StoredLevel { get; init; }
    public required int Aptitude { get; init; }
    public required double Xp { get; init; }
    public required double XpSinceMidnight { get; init; }
    public required double DirectFactor { get; init; }
    public required double UnsaturatedFactor { get; init; }
    public required double SaturationThreshold { get; init; }
    public required double SaturatedFactor { get; init; }
    public required double LossThreshold { get; init; }
    public required int DecayInterval { get; init; }
    public required int NextDecay { get; init; }
    /// <summary>Exclusive bound: refresh at either native midnight reset; at most one game day.</summary>
    public required int ValidForTicks { get; init; }
    /// <summary>Next actual SkillsTickInterval local-day reset, relative to this observation: 0 when it is due at the next
    /// tracker callback. The native tracker resets once each local day, so it always falls within a day and a decay interval.</summary>
    public required int NextMidnightReset { get; init; }
    public required int TrackerIntervalTicks { get; init; }
    public required int TrackerPhase { get; init; }
    public required int TrackerElapsedTicks { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<SkillLevelMechanics>))]
    public required EquatableArray<SkillLevelMechanics> Levels { get; init; }
    /// <summary>The practice the skill admits (#3813, #3846): the sparring bout for Melee, the range drill for Shooting, none for
    /// every other skill.</summary>
    public required SkillPractice Practice { get; init; }
}

/// <summary>The practice a skill admits (#3783): Melee spars, Shooting drills at a range, every other skill has none.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SkillPractice.None), "none")]
[JsonDerivedType(typeof(SkillPractice.Sparring), "sparring")]
[JsonDerivedType(typeof(SkillPractice.Range), "range")]
public abstract record SkillPractice
{
    private protected SkillPractice() { }

    /// <summary>A skill no practice trains.</summary>
    public sealed record None : SkillPractice;

    /// <summary>A practised skill. Capable is the one practice eligibility rule (#3813, native RangeTraining.CanTrain): the pawn
    /// may practise it whatever its level, draft or role. Eligible is the job's own gate now (RangeTraining.Eligible,
    /// SparringBouts.Eligible) and Ceiling the level practice stops at (RangeTraining.CeilingFor).</summary>
    public abstract record Drill(bool Capable, bool Eligible, int Ceiling) : SkillPractice;

    /// <summary>Melee's sparring bout.</summary>
    public sealed record Sparring(bool Capable, bool Eligible, int Ceiling) : Drill(Capable, Eligible, Ceiling);

    /// <summary>Shooting's range drill: the applied XP the day's budget still allows (RangeTraining.Remaining) and the daily cap
    /// it refills to (#3846, RangeTraining.DailyXpBudget).</summary>
    public sealed record Range(bool Capable, bool Eligible, int Ceiling, double AppliedBudget, double DailyBudget)
        : Drill(Capable, Eligible, Ceiling);
}

public sealed record SkillLevelMechanics(int Level, double RequiredXp, double DecayXp);

/// <summary>Ordinary Toils_Recipe learning: unfinished recipes pay during work; others pay at completion.
/// RawXpPerWorkTick is paid per tick spent on recipe work (<c>ticksSpentDoingRecipeWork</c>), not per unit of work amount.</summary>
public sealed record RecipeLearning(string Skill, double RawXpPerWorkTick, bool DuringWork);

/// <summary>Native learning one completed tend pays (#3632): <c>Toils_Tend.FinalizeTend</c> calls <c>Pawn_SkillTracker.Learn</c>
/// once per treatment batch, before the treatment and whatever its quality. RawXp depends on the patient and medicine, not on skill.</summary>
public sealed record TendLearning(SkillLearning Learning, string Skill, double RawXp, string Provenance);

/// <summary>Native work amounts and per-stored-level rates for this pawn, recipe and station at one frame.
/// Rates retain other observed conditions; no prediction of future health, equipment or facility changes.</summary>
public sealed record ProductiveSkillWork(SkillLearning Learning, RecipeLearning Recipe, double WorkAmount,
    [property: JsonConverter(typeof(EquatableArrayJsonConverter<SkillWorkRate>))] EquatableArray<SkillWorkRate> Rates, string Provenance)
{
    public int CallbackIntervalTicks { get; init; } = 1;
    public int CallbackPhase { get; init; }
    public int CallbackElapsedTicks { get; init; }
    /// <summary>The pawn's place in the native normal tick list, which orders same-tick callbacks. Research reads it to order
    /// the final callbacks of a shared project (its native read throws without it); no other work's callbacks are ordered.</summary>
    public int CallbackOrder { get; init; }
    public bool LearnBeforeWork { get; init; }
    public bool FixedDuration { get; init; }
    /// <summary>Each completed native target discards excess work (one cell for sowing).</summary>
    public double WorkQuantum { get; init; }
    public double PreparationTicks { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<ProductiveWorkStage>))]
    public EquatableArray<ProductiveWorkStage> Stages { get; init; }
}
public sealed record ProductiveWorkStage(double WorkAmount, double RateFactor, double RawXpPerWorkTick,
    double WorkQuantum = 0, bool HoldRateUntilQuantum = false);
/// <summary>The work a tick at one stored level: 0 when the pawn's stat gives no speed there.</summary>
public sealed record SkillWorkRate(int StoredLevel, double WorkPerTick);

/// <summary>A pawn's native stat value at each stored skill level (0-20) at one frame, the stat named by its defName or, for
/// a value no stat holds, by a constant here. Capacities and every other observed condition are held; the skill term alone is
/// replaced. Pawns whose pipeline does not qualify carry a finding instead (<see cref="PawnBiography.SkillStatFindings"/>).</summary>
public sealed record SkillStatCurve(string Stat, string Skill,
    [property: JsonConverter(typeof(EquatableArrayJsonConverter<SkillStatLevel>))] EquatableArray<SkillStatLevel> Levels, string Provenance)
{
    /// <summary>The ranged damage the defense capacity counts (#3846): the primary's ranged DPS (<see cref="PawnEquipment.RangedDps"/>)
    /// at the hit factor (<see cref="PawnEquipment.RangedHitFactor"/>) the ShootingAccuracyPawn of each stored Shooting level
    /// gives; 0 at every level without a ranged primary.</summary>
    public const string RangedDefenseDps = "RangedDefenseDps";
}
public sealed record SkillStatLevel(int StoredLevel, double Value);
