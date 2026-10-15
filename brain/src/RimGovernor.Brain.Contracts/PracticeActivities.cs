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
    public bool Truncated { get; init; }
}
public sealed record PracticeWorker(string Pawn, string Skill, string Target, Cell Location,
    string WorkGiver, string Job, Fact<double> ApproachTicks, Fact<double> DurationTicks,
    SkillLearning Learning)
{
    /// <summary>Conditional completed shots/exchanges; native success remains unobserved.</summary>
    public Fact<double> CallbackIntervalTicks { get; init; }
    public double PreparationTicks { get; init; }
    public Fact<double> RawXpPerCallback { get; init; }
    public Fact<double> InjuryRecoveryTicks { get; init; }
    public Fact<double> JoyGainPerTick { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> Equipment { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> Findings { get; init; }
}
/// <summary>Fresh native requirement and skill observations for an admitted attempt.</summary>
public sealed record PracticeMandateRead(string Task, Fact<bool> Assigned,
    [property: JsonConverter(typeof(EquatableArrayJsonConverter<PracticeFloorRead>))] EquatableArray<PracticeFloorRead> Alternatives);
public sealed record PracticeFloorRead(string Skill, int Minimum, SkillLearning Learning);
