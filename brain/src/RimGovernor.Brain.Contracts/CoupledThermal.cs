using System.Text.Json.Serialization;
using RimGovernor.Brain.Domain;

namespace RimGovernor.Contracts.Wire.Observations;

/// <summary>Physical samples, never a copy of save-owned room intent. Missing cells are unknown.</summary>
public sealed record CoupledThermalRead
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<ThermalCell>))]
    public EquatableArray<ThermalCell> Cells { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<ThermalSource>))]
    public EquatableArray<ThermalSource> Sources { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<ThermalWorker>))]
    public EquatableArray<ThermalWorker> Workers { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<ThermalRoofRoute>))]
    public EquatableArray<ThermalRoofRoute> RoofRoutes { get; init; }
    public Fact<double> Outdoors { get; init; }
    public Fact<double> ThinRoofExchangePerTick { get; init; }
    public Fact<double> WallExchangePerTick { get; init; }
    public bool Truncated { get; init; }
}
public sealed record ThermalCell(Cell Cell, Fact<double> Temperature, string Roof,
    bool Solid, bool Door, bool DoorClosed, string Site, string FutureDefinition,
    bool FutureSolid, bool FutureDoor, double DoorExchangePerTick, bool BuildRoof);
/// <summary>Electric heater mechanics at current conditions. Missing supply is unknown, not free energy.</summary>
public sealed record ThermalSource(string Target, Cell Cell, bool Complete, Fact<double> HeatPerTick,
    Fact<double> TargetTemperature, Fact<double> SupplyTicks)
{
    public string PowerNetwork { get; init; } = "";
    public Fact<double> AdditionalWatts { get; init; }
    public Fact<double> SurplusWatts { get; init; }
}
public sealed record ThermalWorker(string Pawn, Fact<double> RoofCellWorkTicks);

/// <summary>
/// Unplaced planned shelters the construction activity read samples (#3323): each room's interior and ring cells as
/// <see cref="ThermalCell"/> rows, and each planned heater as a <see cref="ThermalSource"/> of its definition at its
/// planned cell (never complete; supply from the network that cell would join). Answered in
/// <see cref="ConstructionActivityRead.Thermal"/> with no roof routes. At most 4 rooms, 16 heaters and 1024 cells.
/// </summary>
public sealed record PlannedThermalRequest
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<PlannedThermalRoom>))]
    public EquatableArray<PlannedThermalRoom> Rooms { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<PlannedThermalHeater>))]
    public EquatableArray<PlannedThermalHeater> Heaters { get; init; }
}
/// <summary>A planned room's interior bounds; the read adds the one-cell ring around them.</summary>
public sealed record PlannedThermalRoom(string Room, Cell Origin, int Width, int Height);
/// <summary>A planned heater site (its id is the source's Target) of Definition at Cell.</summary>
public sealed record PlannedThermalHeater(string Site, string Definition, Cell Cell);

public sealed record ThermalRoofRoute(string Pawn, Cell Cell, Fact<double> ApproachTicks);
