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

public sealed record ThermalRoofRoute(string Pawn, Cell Cell, Fact<double> ApproachTicks);
