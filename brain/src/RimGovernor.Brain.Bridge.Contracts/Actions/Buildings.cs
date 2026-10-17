using DomainCell = RimGovernor.Brain.Domain.Cell;
using Op = RimGovernor.Contracts.Wire.Operations;
using PlacementRotation = RimGovernor.Contracts.Wire.Placement.Rotation;
using WireCandidate = RimGovernor.Contracts.Wire.Placement.PlacementCandidate;

namespace RimGovernor.Brain.Bridge.Contracts;

// Go haul.go, actions.go (movementAction, foundationRemovalAction, floorRemovalAction, movementCell),
// building_intent.go, building_patch.go, move_building.go, deconstruct.go, excavation.go, wall_removal.go,
// cover_clearance.go, wastepack_haul.go, cut_plant.go, supply_allow.go (supplyAction), strip.go, acquisition.go and
// auto_home_area.go: the placement and designation arms. The supply census reads and decoders of supply_allow.go
// are wire plumbing and are not part of this port.
public static class BuildingsRules
{
    /// <summary>
    /// Go's <c>haulAction</c>: the Actions/Apply haul arm of one domain haul action, the pawn and the item and nothing
    /// else. Native checks both against live state, lets the game's Hauling WorkGiver pick the storage, and applies a
    /// resend to a pawn already hauling the item without a new order.
    /// </summary>
    internal static Op.Action HaulAction(DomainAction action)
    {
        var h = action.Haul ?? throw BridgeContractException.Contract("not a haul action");
        var pawn = h.Pawn.Value;
        var thing = h.Thing;
        if (!WireValidation.IsValidId(pawn) || !WireValidation.IsValidId(thing) || pawn == thing)
        {
            throw BridgeContractException.Contract("haul intent requires a distinct pawn and item");
        }
        return new Op.Action { Intent = new Op.HaulIntent { PawnId = pawn, ThingId = thing } };
    }

    /// <summary>Go's <c>movementAction</c>: the Actions/Apply move arm of one domain movement.</summary>
    internal static Op.Action MovementAction(DomainAction action)
    {
        var m = action.Movement ?? throw BridgeContractException.Contract("not a movement action");
        WireValidation.ValidId(m.Pawn.Value);
        var cell = IntentActionRules.WireCell(m.Destination);
        MovementCell(cell);
        return new Op.Action { Intent = new Op.MoveIntent { PawnId = m.Pawn.Value, Destination = cell } };
    }

    /// <summary>Go's <c>movementCell</c>: a present, non-negative cell with no fields the schema lacks.</summary>
    internal static void MovementCell(Cell? cell)
    {
        if (cell is null || cell.X < 0 || cell.Z < 0)
        {
            throw BridgeContractException.Contract("movement destination missing or invalid");
        }
        FactHelpers.BuildingUnknown(cell);
    }

    /// <summary>Go's <c>foundationRemovalAction</c>: the Actions/Apply remove_foundation arm of one foundation_removal.</summary>
    internal static Op.Action FoundationRemovalAction(DomainAction action)
    {
        var f = action.FoundationRemoval ?? throw BridgeContractException.Contract("not a foundation removal action");
        WireValidation.ValidId(f.Definition);
        return Designate(new Op.DesignateIntent { Designation = Op.ThingDesignation.RemoveFoundation, Cell = IntentActionRules.WireCell(f.Cell), ExpectedDef = f.Definition });
    }

    /// <summary>Go's <c>floorRemovalAction</c>: the Actions/Apply remove_floor arm of one floor_removal.</summary>
    internal static Op.Action FloorRemovalAction(DomainAction action)
    {
        var f = action.FloorRemoval ?? throw BridgeContractException.Contract("not a floor removal action");
        WireValidation.ValidId(f.Definition);
        return new Op.Action { Intent = new Op.RemoveFloorIntent { Cell = IntentActionRules.WireCell(f.Cell), DefName = f.Definition } };
    }

    // Go moveRotations / buildingAction's rotation table: the cardinal rotations only.
    private static PlacementRotation? WireRotation(Rotation rotation) =>
        rotation switch
        {
            Rotation.North => PlacementRotation.North,
            Rotation.East => PlacementRotation.East,
            Rotation.South => PlacementRotation.South,
            Rotation.West => PlacementRotation.West,
            _ => null,
        };

    /// <summary>
    /// Go's <c>buildingAction</c>: the Actions/Apply intent for a building action, place one blueprint of the
    /// building's definition, stuff and rotation at its cell. Native validates the placement at apply time and stamps
    /// what it places with the intent key (BuildingState.intent_key).
    /// </summary>
    internal static Op.Action BuildingAction(DomainAction action)
    {
        var b = action.Building ?? throw BridgeContractException.Contract("not a building action");
        if (WireRotation(b.Rotation) is not { } rotation || !WireValidation.IsValidId(b.Definition))
        {
            throw BridgeContractException.Contract("building intent requires a definition and a cardinal rotation");
        }
        var candidate = new WireCandidate { DefName = b.Definition, Stuff = b.Stuff, X = b.Cell.X, Z = b.Cell.Z, Rotation = rotation };
        var intent = new Op.BuildingIntent { Placement = candidate };
        if (action.FinishingSkill.TryGetValue(out var minimum))
        {
            intent = intent with { MinimumFinishingSkill = minimum };
        }
        if (action.Tier.TryGetValue(out var tier))
        {
            intent = intent with { Tier = (int)tier };
        }
        if (action.ConstructionTarget.Length != 0)
        {
            intent = intent with { ExistingTargetId = action.ConstructionTarget };
        }
        if (action.ReplacesWall)
        {
            intent = intent with { ReplaceWall = true };
        }
        return new Op.Action { Intent = intent };
    }

    /// <summary>
    /// Go's <c>relocateAction</c>: the RelocateIntent of a move (the game's reinstall blueprint, or its install
    /// blueprint for a packed item's inner id) or an uninstall (the Uninstall designation where the building stands).
    /// Native checks every rule live; applied means ordered and the next building read decides progress.
    /// </summary>
    internal static Op.Action RelocateAction(DomainAction action)
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
        return new Op.Action
        {
            Intent = new Op.RelocateIntent { ThingId = m.Thing, Destination = IntentActionRules.WireCell(m.Cell), Rotation = rotation, Uninstall = uninstall },
        };
    }

    // building_patch.go: one settings change on one exact building, a cooler or heater target, a bed's medical flag or
    // prisoner use, a grower's crop, a claim. Native checks the building and the game's own rules live; applied means
    // set, and the next building read confirms it.
    private static Op.Action BuildingPatch(string thing, Op.BuildingPatchIntentChange change)
    {
        if (!WireValidation.IsValidId(thing))
        {
            throw BridgeContractException.Contract("building patch requires an exact building");
        }
        return new Op.Action { Intent = new Op.BuildingPatchIntent { ThingId = thing, Change = change } };
    }

    internal static Op.Action BuildingTemperatureAction(DomainAction action)
    {
        if (action.BuildingTemperature is not { } v || !double.IsFinite(v.Celsius))
        {
            throw BridgeContractException.Contract("not a building temperature action");
        }
        return BuildingPatch(v.Thing, new Op.BuildingPatchIntentChange.TargetTemperatureCase((float)v.Celsius));
    }

    internal static Op.Action BedUseAction(DomainAction action)
    {
        var v = action.BedUse ?? throw BridgeContractException.Contract("not a bed use action");
        Op.BuildingPatchIntentChange change = v.Prisoners
            ? new Op.BuildingPatchIntentChange.ForPrisonersCase(new Op.Clear())
            : v.Slaves
                ? new Op.BuildingPatchIntentChange.ForSlavesCase(new Op.Clear())
                : new Op.BuildingPatchIntentChange.MedicalCase(v.Medical);
        return BuildingPatch(v.Thing, change);
    }

    internal static Op.Action GrowerCropAction(DomainAction action)
    {
        if (action.GrowerCrop is not { } v || !WireValidation.IsValidId(v.Crop))
        {
            throw BridgeContractException.Contract("not a grower crop action");
        }
        return BuildingPatch(v.Thing, new Op.BuildingPatchIntentChange.PlantDefCase(v.Crop));
    }

    internal static Op.Action AutoRefuelAction(DomainAction action)
    {
        var v = action.AutoRefuel ?? throw BridgeContractException.Contract("not an auto refuel action");
        return BuildingPatch(v.Thing, new Op.BuildingPatchIntentChange.AutoRefuelCase(v.Allow));
    }

    internal static Op.Action ClaimBuildingAction(DomainAction action)
    {
        var v = action.ClaimBuilding ?? throw BridgeContractException.Contract("not a claim building action");
        return BuildingPatch(v.Thing, new Op.BuildingPatchIntentChange.ClaimCase(new Op.Clear()));
    }

    /// <summary>Go's <c>designate</c>: wraps one DesignateIntent as an Actions/Apply action.</summary>
    private static Op.Action Designate(Op.DesignateIntent intent) => new() { Intent = intent };

    /// <summary>
    /// Go's <c>deconstructAction</c>: the DECONSTRUCT Designate of one exact building under the enclosure guard. Native
    /// checks the target's safety and the game designator live when it applies; a designation already standing applies
    /// again, and revoking authority releases every guarded designation natively.
    /// </summary>
    internal static Op.Action DeconstructAction(DomainAction action)
    {
        var v = action.Deconstruction ?? throw BridgeContractException.Contract("not a deconstruction action");
        if (!WireValidation.IsValidId(v.Target))
        {
            throw BridgeContractException.Contract("deconstruction target invalid");
        }
        var intent = new Op.DesignateIntent { Designation = Op.ThingDesignation.Deconstruct, Target = IntentActionRules.NewRef(v.Target), Guard = Op.DesignationGuard.Enclosure };
        if (v.ReplacesWithWall)
        {
            intent = intent with { ReplaceWithWall = true };
        }
        return Designate(intent);
    }

    /// <summary>
    /// Go's <c>excavateAction</c>: the MINE Designate of one rock cell under the mine_safety guard. Native checks the
    /// rock, roof support and the game designator live when it applies, adopting a standing Mine designation; applied
    /// means designated, and the site read decides when the cell is cleared.
    /// </summary>
    internal static Op.Action ExcavateAction(DomainAction action)
    {
        var v = action.Excavation ?? throw BridgeContractException.Contract("not an excavation action");
        return Designate(new Op.DesignateIntent { Designation = Op.ThingDesignation.Mine, Cell = IntentActionRules.WireCell(v.Cell), ExpectedDef = v.Definition, Guard = Op.DesignationGuard.MineSafety });
    }

    /// <summary>
    /// Go's <c>wallRemovalAction</c>: the wall_upgrade-guarded Deconstruct Designate of one stone-shell wall removal, the
    /// wall's cell and, for the original demolition, the wall identity the proposal observed. Native resolves the site
    /// live.
    /// </summary>
    internal static Op.Action WallRemovalAction(DomainAction action)
    {
        var w = action.WallRemoval ?? throw BridgeContractException.Contract("not a wall removal action");
        var intent = new Op.DesignateIntent { Designation = Op.ThingDesignation.Deconstruct, Cell = IntentActionRules.WireCell(w.Cell), Guard = Op.DesignationGuard.WallUpgrade };
        if (w.Original.Length != 0)
        {
            intent = intent with { Target = IntentActionRules.NewRef(w.Original) };
        }
        return new Op.Action { Intent = intent };
    }

    /// <summary>
    /// Go's <c>coverAction</c>: the Designate of one raider-cover clearance, the designation that removes the exact cover
    /// thing the defense census named standing at its cell. Native checks the thing and the guard live when it applies;
    /// one already standing applies again.
    /// </summary>
    internal static Op.Action CoverAction(DomainAction action)
    {
        var v = action.CoverClearance ?? throw BridgeContractException.Contract("not a cover clearance action");
        // Go coverDesignations: the designation and guard that remove the thing.
        (Op.ThingDesignation Designation, Op.DesignationGuard Guard)? kind = v.Designation switch
        {
            CoverClearanceDesignation.Mine => (Op.ThingDesignation.Mine, Op.DesignationGuard.MineSafety),
            CoverClearanceDesignation.CutPlant => (Op.ThingDesignation.CutPlant, Op.DesignationGuard.Unspecified),
            CoverClearanceDesignation.Haul => (Op.ThingDesignation.Haul, Op.DesignationGuard.Unspecified),
            CoverClearanceDesignation.Deconstruct => (Op.ThingDesignation.Deconstruct, Op.DesignationGuard.Enclosure),
            _ => null,
        };
        if (!WireValidation.IsValidId(v.Thing) || kind is not { } known)
        {
            throw BridgeContractException.Contract("cover clearance target invalid");
        }
        var intent = new Op.DesignateIntent { Designation = known.Designation, Target = IntentActionRules.NewRef(v.Thing), Cell = IntentActionRules.WireCell(v.Cell) };
        if (known.Guard != Op.DesignationGuard.Unspecified)
        {
            intent = intent with { Guard = known.Guard };
        }
        return Designate(intent);
    }

    /// <summary>
    /// Go's <c>wastepackHaulAction</c>: the DesignateIntent HAUL of one exact wastepack under the wastepack guard. Native
    /// checks the pack and the guard live when it applies; a designation already standing applies again.
    /// </summary>
    internal static Op.Action WastepackHaulAction(DomainAction action)
    {
        var v = action.WastepackHaul ?? throw BridgeContractException.Contract("not a wastepack haul action");
        if (!WireValidation.IsValidId(v.Thing))
        {
            throw BridgeContractException.Contract("wastepack haul target invalid");
        }
        return Designate(
            new Op.DesignateIntent
            {
                Designation = Op.ThingDesignation.Haul,
                Target = IntentActionRules.NewRef(v.Thing),
                Cell = IntentActionRules.WireCell(v.Cell),
                Guard = Op.DesignationGuard.Wastepack,
                ExpectedDef = v.Definition,
            }
        );
    }

    /// <summary>Go's <c>cutPlantAction</c>: the DesignateIntent that orders one exact blighted plant cut.</summary>
    internal static Op.Action CutPlantAction(DomainAction action)
    {
        var v = action.CutPlant ?? throw BridgeContractException.Contract("not a cut plant action");
        if (!WireValidation.IsValidId(v.Plant))
        {
            throw BridgeContractException.Contract("cut plant target invalid");
        }
        return new Op.Action { Intent = new Op.DesignateIntent { ThingId = v.Plant, Designation = Op.ThingDesignation.CutPlant } };
    }

    /// <summary>
    /// Go's <c>supplyAction</c>: the DesignateIntent that allows or forbids one exact supply item. Native checks the item
    /// live when it applies; an item already in the wanted state applies again.
    /// </summary>
    internal static Op.Action SupplyAction(DomainAction action)
    {
        var v = action.SupplyAllow ?? throw BridgeContractException.Contract("not a supply action");
        if (!WireValidation.IsValidId(v.Thing))
        {
            throw BridgeContractException.Contract("supply target invalid");
        }
        var designation = v.Forbidden ? Op.ThingDesignation.Forbid : Op.ThingDesignation.Allow;
        return new Op.Action { Intent = new Op.DesignateIntent { ThingId = v.Thing, Designation = designation } };
    }

    /// <summary>
    /// Go's <c>stripAction</c>: the DesignateIntent that places vanilla's Strip designation on one exact pawn or corpse.
    /// Native refuses a target that is not spawned, has nothing to strip, or is already designated.
    /// </summary>
    internal static Op.Action StripAction(DomainAction action)
    {
        var v = action.Strip ?? throw BridgeContractException.Contract("not a strip action");
        if (!WireValidation.IsValidId(v.Target))
        {
            throw BridgeContractException.Contract("strip target invalid");
        }
        return new Op.Action { Intent = new Op.DesignateIntent { ThingId = v.Target, Designation = Op.ThingDesignation.Strip } };
    }

    /// <summary>
    /// Go's <c>acquireAction</c>: the acquisition-guarded Designate of an acquisition or a mine acquisition (designate the
    /// source), or of a stall withdraw (withdraw=true). The designation is the one the source takes: MINE for a mine
    /// acquisition, HUNT for a corpse resource, otherwise HARVEST_PLANT. Native checks the source live; a designation
    /// already in the requested state applies again, and the next census reads progress.
    /// </summary>
    internal static Op.Action AcquireAction(DomainAction action)
    {
        if (action.AcquireIntent is not { } acquire)
        {
            throw BridgeContractException.Contract("not an acquisition action");
        }
        var (acquisition, withdraw) = acquire;
        var designation = Op.ThingDesignation.HarvestPlant;
        if (action.Kind == ActionKind.MineAcquisition)
        {
            designation = Op.ThingDesignation.Mine;
        }
        else if (acquisition.Hunt)
        {
            designation = Op.ThingDesignation.Hunt;
        }
        var intent = new Op.DesignateIntent
        {
            Designation = designation,
            Target = IntentActionRules.NewRef(acquisition.Thing),
            Cell = IntentActionRules.WireCell(acquisition.Cell),
            Guard = Op.DesignationGuard.Acquisition,
            ExpectedDef = acquisition.Definition,
        };
        if (withdraw)
        {
            intent = intent with { Withdraw = true };
        }
        return new Op.Action { Intent = intent };
    }

    /// <summary>Go's <c>autoHomeAreaAction</c>: the AutoHomeAreaIntent that sets the game's home-area auto-expand. A value that already holds applies again.</summary>
    internal static Op.Action AutoHomeAreaAction(DomainAction action)
    {
        var enabled = action.AutoHomeArea ?? throw BridgeContractException.Contract("not an auto home area action");
        return new Op.Action { Intent = new Op.AutoHomeAreaIntent { Enabled = enabled } };
    }
}
