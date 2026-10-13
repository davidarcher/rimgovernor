namespace RimGovernor.Brain.Policy;

// Go's defined string types PlannedRole, NodeKind, ZoneKind and ReservationKind, as the port conventions' enums: the Go string is the wire name.

/// <summary>A planned room's role.</summary>
public enum PlannedRole
{
    /// <summary>Go's empty PlannedRole: a room with no plan role.</summary>
    [WireName("")]
    None,

    [WireName("hospital")]
    Hospital,

    [WireName("prison")]
    Prison,

    [WireName("kitchen")]
    Kitchen,

    [WireName("freezer")]
    Freezer,

    [WireName("butchery")]
    Butchery,

    [WireName("storage")]
    Storage,

    [WireName("workshop")]
    Workshop,

    [WireName("reserve")]
    Reserve,

    [WireName("bedroom")]
    Bedroom,

    [WireName("shelter")]
    Shelter,

    [WireName("dining")]
    Dining,

    [WireName("rec")]
    Rec,

    [WireName("lab")]
    Lab,

    [WireName("tomb")]
    Tomb,

    [WireName("morgue")]
    Morgue,

    [WireName("graveyard")]
    Graveyard,

    [WireName("waste_yard")]
    WasteYard,

    [WireName("training_range")]
    TrainingRange,

    [WireName("porch")]
    Porch,

    [WireName("sparring_ring")]
    SparringRing,

    [WireName("throne")]
    Throne,

    [WireName("suite")]
    Suite,

    [WireName("battery")]
    Battery,

    [WireName("yard")]
    Yard,

    [WireName("airlock")]
    Airlock,

    [WireName("meal_closet")]
    MealCloset,

    [WireName("circulation")]
    Circulation,

    [WireName("armory")]
    Armory,

    [WireName("wardrobe")]
    Wardrobe,

    [WireName("incinerator")]
    Incinerator,

    [WireName("isolation-room")]
    IsolationRoom,

    [WireName("containment-cell")]
    ContainmentCell,

    [WireName("worship")]
    Worship,

    [WireName("barn")]
    Barn,

    [WireName("pen")]
    Pen,

    [WireName("nursery")]
    Nursery,

    [WireName("playroom")]
    Playroom,

    [WireName("classroom")]
    Classroom,

    [WireName("deathrest-chamber")]
    DeathrestChamber,
}

/// <summary>A site node's kind.</summary>
public enum NodeKind
{
    [WireName("")]
    None,

    [WireName("perimeter_wall")]
    Ring,

    [WireName("gate")]
    Gate,

    [WireName("killbox")]
    Killbox,

    [WireName("killbox_approach")]
    Approach,

    [WireName("killbox_fence")]
    Fence,

    [WireName("perimeter_pump")]
    Pump,

    [WireName("mortar")]
    Mortar,

    [WireName("cover_clear")]
    CoverClear,

    [WireName("pocket_wall")]
    PocketWall,

    [WireName("wind_turbine")]
    Turbine,

    [WireName("solar")]
    Solar,

    [WireName("vent")]
    Vent,

    [WireName("defense_corridor")]
    DefCorridor,

    [WireName("defense_trap_lane")]
    DefLane,

    [WireName("defense_funnel")]
    DefFunnel,

    [WireName("defense_kill_fence")]
    DefFence,

    [WireName("defense_spike_traps")]
    DefTraps,

    [WireName("defense_firing_line")]
    DefFiring,

    [WireName("defense_shooter_floor")]
    DefShooter,

    [WireName("defense_turrets")]
    DefTurrets,

    [WireName("defense_mortars")]
    DefMortars,

    [WireName("defense_ieds")]
    DefIEDs,

    [WireName("defense_bait")]
    DefBait,
}

/// <summary>A zone's kind.</summary>
public enum ZoneKind
{
    [WireName("")]
    None,

    [WireName("field")]
    Field,

    [WireName("pasture")]
    Pasture,

    [WireName("mining")]
    Mining,

    [WireName("wood")]
    Wood,

    [WireName("no_go")]
    NoGo,

    [WireName("core")]
    Core,
}

/// <summary>An infrastructure site the plan holds. A site node's footprint is held under its node kind too (LayoutPlan.Reserved), so every node kind is a member.</summary>
public enum ReservationKind
{
    [WireName("")]
    None,

    [WireName("battery_room")]
    BatteryRoom,

    [WireName("geothermal")]
    Geothermal,

    [WireName("barn")]
    Barn,

    [WireName("pen")]
    Pen,

    [WireName("yard")]
    Yard,

    [WireName("cooler_exhaust")]
    Exhaust,

    [WireName("perimeter_wall")]
    Ring,

    [WireName("gate")]
    Gate,

    [WireName("killbox")]
    Killbox,

    [WireName("killbox_approach")]
    Approach,

    [WireName("killbox_fence")]
    Fence,

    [WireName("perimeter_pump")]
    Pump,

    [WireName("mortar")]
    Mortar,

    [WireName("cover_clear")]
    CoverClear,

    [WireName("pocket_wall")]
    PocketWall,

    [WireName("wind_turbine")]
    Turbine,

    [WireName("solar")]
    Solar,

    [WireName("vent")]
    Vent,

    [WireName("defense_corridor")]
    DefCorridor,

    [WireName("defense_trap_lane")]
    DefLane,

    [WireName("defense_funnel")]
    DefFunnel,

    [WireName("defense_kill_fence")]
    DefFence,

    [WireName("defense_spike_traps")]
    DefTraps,

    [WireName("defense_firing_line")]
    DefFiring,

    [WireName("defense_shooter_floor")]
    DefShooter,

    [WireName("defense_turrets")]
    DefTurrets,

    [WireName("defense_mortars")]
    DefMortars,

    [WireName("defense_ieds")]
    DefIEDs,

    [WireName("defense_bait")]
    DefBait,
}

public static partial class PolicyFunctions
{
    public static ReservationKind ReserveBarn => ReservationKind.Barn;

    public static ReservationKind ReservePen => ReservationKind.Pen;

    /// <summary>Go's <c>ReservationKind(s.Kind)</c>: the reservation kind that holds a site node's ground.</summary>
    internal static ReservationKind ReservationKindOf(NodeKind kind) =>
        Wire.TryParse<ReservationKind>(Wire.Name(kind), out var reserved) ? reserved : throw new DomainException($"node kind {Wire.Name(kind)} has no reservation kind");
}
