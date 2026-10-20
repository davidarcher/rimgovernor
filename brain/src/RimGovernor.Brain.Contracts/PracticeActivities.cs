using System.Text.Json.Serialization;
using RimGovernor.Brain.Domain;
namespace RimGovernor.Contracts.Wire.Observations;

/// <summary>Native mechanics for a bounded practice session, not a chosen trainee or goal.</summary>
public sealed record PracticeActivityRead
{
    public bool Known { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<PracticeWorker>))]
    public EquatableArray<PracticeWorker> Workers { get; init; }
    /// <summary>The practice surgeries doctors can run now on colony prisoners (#3637).</summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<PracticeSurgeryWorker>))]
    public EquatableArray<PracticeSurgeryWorker> Surgeries { get; init; }
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
    public Fact<double> JoyGainPerTick { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> Equipment { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> Findings { get; init; }
}
/// <summary>One doctor's practice surgery on a colony prisoner (#3637): a native recipe on a part (-1 for none) with one stocked medicine
/// kind (thing def) the recipe and the patient's care allow. <see cref="SuccessChance"/> is that doctor's native chance with that medicine in the
/// patient's bed (the best medical bed while out of one); <see cref="ApproachTicks"/> walks via the nearest stack of that medicine.</summary>
public sealed record PracticeSurgeryWorker(string Doctor, string Patient, string Recipe, int Part, string Medicine, int MedicineCount,
    Fact<double> SuccessChance, string WorkGiver, Cell Location, Fact<double> ApproachTicks, Fact<double> WorkTicks)
{
    public ProductiveSkillWork? SkillWork { get; init; }
}
/// <summary>Fresh native requirement and skill observations for an admitted attempt.</summary>
public sealed record PracticeMandateRead(string Task, Fact<bool> Assigned,
    [property: JsonConverter(typeof(EquatableArrayJsonConverter<PracticeFloorRead>))] EquatableArray<PracticeFloorRead> Alternatives);
public sealed record PracticeFloorRead(string Skill, int Minimum, SkillLearning Learning);
