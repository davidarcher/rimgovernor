using System.Text.Json.Serialization;
using RimGovernor.Brain.Domain;
namespace RimGovernor.Contracts.Wire.Observations;

/// <summary>Native mechanics for a bounded practice session, not a chosen trainee or goal: one row per free colonist, skill
/// and stand or marker it can reach and use now.</summary>
public sealed record PracticeActivityRead
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<PracticeWorker>))]
    public required EquatableArray<PracticeWorker> Workers { get; init; }
}

/// <summary>One pawn's practice at one target: the walk there, the session's length, its native callback cadence and the
/// raw XP each completed shot or exchange pays (native success remains unobserved), the joy a sparring tick gives (0 at the
/// range) and the preparation before the first callback (a sparring bout's join window).</summary>
public sealed record PracticeWorker(string Pawn, string Skill, string Target, Cell Location,
    string WorkGiver, string Job, double ApproachTicks, double DurationTicks,
    SkillLearning Learning)
{
    public required double CallbackIntervalTicks { get; init; }
    public required double PreparationTicks { get; init; }
    public required double RawXpPerCallback { get; init; }
    /// <summary>The joy a tick of the session gives (the spar's JobDef.joyGainRate × 0.36 / 2500 × the tolerance factor); 0 for
    /// a range drill, which is not recreation.</summary>
    public required double JoyGainPerTick { get; init; }
    /// <summary>The memories a completed session gives the pawn in company: RimGovernor_TrainedWith (TrainingCompany.Finished),
    /// unless ThoughtUtility.CanGetThought refuses it (#3858). A spar always has company (a bout is shared); a range drill has it
    /// when another colonist is on a session at a <see cref="Company"/> target as it finishes (#3871).</summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<NeedMemory>))]
    public required EquatableArray<NeedMemory> Thoughts { get; init; }
    /// <summary>The practice targets (stands and sparring markers) whose cell is within chat range of this target
    /// (SocialInteractionUtility.IsGoodPositionForInteraction), where a colonist on its session is a drill's company (#3871).
    /// Empty for a spar, whose company is its bout.</summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public required EquatableArray<string> Company { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public required EquatableArray<string> Equipment { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public required EquatableArray<string> Findings { get; init; }
}
