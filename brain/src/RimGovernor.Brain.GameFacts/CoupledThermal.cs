using System.Text.Json.Serialization;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.GameFacts;

namespace RimGovernor.Brain.GameFacts.Observations;

/// <summary>
/// Physical samples, never a copy of save-owned room intent: the cells around every site and heater, every heat source,
/// the colonists who can roof and every colonist's route to every open cell marked to roof, and the map's exchange
/// constants. Outdoors is the map's current outdoor temperature; the roof rates are RoomTempTracker's private constants
/// (ThinRoofEqualizeRate; NoRoofEqualizeRate, an unroofed cell's share of the room's exchange with outdoors a tick, #3534)
/// and WallExchangePerTick its wall term. The native read throws when a constant cannot be read.
/// </summary>
public sealed record CoupledThermalRead
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<ThermalCell>))]
    public required EquatableArray<ThermalCell> Cells { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<ThermalSource>))]
    public required EquatableArray<ThermalSource> Sources { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<ThermalWorker>))]
    public required EquatableArray<ThermalWorker> Workers { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<ThermalRoofRoute>))]
    public required EquatableArray<ThermalRoofRoute> RoofRoutes { get; init; }
    public required double Outdoors { get; init; }
    public required double ThinRoofExchangePerTick { get; init; }
    public required double NoRoofExchangePerTick { get; init; }
    public required double WallExchangePerTick { get; init; }
}
/// <summary>A cell's sample. <see cref="DoorHeldOpen"/> is a door's hold-open toggle, not whether it stands open this tick.</summary>
public sealed record ThermalCell(Cell Cell, double Temperature, string Roof,
    bool Solid, bool DoorHeldOpen, string Site,
    bool FutureSolid, bool FutureDoor, double DoorExchangePerTick, bool BuildRoof);
/// <summary>
/// Heater mechanics at current conditions: an electric heater's or a fueled heat pusher's (#3673: a campfire) heat a tick
/// and the temperature it pushes toward, and where its energy comes from (<see cref="HeatSupply"/>).
/// </summary>
public sealed record ThermalSource(string Target, Cell Cell, bool Complete, double HeatPerTick, double TargetTemperature, HeatSupply Supply)
{
    /// <summary>A cooler's observed hot-side temperature. Negative HeatPerTick identifies cooling.</summary>
    [JsonRequired]
    public double HotSideTemperature { get; init; }
    [JsonRequired]
    public double PowerWatts { get; init; }
    public string Definition { get; init; } = "";
    public Cell BuildCell { get; init; }
    public Rotation BuildRotation { get; init; }
    public PlannedSiteAnswer? BuildEstimate { get; init; }
}

/// <summary>
/// Where a heat source's energy comes from now. Missing supply is never free energy: a source that is short of power,
/// unconnected or burns unsteadily heats nothing the forecast can count on.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(HeatSupply.Powered), "powered")]
[JsonDerivedType(typeof(HeatSupply.ShortOfPower), "short_of_power")]
[JsonDerivedType(typeof(HeatSupply.Unconnected), "unconnected")]
[JsonDerivedType(typeof(HeatSupply.Burning), "burning")]
[JsonDerivedType(typeof(HeatSupply.UnsteadyBurn), "unsteady_burn")]
public abstract record HeatSupply
{
    private protected HeatSupply() { }

    /// <summary>
    /// An electric heater on a power network (<see cref="Network"/>: its power comps' ids) whose current surplus covers
    /// the heater's <see cref="AdditionalWatts"/> (and a built heater is on): supplied for <see cref="Ticks"/>, a day with
    /// current weather and power held constant.
    /// </summary>
    public sealed record Powered(string Network, double AdditionalWatts, double SurplusWatts, double Ticks) : HeatSupply;

    /// <summary>An electric heater on a network whose surplus does not cover its draw, or a built heater that is off.</summary>
    public sealed record ShortOfPower(string Network, double AdditionalWatts, double SurplusWatts) : HeatSupply;

    /// <summary>An electric heater with no power network at its cell.</summary>
    public sealed record Unconnected : HeatSupply;

    /// <summary>
    /// A fueled heat pusher with a steady burn of one fuel def: unlimited supply, burning <see cref="FuelPerTick"/> items of
    /// <see cref="Fuel"/> a tick.
    /// </summary>
    public sealed record Burning(string Fuel, double FuelPerTick) : HeatSupply;

    /// <summary>A fueled heat pusher whose burn is use-, power- or externally driven, or takes more than one fuel def.</summary>
    public sealed record UnsteadyBurn : HeatSupply;
}

/// <summary>A colonist who can roof: the construction ticks one roof cell takes it (a colonist with no construction speed is not one).</summary>
public sealed record ThermalWorker(string Pawn, double RoofCellWorkTicks)
{
    public ProductiveSkillWork? SkillWork { get; init; }
}

/// <summary>A colonist's route from its own cell to a cell marked to roof.</summary>
public sealed record ThermalRoofRoute(string Pawn, Cell Cell, RoofApproach Approach);

/// <summary>How a colonist reaches a roof cell: a path of <see cref="Path.Ticks"/>, or none.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(RoofApproach.Path), "path")]
[JsonDerivedType(typeof(RoofApproach.Unreachable), "unreachable")]
public abstract record RoofApproach
{
    private protected RoofApproach() { }

    public sealed record Path(double Ticks) : RoofApproach;

    /// <summary>No path from the colonist to the cell: the colonist cannot roof it.</summary>
    public sealed record Unreachable : RoofApproach;
}
