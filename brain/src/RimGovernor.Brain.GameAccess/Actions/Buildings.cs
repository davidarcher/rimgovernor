using DomainCell = RimGovernor.Brain.Domain.Cell;
using PlacementRotation = RimGovernor.Brain.GameFacts.Placement.Rotation;
using WireCandidate = RimGovernor.Brain.GameOrders.Placement.PlacementCandidate;

namespace RimGovernor.Brain.GameAccess;

// The placement and designation arms. The supply census reads and decoders
// are wire plumbing and are not part of this file.
public static class BuildingsRules
{
    /// <summary>
    /// The Actions/Apply haul arm of one domain haul action, the pawn and the item and nothing
    /// else. Native checks both against live state, lets the game's Hauling WorkGiver pick the storage, and applies a
    /// resend to a pawn already hauling the item without a new order.
    /// </summary>
    internal static RimGovernor.Brain.GameOrders.Operations.Action HaulAction(DomainAction action)
    {
        var h = action.Haul ?? throw BridgeContractException.Contract("not a haul action");
        var pawn = h.Pawn.Value;
        var thing = h.Thing;
        if (pawn == thing)
        {
            throw BridgeContractException.Contract("haul intent requires a distinct pawn and item");
        }
        return new RimGovernor.Brain.GameOrders.Operations.Action { Intent = new RimGovernor.Brain.GameOrders.Operations.HaulIntent { PawnId = pawn, ThingId = thing } };
    }

    /// <summary>The Actions/Apply move arm of one domain movement.</summary>
    internal static RimGovernor.Brain.GameOrders.Operations.Action MovementAction(DomainAction action)
    {
        var m = action.Movement ?? throw BridgeContractException.Contract("not a movement action");
        WireValidation.ValidId(m.Pawn.Value);
        var cell = IntentActionRules.WireCell(m.Destination);
        MovementCell(cell);
        return new RimGovernor.Brain.GameOrders.Operations.Action { Intent = new RimGovernor.Brain.GameOrders.Operations.MoveIntent { PawnId = m.Pawn.Value, Destination = cell } };
    }

    /// <summary>A present, non-negative cell with no fields the schema lacks.</summary>
    internal static void MovementCell(Cell cell)
    {
        if (cell.X < 0 || cell.Z < 0)
        {
            throw BridgeContractException.Contract("movement destination missing or invalid");
        }
    }

    /// <summary>The Actions/Apply remove_foundation arm of one foundation_removal.</summary>
    internal static RimGovernor.Brain.GameOrders.Operations.Action FoundationRemovalAction(DomainAction action)
    {
        var f = action.FoundationRemoval ?? throw BridgeContractException.Contract("not a foundation removal action");
        WireValidation.ValidId(f.Definition);
        return Designate(new RimGovernor.Brain.GameOrders.Operations.DesignateIntent { Designation = RimGovernor.Brain.GameOrders.Operations.ThingDesignation.RemoveFoundation, Location = new RimGovernor.Brain.GameOrders.Operations.DesignationLocation.At(IntentActionRules.WireCell(f.Cell)), ExpectedDef = f.Definition, ReplaceWithWall = false, Withdraw = false, Guard = RimGovernor.Brain.GameOrders.Operations.DesignationGuard.NoGuard });
    }

    /// <summary>The Actions/Apply remove_floor arm of one floor_removal.</summary>
    internal static RimGovernor.Brain.GameOrders.Operations.Action FloorRemovalAction(DomainAction action)
    {
        var f = action.FloorRemoval ?? throw BridgeContractException.Contract("not a floor removal action");
        WireValidation.ValidId(f.Definition);
        return new RimGovernor.Brain.GameOrders.Operations.Action { Intent = new RimGovernor.Brain.GameOrders.Operations.RemoveFloorIntent { Cell = IntentActionRules.WireCell(f.Cell), DefName = f.Definition } };
    }

    // The cardinal rotations only.
    private static PlacementRotation? WireRotation(Rotation rotation) =>
        rotation == Rotation.North ? PlacementRotation.North
        : rotation == Rotation.East ? PlacementRotation.East
        : rotation == Rotation.South ? PlacementRotation.South
        : rotation == Rotation.West ? PlacementRotation.West
        : null;

    /// <summary>
    /// The Actions/Apply intent for a building action, place one blueprint of the
    /// building's definition, stuff and rotation at its cell. Native validates the placement at apply time and stamps
    /// what it places with the intent key (BuildingState.intent_key).
    /// </summary>
    internal static RimGovernor.Brain.GameOrders.Operations.Action BuildingAction(DomainAction action)
    {
        var b = action.Building ?? throw BridgeContractException.Contract("not a building action");
        if (WireRotation(b.Rotation) is not { } rotation)
        {
            throw BridgeContractException.Contract("building intent requires a cardinal rotation");
        }
        var candidate = new WireCandidate { DefName = b.Definition, Stuff = b.Stuff, X = b.Cell.X, Z = b.Cell.Z, Rotation = rotation, IgnoreNaturalRock = false };
        var intent = new RimGovernor.Brain.GameOrders.Operations.BuildingIntent { Placement = candidate, ReplaceWall = false, Tier = new RimGovernor.Brain.GameOrders.Operations.ConstructionTierChange.Preserve(), MinimumFinishingSkill = new RimGovernor.Brain.GameOrders.Operations.FinishingSkillChange.Preserve() };
        if (action.FinishingSkill is { } floor)
        {
            intent = intent with { MinimumFinishingSkill = new RimGovernor.Brain.GameOrders.Operations.FinishingSkillChange.Selected(floor.MinimumSkill) };
        }
        intent = intent with { Tier = new RimGovernor.Brain.GameOrders.Operations.ConstructionTierChange.Selected((int)action.Tier) };
        if (action.ConstructionTarget.Length != 0)
        {
            intent = intent with { ExistingTargetId = action.ConstructionTarget };
        }
        if (action.ReplacesWall)
        {
            intent = intent with { ReplaceWall = true };
        }
        return new RimGovernor.Brain.GameOrders.Operations.Action { Intent = intent };
    }

    /// <summary>
    /// The RelocateIntent of a move (the game's reinstall blueprint, or its install
    /// blueprint for a packed item's inner id) or an uninstall (the Uninstall designation where the building stands).
    /// Native checks every rule live; applied means ordered and the next building read decides progress.
    /// </summary>
    internal static RimGovernor.Brain.GameOrders.Operations.Action RelocateAction(DomainAction action)
    {
        if (action.Relocation is not { } relocation)
        {
            throw BridgeContractException.Contract("not a relocation action");
        }
        var (m, uninstall) = relocation;
        if (WireRotation(m.Rotation) is not { } rotation)
        {
            throw BridgeContractException.Contract("relocate intent requires a building, a destination and a rotation");
        }
        return new RimGovernor.Brain.GameOrders.Operations.Action
        {
            Intent = new RimGovernor.Brain.GameOrders.Operations.RelocateIntent { ThingId = m.Thing, Mode = uninstall ? new RimGovernor.Brain.GameOrders.Operations.RelocationMode.Uninstall() : new RimGovernor.Brain.GameOrders.Operations.RelocationMode.Move(rotation, m.Cell) },
        };
    }

    // One settings change on one exact building, a cooler or heater target, a bed's medical flag or
    // prisoner use, a grower's crop, a claim. Native checks the building and the game's own rules live; applied means
    // set, and the next building read confirms it.
    private static RimGovernor.Brain.GameOrders.Operations.Action BuildingPatch(string thing, RimGovernor.Brain.GameOrders.Operations.BuildingPatchIntentChange change)
    {
        return new RimGovernor.Brain.GameOrders.Operations.Action { Intent = new RimGovernor.Brain.GameOrders.Operations.BuildingPatchIntent { ThingId = thing, Change = change } };
    }

    internal static RimGovernor.Brain.GameOrders.Operations.Action BuildingTemperatureAction(DomainAction action)
    {
        if (action.BuildingTemperature is not { } v || !double.IsFinite(v.Celsius))
        {
            throw BridgeContractException.Contract("not a building temperature action");
        }
        return BuildingPatch(v.Thing, new RimGovernor.Brain.GameOrders.Operations.BuildingPatchIntentChange.TargetTemperatureCase((float)v.Celsius));
    }

    internal static RimGovernor.Brain.GameOrders.Operations.Action BedUseAction(DomainAction action)
    {
        var v = action.BedUse ?? throw BridgeContractException.Contract("not a bed use action");
        RimGovernor.Brain.GameOrders.Operations.BuildingPatchIntentChange change = v.Prisoners
            ? new RimGovernor.Brain.GameOrders.Operations.BuildingPatchIntentChange.ForPrisonersCase()
            : v.Slaves
                ? new RimGovernor.Brain.GameOrders.Operations.BuildingPatchIntentChange.ForSlavesCase()
                : new RimGovernor.Brain.GameOrders.Operations.BuildingPatchIntentChange.MedicalCase(v.Medical);
        return BuildingPatch(v.Thing, change);
    }

    internal static RimGovernor.Brain.GameOrders.Operations.Action GrowerCropAction(DomainAction action)
    {
        if (action.GrowerCrop is not { } v)
        {
            throw BridgeContractException.Contract("not a grower crop action");
        }
        return BuildingPatch(v.Thing, new RimGovernor.Brain.GameOrders.Operations.BuildingPatchIntentChange.PlantDefCase(v.Crop));
    }

    internal static RimGovernor.Brain.GameOrders.Operations.Action AutoRefuelAction(DomainAction action)
    {
        var v = action.AutoRefuel ?? throw BridgeContractException.Contract("not an auto refuel action");
        return BuildingPatch(v.Thing, new RimGovernor.Brain.GameOrders.Operations.BuildingPatchIntentChange.AutoRefuelCase(v.Allow));
    }

    internal static RimGovernor.Brain.GameOrders.Operations.Action ClaimBuildingAction(DomainAction action)
    {
        var v = action.ClaimBuilding ?? throw BridgeContractException.Contract("not a claim building action");
        return BuildingPatch(v.Thing, new RimGovernor.Brain.GameOrders.Operations.BuildingPatchIntentChange.ClaimCase());
    }

    /// <summary>Wraps one DesignateIntent as an Actions/Apply action.</summary>
    private static RimGovernor.Brain.GameOrders.Operations.Action Designate(RimGovernor.Brain.GameOrders.Operations.DesignateIntent intent) => new() { Intent = intent };

    /// <summary>
    /// The DECONSTRUCT Designate of one exact building under the enclosure guard. Native
    /// checks the target's safety and the game designator live when it applies; a designation already standing applies
    /// again, and revoking authority releases every guarded designation natively. A blueprint's removal is the
    /// unguarded CANCEL Designate of the blueprint (#3742): the deconstruct designator refuses blueprints.
    /// </summary>
    internal static RimGovernor.Brain.GameOrders.Operations.Action DeconstructAction(DomainAction action)
    {
        var v = action.Deconstruction ?? throw BridgeContractException.Contract("not a deconstruction action");
        if (v.CancelsBlueprint)
        {
            return Designate(new RimGovernor.Brain.GameOrders.Operations.DesignateIntent { Location = new RimGovernor.Brain.GameOrders.Operations.DesignationLocation.ByTarget(), Designation = RimGovernor.Brain.GameOrders.Operations.ThingDesignation.Cancel, Target = IntentActionRules.NewRef(v.Target), ReplaceWithWall = false, Withdraw = false, Guard = RimGovernor.Brain.GameOrders.Operations.DesignationGuard.NoGuard });
        }
        var intent = new RimGovernor.Brain.GameOrders.Operations.DesignateIntent { Location = new RimGovernor.Brain.GameOrders.Operations.DesignationLocation.ByTarget(), Designation = RimGovernor.Brain.GameOrders.Operations.ThingDesignation.Deconstruct, Target = IntentActionRules.NewRef(v.Target), Guard = RimGovernor.Brain.GameOrders.Operations.DesignationGuard.Enclosure, ReplaceWithWall = false, Withdraw = false };
        if (v.ReplacesWithWall)
        {
            intent = intent with { ReplaceWithWall = true };
        }
        return Designate(intent);
    }

    /// <summary>
    /// The MINE Designate of one rock cell under the mine_safety guard. Native checks the
    /// rock, roof support and the game designator live when it applies, adopting a standing Mine designation; applied
    /// means designated, and the site read decides when the cell is cleared.
    /// </summary>
    internal static RimGovernor.Brain.GameOrders.Operations.Action ExcavateAction(DomainAction action)
    {
        var v = action.Excavation ?? throw BridgeContractException.Contract("not an excavation action");
        return Designate(new RimGovernor.Brain.GameOrders.Operations.DesignateIntent { Designation = RimGovernor.Brain.GameOrders.Operations.ThingDesignation.Mine, Location = new RimGovernor.Brain.GameOrders.Operations.DesignationLocation.At(IntentActionRules.WireCell(v.Cell)), ExpectedDef = v.Definition, Guard = RimGovernor.Brain.GameOrders.Operations.DesignationGuard.MineSafety, ReplaceWithWall = false, Withdraw = false });
    }

    /// <summary>
    /// The wall_upgrade-guarded Deconstruct Designate of one stone-shell wall removal, the
    /// wall's cell and, for the original demolition, the wall identity the proposal observed. Native resolves the site
    /// live.
    /// </summary>
    internal static RimGovernor.Brain.GameOrders.Operations.Action WallRemovalAction(DomainAction action)
    {
        var w = action.WallRemoval ?? throw BridgeContractException.Contract("not a wall removal action");
        var intent = new RimGovernor.Brain.GameOrders.Operations.DesignateIntent { Designation = RimGovernor.Brain.GameOrders.Operations.ThingDesignation.Deconstruct, Location = new RimGovernor.Brain.GameOrders.Operations.DesignationLocation.At(IntentActionRules.WireCell(w.Cell)), Guard = RimGovernor.Brain.GameOrders.Operations.DesignationGuard.WallUpgrade, ReplaceWithWall = false, Withdraw = false };
        if (w.Original.Length != 0)
        {
            intent = intent with { Target = IntentActionRules.NewRef(w.Original) };
        }
        return new RimGovernor.Brain.GameOrders.Operations.Action { Intent = intent };
    }

    /// <summary>
    /// The Designate of one raider-cover clearance, the designation that removes the exact cover
    /// thing the defense census named standing at its cell. Native checks the thing and the guard live when it applies;
    /// one already standing applies again.
    /// </summary>
    internal static RimGovernor.Brain.GameOrders.Operations.Action CoverAction(DomainAction action)
    {
        var v = action.CoverClearance ?? throw BridgeContractException.Contract("not a cover clearance action");
        var intent = v.Designation switch
        {
            CoverClearanceDesignation.Mine => new RimGovernor.Brain.GameOrders.Operations.DesignateIntent { Location = new RimGovernor.Brain.GameOrders.Operations.DesignationLocation.ByTarget(), Designation = RimGovernor.Brain.GameOrders.Operations.ThingDesignation.Mine, Guard = RimGovernor.Brain.GameOrders.Operations.DesignationGuard.MineSafety, ReplaceWithWall = false, Withdraw = false },
            CoverClearanceDesignation.CutPlant => new RimGovernor.Brain.GameOrders.Operations.DesignateIntent { Location = new RimGovernor.Brain.GameOrders.Operations.DesignationLocation.ByTarget(), Designation = RimGovernor.Brain.GameOrders.Operations.ThingDesignation.CutPlant, ReplaceWithWall = false, Withdraw = false, Guard = RimGovernor.Brain.GameOrders.Operations.DesignationGuard.NoGuard },
            CoverClearanceDesignation.Haul => new RimGovernor.Brain.GameOrders.Operations.DesignateIntent { Location = new RimGovernor.Brain.GameOrders.Operations.DesignationLocation.ByTarget(), Designation = RimGovernor.Brain.GameOrders.Operations.ThingDesignation.Haul, ReplaceWithWall = false, Withdraw = false, Guard = RimGovernor.Brain.GameOrders.Operations.DesignationGuard.NoGuard },
            CoverClearanceDesignation.Deconstruct => new RimGovernor.Brain.GameOrders.Operations.DesignateIntent { Location = new RimGovernor.Brain.GameOrders.Operations.DesignationLocation.ByTarget(), Designation = RimGovernor.Brain.GameOrders.Operations.ThingDesignation.Deconstruct, Guard = RimGovernor.Brain.GameOrders.Operations.DesignationGuard.Enclosure, ReplaceWithWall = false, Withdraw = false },
            var invalid => throw BridgeContractException.Contract($"cover clearance target invalid: {invalid}"),
        };
        intent = intent with { Target = IntentActionRules.NewRef(v.Thing), Location = new RimGovernor.Brain.GameOrders.Operations.DesignationLocation.At(v.Cell) };
        return Designate(intent);
    }

    /// <summary>
    /// The DesignateIntent HAUL of one exact wastepack under the wastepack guard. Native
    /// checks the pack and the guard live when it applies; a designation already standing applies again.
    /// </summary>
    internal static RimGovernor.Brain.GameOrders.Operations.Action WastepackHaulAction(DomainAction action)
    {
        var v = action.WastepackHaul ?? throw BridgeContractException.Contract("not a wastepack haul action");
        return Designate(
            new RimGovernor.Brain.GameOrders.Operations.DesignateIntent
            {
                Designation = RimGovernor.Brain.GameOrders.Operations.ThingDesignation.Haul,
                Target = IntentActionRules.NewRef(v.Thing),
                Location = new RimGovernor.Brain.GameOrders.Operations.DesignationLocation.At(IntentActionRules.WireCell(v.Cell)),
                Guard = RimGovernor.Brain.GameOrders.Operations.DesignationGuard.Wastepack,
                ExpectedDef = v.Definition, ReplaceWithWall = false, Withdraw = false }
        );
    }

    /// <summary>The DesignateIntent that orders one exact blighted plant cut.</summary>
    internal static RimGovernor.Brain.GameOrders.Operations.Action CutPlantAction(DomainAction action)
    {
        var v = action.CutPlant ?? throw BridgeContractException.Contract("not a cut plant action");
        return new RimGovernor.Brain.GameOrders.Operations.Action { Intent = new RimGovernor.Brain.GameOrders.Operations.DesignateIntent { Location = new RimGovernor.Brain.GameOrders.Operations.DesignationLocation.ByTarget(), ThingId = v.Plant, Designation = RimGovernor.Brain.GameOrders.Operations.ThingDesignation.CutPlant, ReplaceWithWall = false, Withdraw = false, Guard = RimGovernor.Brain.GameOrders.Operations.DesignationGuard.NoGuard } };
    }

    /// <summary>
    /// The DesignateIntent that allows or forbids one exact supply item. Native checks the item
    /// live when it applies; an item already in the wanted state applies again.
    /// </summary>
    internal static RimGovernor.Brain.GameOrders.Operations.Action SupplyAction(DomainAction action)
    {
        var v = action.SupplyAllow ?? throw BridgeContractException.Contract("not a supply action");
        var designation = v.Forbidden ? RimGovernor.Brain.GameOrders.Operations.ThingDesignation.Forbid : RimGovernor.Brain.GameOrders.Operations.ThingDesignation.Allow;
        return new RimGovernor.Brain.GameOrders.Operations.Action { Intent = new RimGovernor.Brain.GameOrders.Operations.DesignateIntent { Location = new RimGovernor.Brain.GameOrders.Operations.DesignationLocation.ByTarget(), ThingId = v.Thing, Designation = designation, ReplaceWithWall = false, Withdraw = false, Guard = RimGovernor.Brain.GameOrders.Operations.DesignationGuard.NoGuard } };
    }

    /// <summary>
    /// The DesignateIntent that places vanilla's Strip designation on one exact pawn or corpse.
    /// Native refuses a target that is not spawned, has nothing to strip, or is already designated.
    /// </summary>
    internal static RimGovernor.Brain.GameOrders.Operations.Action StripAction(DomainAction action)
    {
        var v = action.Strip ?? throw BridgeContractException.Contract("not a strip action");
        return new RimGovernor.Brain.GameOrders.Operations.Action { Intent = new RimGovernor.Brain.GameOrders.Operations.DesignateIntent { Location = new RimGovernor.Brain.GameOrders.Operations.DesignationLocation.ByTarget(), ThingId = v.Target, Designation = RimGovernor.Brain.GameOrders.Operations.ThingDesignation.Strip, ReplaceWithWall = false, Withdraw = false, Guard = RimGovernor.Brain.GameOrders.Operations.DesignationGuard.NoGuard } };
    }

    /// <summary>
    /// The acquisition-guarded Designate of an acquisition or a mine acquisition (designate the
    /// source), or of a stall withdraw (withdraw=true). The designation is the one the source takes: MINE for a mine
    /// acquisition, HUNT for a corpse resource, otherwise HARVEST_PLANT. Native checks the source live; a designation
    /// already in the requested state applies again, and the next census reads progress.
    /// </summary>
    internal static RimGovernor.Brain.GameOrders.Operations.Action AcquireAction(DomainAction action)
    {
        if (action.AcquireIntent is not { } acquire)
        {
            throw BridgeContractException.Contract("not an acquisition action");
        }
        var (acquisition, withdraw) = acquire;
        var designation = RimGovernor.Brain.GameOrders.Operations.ThingDesignation.HarvestPlant;
        if (action.Kind == ActionKind.MineAcquisition)
        {
            designation = RimGovernor.Brain.GameOrders.Operations.ThingDesignation.Mine;
        }
        else if (acquisition.Hunt)
        {
            designation = RimGovernor.Brain.GameOrders.Operations.ThingDesignation.Hunt;
        }
        var intent = new RimGovernor.Brain.GameOrders.Operations.DesignateIntent
        {
            Designation = designation,
            Target = IntentActionRules.NewRef(acquisition.Thing),
            Location = new RimGovernor.Brain.GameOrders.Operations.DesignationLocation.At(IntentActionRules.WireCell(acquisition.Cell)),
            Guard = RimGovernor.Brain.GameOrders.Operations.DesignationGuard.Acquisition,
            ExpectedDef = acquisition.Definition, ReplaceWithWall = false, Withdraw = false };
        if (withdraw)
        {
            intent = intent with { Withdraw = true };
        }
        return new RimGovernor.Brain.GameOrders.Operations.Action { Intent = intent };
    }

    /// <summary>The AutoHomeAreaIntent that sets the game's home-area auto-expand. A value that already holds applies again.</summary>
    internal static RimGovernor.Brain.GameOrders.Operations.Action AutoHomeAreaAction(DomainAction action)
    {
        var order =
            action.AutoHomeArea
            ?? throw BridgeContractException.Contract("not an auto home area action");
        return new RimGovernor.Brain.GameOrders.Operations.Action { Intent = new RimGovernor.Brain.GameOrders.Operations.AutoHomeAreaIntent { Enabled = order.Enabled } };
    }
}
