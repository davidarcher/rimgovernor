using System.Text.Json.Serialization;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameFacts;

namespace RimGovernor.Brain.GameOrders.Observations;

/// <summary>
/// Unplaced planned shelters the construction activity read samples (#3323): each room's interior and ring cells as
/// <see cref="ThermalCell"/> rows, and each planned heater as a <see cref="ThermalSource"/> of its definition at its
/// planned cell (never complete; an electric heater's supply from the network that cell would join, a fueled one's burn). Answered in
/// <see cref="ConstructionActivityRead.Thermal"/> with no roof routes. At most 4 rooms, 16 heaters and 1024 cells.
/// </summary>
public sealed record PlannedThermalRequest
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<PlannedThermalRoom>))]
    public required EquatableArray<PlannedThermalRoom> Rooms { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<PlannedThermalHeater>))]
    public required EquatableArray<PlannedThermalHeater> Heaters { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<PlannedThermalCooler>))]
    public EquatableArray<PlannedThermalCooler> Coolers { get; init; }
}
/// <summary>A planned room's interior bounds; the read adds the one-cell ring around them.</summary>
public sealed record PlannedThermalRoom(Cell Origin, int Width, int Height)
{
    public int Room { get; init; }
    public Cell Door { get; init; }
}
public sealed record PlannedThermalCooler(int Room, string Definition, double TargetTemperature);
/// <summary>A planned heater site (its id is the source's Target) of Definition at Cell.</summary>
public sealed record PlannedThermalHeater(string Site, string Definition, Cell Cell);
