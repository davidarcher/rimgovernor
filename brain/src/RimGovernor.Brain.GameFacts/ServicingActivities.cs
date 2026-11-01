using System.Text.Json.Serialization;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.GameFacts;

namespace RimGovernor.Brain.GameFacts.Observations;

/// <summary>
/// One native census of the colony's buildings that need servicing and every free colonist that could service them (task 5.1
/// of <c>redefine-habitat-requirements</c>): a building below its MaxHitPoints that RepairUtility.PawnCanRepairEver's def
/// checks admit, a CompBreakdownable building that is BrokenDown, a CompRefuelable that is not IsFull, and a 1x1 player door
/// of a wall stuff (the door-to-wall swap NativeDesignate places). A target's hit points, power, fuel, breakdown and door
/// state are its canonical building row's (<see cref="BuildingState"/>, joined by <see cref="ServicingTarget.Id"/>); this read
/// carries only the mechanics, inputs and per-pawn measurements the row lacks. These are conditional estimates, never new
/// demand or an order.
/// </summary>
public sealed record ServicingActivityRead
{
    public required ServicingMechanics Mechanics { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<ServicingTarget>))]
    public required EquatableArray<ServicingTarget> Targets { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<ServicingWorker>))]
    public required EquatableArray<ServicingWorker> Workers { get; init; }
    /// <summary>Every spawned stack the player may use of each resource a target's work consumes, summed by def.</summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<ServicingStock>))]
    public required EquatableArray<ServicingStock> Stock { get; init; }
    /// <summary>The read's standing limits: <c>servicing_targets_truncated</c> when the target bound cut targets.</summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public required EquatableArray<string> Findings { get; init; }
}

/// <summary>
/// The native job drivers' constants the servicing durations are made of. <see cref="WorkPerSpeed"/> is the 1.7 every
/// construction driver multiplies StatDefOf.ConstructionSpeed by (JobDriver_Repair, JobDriver_RemoveBuilding,
/// JobDriver_ConstructFinishFrame); <see cref="RepairWarmupWork"/> and <see cref="RepairWorkPerHitPoint"/> are
/// JobDriver_Repair's WarmupTicks (80) and TicksBetweenRepairs (20), the work before the first hit point and between later
/// ones, at most one hit point a tick; <see cref="FixTicks"/> is JobDriver_FixBrokenDownBuilding's TicksDuration (1000);
/// <see cref="RefuelTicks"/> is JobDriver_Refuel.RefuelingDuration (240), waited once per carried load;
/// <see cref="Component"/> is the def a breakdown fix consumes (ThingDefOf.ComponentIndustrial).
/// </summary>
public sealed record ServicingMechanics(double WorkPerSpeed, double RepairWarmupWork, double RepairWorkPerHitPoint, double FixTicks,
    double RefuelTicks, string Component);

/// <summary>What servicing a target does: restore hit points, fix a breakdown, carry fuel to it, or swap a door for a wall.</summary>
[JsonConverter(typeof(WireEnumConverter<ServicingKind>))]
public enum ServicingKind { Repair, FixBreakdown, Refuel, Replace }

/// <summary>
/// One building needing servicing, by its canonical row's id, def and cell. <see cref="Home"/> is Map.areaManager.Home at its
/// cell (WorkGiver_Repair and WorkGiver_FixBrokenDownBuilding refuse a colony building outside it); <see cref="Repairable"/>
/// is RepairUtility.PawnCanRepairEver's def checks (useHitPoints and building.repairable), which WorkGiver_FixBrokenDownBuilding
/// also requires.
/// </summary>
public sealed record ServicingTarget(string Id, string Definition, Cell Cell)
{
    public required bool Home { get; init; }
    public required bool Repairable { get; init; }
    public required ServicingFuel Fuel { get; init; }
    public required ServicingRemoval Removal { get; init; }
    public required ServicingReplacement Replacement { get; init; }
}

/// <summary>A target's refuel mechanics beyond its row's fuel and burn: <see cref="ServicingFuel.Refuel"/> on a CompRefuelable.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(NotRefuelable), "not_refuelable")]
[JsonDerivedType(typeof(Refuel), "refuel")]
public abstract record ServicingFuel
{
    private protected ServicingFuel() { }

    public sealed record NotRefuelable : ServicingFuel;

    /// <summary>
    /// CompRefuelable.IsFull (less than one item's fuel short of TargetFuelLevel: no refuel job is given), GetFuelCountToFullyRefuel
    /// (the items one Refuel job asks for), Props.allowRefuelIfNotEmpty (false refuses a refuel while any fuel remains). The
    /// auto-refuel threshold is the canonical row's (<see cref="BuildingFuel.Refuelable.AutoRefuelPercent"/>).
    /// </summary>
    public sealed record Refuel(bool Full, int Items, bool AllowRefuelIfNotEmpty) : ServicingFuel;
}

/// <summary>
/// What removing the target returns: its CostListAdjusted, def.resourcesFractionWhenDeconstructed (GenLeaving's Deconstruct
/// leavings: each count times the fraction, rounded at random, at most the count) and JobDriver_Deconstruct's work (WorkToBuild
/// clamped to 20..3000); <see cref="Unread"/> names the fact when the cost list cannot be read.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Read), "read")]
[JsonDerivedType(typeof(Unread), "unread")]
public abstract record ServicingRemoval
{
    private protected ServicingRemoval() { }

    public sealed record Read(
        [property: JsonConverter(typeof(EquatableArrayJsonConverter<ServicingCost>))] EquatableArray<ServicingCost> Costs,
        double RefundFraction, double DeconstructWork) : ServicingRemoval;

    public sealed record Unread(string Fact) : ServicingRemoval;
}

/// <summary>
/// The building that replaces the target: <see cref="Wall"/> for a 1x1 player door whose stuff the wall allows
/// (GenStuff.AllowedStuffsFor(ThingDefOf.Wall)), its CostListAdjusted in that stuff, WorkToBuild in that stuff and the stuff's
/// StatDefOf.ConstructionSpeedFactor; <see cref="None"/> names why no replacement is read.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(None), "none")]
[JsonDerivedType(typeof(Wall), "wall")]
public abstract record ServicingReplacement
{
    private protected ServicingReplacement() { }

    public sealed record None(string Reason) : ServicingReplacement;

    public sealed record Wall(string Definition,
        [property: JsonConverter(typeof(EquatableArrayJsonConverter<ServicingCost>))] EquatableArray<ServicingCost> Costs,
        double WorkToBuild, double StuffSpeedFactor) : ServicingReplacement;
}

/// <summary>One resource count of a cost list.</summary>
public sealed record ServicingCost(string Resource, int Count);

/// <summary>Every spawned stack of a resource the player may use (not forbidden to the colony), summed.</summary>
public sealed record ServicingStock(string Resource, int Count);

/// <summary>
/// One free colonist's servicing of one target in one way, found by the native work giver's class: whether the pawn's work
/// settings have its work type active (Pawn_WorkSettings.WorkIsActive of the giver's work type, and for a replacement of the
/// frame-finishing giver's too, since native-led labor builds the wall: native-led labor serves it only then), the pawn's walk
/// to the target, its ability and the supply it carries. A pawn whose work type is disabled, that cannot manipulate or has no
/// path to the target has no row.
/// </summary>
public sealed record ServicingWorker(string Target, ServicingKind Kind, string Pawn, double ApproachTicks)
{
    public required bool WorkActive { get; init; }
    public required ServicingAbility Ability { get; init; }
    /// <summary>The input the work carries (a component, fuel or the wall's stuff), or null for a repair, which consumes nothing.</summary>
    public required ServicingSupply? Supply { get; init; }
}

/// <summary>
/// The pawn's measured ability for the work: <see cref="Construction"/> its StatDefOf.ConstructionSpeed (repair and replacement),
/// <see cref="Fixing"/> its StatDefOf.FixBrokenDownBuildingSuccessChance (a failed fix still consumes the component),
/// <see cref="Carrying"/> for a refuel (its duration is the carry and RefuelTicks), or <see cref="Unread"/> naming the stat the
/// native has disabled for the pawn.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Construction), "construction")]
[JsonDerivedType(typeof(Fixing), "fixing")]
[JsonDerivedType(typeof(Carrying), "carrying")]
[JsonDerivedType(typeof(Unread), "unread")]
public abstract record ServicingAbility
{
    private protected ServicingAbility() { }

    public sealed record Construction(double Speed) : ServicingAbility;

    public sealed record Fixing(double SuccessChance) : ServicingAbility;

    public sealed record Carrying : ServicingAbility;

    public sealed record Unread(string Fact) : ServicingAbility;
}

/// <summary>
/// The stack nearest the target the pawn may reserve, of the resource the work consumes: its def, the pawn's
/// carry capacity of it (Pawn_CarryTracker.MaxStackSpaceEver), its walk to the stack, the loaded carry to the target and the
/// walk back for another load. The legs of later loads are this stack's (finding <c>servicing_supply_nearest_stack_legs</c>).
/// </summary>
public sealed record ServicingSupply(string Resource, int Capacity,
    double FetchTicks, double CarryTicks, double ReturnTicks);
