using System.Text.Json.Serialization;
using RimGovernor.Brain.Domain;
using RimGovernor.Contracts.Wire.Common;
using Cell = RimGovernor.Brain.Domain.Cell;

namespace RimGovernor.Contracts.Wire.Observations;

/// <summary>Observed recipe mechanics; policy still owns garment, material and worker choices.</summary>
public sealed record RecipeIngredientObservation(string Thing, string Resource, int Count, Cell Location, int AvailableCount)
{
    public Fact<double> ApproachTicks { get; init; }
    public Fact<double> CarryTicks { get; init; }
    public Fact<double> ReturnTicks { get; init; }
    public int Capacity { get; init; }
}
public sealed record RecipeActivityOption(string Id, string Pawn, string Bench, string WorkGiver,
    string Recipe, string Product, string Stuff, string Bill, string Unfinished, Cell Location,
    [property: JsonConverter(typeof(EquatableArrayJsonConverter<RecipeIngredientObservation>))] EquatableArray<RecipeIngredientObservation> Inputs, Fact<double> TravelTicks, Fact<double> WorkTicks,
    Fact<double> OutputTravelTicks, Cell Destination)
{
    public int RequiredCount { get; init; }
}
public sealed record RecipeActivityRead
{
    public ObservationContext? Context { get; init; }
    public bool Known { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<RecipeActivityOption>))]
    public EquatableArray<RecipeActivityOption> Options { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<RecipeRetainedOutput>))]
    public EquatableArray<RecipeRetainedOutput> Retained { get; init; }
}
public sealed record RecipeRetainedOutput(string Id, string Product, string Stuff, Fact<double> CompletionTicks);
