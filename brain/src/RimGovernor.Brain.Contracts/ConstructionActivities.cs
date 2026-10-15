using RimGovernor.Brain.Domain;
using RimGovernor.Contracts.Wire.Common;
using Identity = RimGovernor.Contracts.Wire.Common.Identity;
using Cell = RimGovernor.Brain.Domain.Cell;
using System.Text.Json.Serialization;

namespace RimGovernor.Contracts.Wire.Observations;

public sealed record ConstructionActivityRequest(Identity Identity,
    [property: JsonConverter(typeof(EquatableArrayJsonConverter<Building>))] EquatableArray<Building> Buildings);
public sealed record ConstructionActivityOption(string Site, string Definition, Cell Location, string Pawn,
    string Resource, string Input, Cell Origin, int Count, int Capacity, string WorkGiver,
    Fact<double> ApproachTicks, Fact<double> DeliveryTicks, Fact<double> FinishTicks)
{
    public Fact<double> SiteApproachTicks { get; init; }
    public Fact<double> AvailableInput { get; init; }
}
public sealed record ConstructionActivityRead
{
    public ObservationContext? Context { get; init; }
    public bool Known { get; init; }
    public bool Truncated { get; init; }
    public EquatableArray<string> Findings { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<ConstructionActivityOption>))]
    public EquatableArray<ConstructionActivityOption> Options { get; init; }
}
