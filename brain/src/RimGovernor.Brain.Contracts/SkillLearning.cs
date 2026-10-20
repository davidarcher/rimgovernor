using System.Text.Json.Serialization;
using RimGovernor.Brain.Domain;

namespace RimGovernor.Contracts.Wire.Observations;

/// <summary>Read-only native skill mechanics at one frame. Missing evidence is never zero.</summary>
public sealed record SkillLearning
{
    public Fact<bool> Disabled { get; init; }
    public Fact<bool> TrackerAllowsLearning { get; init; }
    public Fact<int> StoredLevel { get; init; }
    public Fact<int> Aptitude { get; init; }
    public Fact<double> Xp { get; init; }
    public Fact<double> XpSinceMidnight { get; init; }
    public Fact<double> DirectFactor { get; init; }
    public Fact<double> UnsaturatedFactor { get; init; }
    public Fact<double> SaturationThreshold { get; init; }
    public Fact<double> SaturatedFactor { get; init; }
    public Fact<double> LossThreshold { get; init; }
    public Fact<int> DecayInterval { get; init; }
    public Fact<int> NextDecay { get; init; }
    /// <summary>Exclusive bound: refresh at either native midnight reset; at most one game day.</summary>
    public Fact<int> ValidForTicks { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<SkillLevelMechanics>))]
    public EquatableArray<SkillLevelMechanics> Levels { get; init; }
    public Fact<bool> PracticeEligible { get; init; }
    public Fact<int> PracticeCeiling { get; init; }
    public Fact<double> RangeAppliedBudget { get; init; }
}

public sealed record SkillLevelMechanics(int Level, double RequiredXp, double DecayXp);

/// <summary>Ordinary Toils_Recipe learning: unfinished recipes pay during work; others pay at completion.
/// RawXpPerWorkTick is paid per tick spent on recipe work (<c>ticksSpentDoingRecipeWork</c>), not per unit of work amount.</summary>
public sealed record RecipeLearning(string Skill, Fact<double> RawXpPerWorkTick, bool DuringWork);

/// <summary>Native learning one completed tend pays (#3632): <c>Toils_Tend.FinalizeTend</c> calls <c>Pawn_SkillTracker.Learn</c>
/// once per treatment batch, before the treatment and whatever its quality. RawXp depends on the patient and medicine, not on skill.</summary>
public sealed record TendLearning(SkillLearning Learning, string Skill, Fact<double> RawXp, string Provenance);

/// <summary>Native work amounts and per-stored-level rates for this pawn, recipe and station at one frame.
/// Rates retain other observed conditions; no prediction of future health, equipment or facility changes.</summary>
public sealed record ProductiveSkillWork(SkillLearning Learning, RecipeLearning Recipe, Fact<double> WorkAmount,
    [property: JsonConverter(typeof(EquatableArrayJsonConverter<SkillWorkRate>))] EquatableArray<SkillWorkRate> Rates, string Provenance);
public sealed record SkillWorkRate(int StoredLevel, Fact<double> WorkPerTick);

/// <summary>A pawn's native stat value at each stored skill level (0-20) at one frame. Capacities and every other
/// observed condition are held; the skill term alone is replaced. Pawns whose pipeline does not qualify carry a
/// finding instead (<see cref="PawnBiography.SkillStatFindings"/>).</summary>
public sealed record SkillStatCurve(string Stat, string Skill,
    [property: JsonConverter(typeof(EquatableArrayJsonConverter<SkillStatLevel>))] EquatableArray<SkillStatLevel> Levels, string Provenance);
public sealed record SkillStatLevel(int StoredLevel, double Value);
