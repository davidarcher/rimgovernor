using RimGovernor.Brain.Domain;
using RimGovernor.Contracts.Wire.Common;
using Identity = RimGovernor.Contracts.Wire.Common.Identity;
using System.Text.Json.Serialization;

namespace RimGovernor.Contracts.Wire.Observations;

public sealed record ActivityTaskRequest(Identity Identity, PawnActivities Batch);
/// <summary>Fresh legal bindings for the already-selected participant/task, not a new winner.</summary>
public sealed record ActivityTaskBinding(string Task, PawnActivityStep Step)
{
    public Fact<int> RemainingCount { get; init; }
    public Fact<double> ProductiveWorkTicks { get; init; }
}
public sealed record ActivityTaskObservation(string Pawn, Operations.PawnActivityExecution? Execution);
public sealed record ActivityTaskRead
{
    public ObservationContext? Context { get; init; }
    public bool Known { get; init; }
    public bool Truncated { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<ActivityTaskBinding>))]
    public EquatableArray<ActivityTaskBinding> Bindings { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<ActivityTaskObservation>))]
    public EquatableArray<ActivityTaskObservation> Pawns { get; init; }
    public EquatableArray<string> SatisfiedTasks { get; init; }
    public FoodActivityRead? Food { get; init; }
    public NeedsForecastRead? Needs { get; init; }
}
