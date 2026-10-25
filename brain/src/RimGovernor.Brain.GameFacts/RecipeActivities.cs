using System.Text.Json.Serialization;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.GameFacts.Common;
using Cell = RimGovernor.Brain.Domain.Cell;
using RimGovernor.Brain.GameFacts;

namespace RimGovernor.Brain.GameFacts.Observations;

/// <summary>
/// Observed recipe mechanics; policy still owns garment, material and worker choices. A stack is listed only when the
/// pawn reaches it both ways, so its legs are read path costs.
/// </summary>
public sealed record RecipeIngredientObservation(string Thing, string Resource, int Count, Cell Location, int AvailableCount)
{
    public required double ApproachTicks { get; init; }
    public required double CarryTicks { get; init; }
    public required double ReturnTicks { get; init; }
    public required int Capacity { get; init; }
}
/// <summary>
/// One pawn's way to make a garment at a bench: listed only when the pawn reaches the bench, its stacks and the output
/// cell and works at a positive speed, so travel, work and output travel are read ticks.
/// </summary>
public sealed record RecipeActivityOption(string Id, string Pawn, string Bench, string WorkGiver,
    string Recipe, string Product, string Stuff, string Bill, string Unfinished, Cell Location,
    [property: JsonConverter(typeof(EquatableArrayJsonConverter<RecipeIngredientObservation>))] EquatableArray<RecipeIngredientObservation> Inputs, double TravelTicks, double WorkTicks,
    double OutputTravelTicks, Cell Destination)
{
    /// <summary>The native stationary service cell; target and route anchors are not service positions.</summary>
    public ServicePositionRead ServicePosition { get; init; } = new ServicePositionRead.Unprojected("synthetic_or_unsupported_service");
    public required int RequiredCount { get; init; }
    public ProductiveSkillWork? SkillWork { get; init; }
}
public sealed record RecipeActivityRead
{
    public required ObservationContext Context { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<RecipeActivityOption>))]
    public required EquatableArray<RecipeActivityOption> Options { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<RecipeRetainedOutput>))]
    public required EquatableArray<RecipeRetainedOutput> Retained { get; init; }
}
/// <summary>A garment a worker is making now: its work left over the worker's positive rate at this bench.</summary>
public sealed record RecipeRetainedOutput(string Id, string Product, string Stuff, double CompletionTicks);
