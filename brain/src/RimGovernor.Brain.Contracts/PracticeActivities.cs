using System.Text.Json.Serialization;
using RimGovernor.Brain.Domain;
namespace RimGovernor.Contracts.Wire.Observations;

/// <summary>Native mechanics for a bounded practice session, not a chosen trainee or goal.</summary>
public sealed record PracticeActivityRead
{
    public bool Known { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<PracticeWorker>))]
    public EquatableArray<PracticeWorker> Workers { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> Findings { get; init; }
}
public sealed record PracticeWorker(string Pawn, string Skill, string Target, Cell Location,
    string WorkGiver, string Job, Fact<double> ApproachTicks, Fact<double> DurationTicks,
    SkillLearning Learning)
{
    /// <summary>Conditional completed shots/exchanges; native success remains unobserved.</summary>
    public Fact<double> CallbackIntervalTicks { get; init; }
    public double PreparationTicks { get; init; }
    public Fact<double> RawXpPerCallback { get; init; }
    /// <summary>The joy a tick of the session gives (the spar's JobDef.joyGainRate × 0.36 / 2500 × the tolerance factor); 0 for
    /// a range drill, which is not recreation.</summary>
    public required double JoyGainPerTick { get; init; }
    /// <summary>The memories a completed session gives the pawn: a spar's RimGovernor_TrainedWith (TrainingCompany.Finished,
    /// a bout always shared), unless ThoughtUtility.CanGetThought refuses it (#3858). A range drill's is empty: its company
    /// memory depends on who else trains (practice_company_mood_unassessed), and it is no recreation task.</summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<NeedMemory>))]
    public required EquatableArray<NeedMemory> Thoughts { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> Equipment { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> Findings { get; init; }
}
