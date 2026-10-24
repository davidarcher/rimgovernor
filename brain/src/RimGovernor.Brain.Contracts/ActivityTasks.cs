using RimGovernor.Brain.Domain;
using RimGovernor.Contracts.Wire.Common;
using Identity = RimGovernor.Contracts.Wire.Common.Identity;
using System.Text.Json.Serialization;

namespace RimGovernor.Contracts.Wire.Observations;

public sealed record ActivityTaskRequest(Identity Identity, PawnActivities Batch);
/// <summary>
/// A fresh legal binding for an already-selected participant/task, not a new winner (#3775): the bound
/// <see cref="Step"/>, plus what native read for its kind.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ActivityTaskBinding.Bound), "bound")]
[JsonDerivedType(typeof(ActivityTaskBinding.Supply), "supply")]
[JsonDerivedType(typeof(ActivityTaskBinding.Recipe), "recipe")]
public abstract record ActivityTaskBinding(string Task, PawnActivityStep Step)
{
    /// <summary>A binding with nothing further read: the step alone.</summary>
    public sealed record Bound(string Task, PawnActivityStep Step) : ActivityTaskBinding(Task, Step);

    /// <summary>A Supply binding: the frame's <see cref="RemainingCount"/> of the bound stack's material still owed.</summary>
    public sealed record Supply(string Task, PawnActivityStep Step, int RemainingCount) : ActivityTaskBinding(Task, Step);

    /// <summary>A recipe binding of a <see cref="BoundRecipeTask"/>: the pawn's <see cref="ProductiveWorkTicks"/> for the
    /// bound recipe at its current learning (the work amount over its stored-level rate, rounded up).</summary>
    public sealed record Recipe(string Task, PawnActivityStep Step, double ProductiveWorkTicks) : ActivityTaskBinding(Task, Step);
}
public sealed record ActivityTaskObservation(string Pawn, Operations.PawnActivityExecution? Execution);
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