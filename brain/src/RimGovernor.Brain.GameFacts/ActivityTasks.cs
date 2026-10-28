using RimGovernor.Brain.Domain;
using RimGovernor.Brain.GameFacts.Common;
using System.Text.Json.Serialization;
using RimGovernor.Brain.GameFacts;

namespace RimGovernor.Brain.GameFacts.Observations;
/// <summary>
/// A fresh legal binding for an already-selected participant/task, not a new winner (#3775): the bound
/// <see cref="Step"/>, plus what native read for its kind.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ActivityTaskBinding.Bound), "bound")]
[JsonDerivedType(typeof(ActivityTaskBinding.Supply), "supply")]

public abstract record ActivityTaskBinding(string Task, PawnActivityStep Step)
{
    /// <summary>A binding with nothing further read: the step alone.</summary>
    public sealed record Bound(string Task, PawnActivityStep Step) : ActivityTaskBinding(Task, Step);

    /// <summary>A Supply binding: the frame's <see cref="RemainingCount"/> of the bound stack's material still owed.</summary>
    public sealed record Supply(string Task, PawnActivityStep Step, int RemainingCount) : ActivityTaskBinding(Task, Step);

}
public sealed record ActivityTaskObservation(string Pawn, RimGovernor.Brain.GameFacts.Operations.PawnActivityExecution? Execution);
public sealed record ActivityTaskRead
{
    public required ObservationContext Context { get; init; }
    public required bool Truncated { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<ActivityTaskBinding>))]
    public required EquatableArray<ActivityTaskBinding> Bindings { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<ActivityTaskObservation>))]
    public required EquatableArray<ActivityTaskObservation> Pawns { get; init; }
    public required EquatableArray<string> SatisfiedTasks { get; init; }
    /// <summary>The participants' needs: the boundary between tasks hands no work to a pawn at its rest or food threshold.</summary>
    public required NeedsForecastRead Needs { get; init; }
}