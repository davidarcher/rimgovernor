using DomainCell = RimGovernor.Brain.Domain.Cell;
using Op = RimGovernor.Protocol.Operations;
using PlacementRotation = RimGovernor.Protocol.Placement.Rotation;
using WireCandidate = RimGovernor.Protocol.Placement.PlacementCandidate;

namespace RimGovernor.Brain.Bridge.Contracts;

// Go haul.go, actions.go (movementAction, foundationRemovalAction, floorRemovalAction, movementCell),
// building_intent.go, building_patch.go, move_building.go, deconstruct.go, excavation.go, wall_removal.go,
// cover_clearance.go, wastepack_haul.go, cut_plant.go, supply_allow.go (supplyAction), strip.go, acquisition.go and
// auto_home_area.go: the placement and designation arms. The supply census reads and decoders of supply_allow.go
// are wire plumbing and are not part of this port.
public static partial class BridgeFunctions
{
    /// <summary>
    /// Go's <c>haulAction</c>: the Actions/Apply haul arm of one domain haul action, the pawn and the item and nothing
    /// else. Native checks both against live state, lets the game's Hauling WorkGiver pick the storage, and applies a
    /// resend to a pawn already hauling the item without a new order.
    /// </summary>
    private static Op.Action HaulAction(DomainAction action)
    {
        var h = action.Haul ?? throw Contract("not a haul action");
        var pawn = h.Pawn.Value;
        var thing = h.Thing;
        if (!IsValidId(pawn) || !IsValidId(thing) || pawn == thing)
        {
            throw Contract("haul intent requires a distinct pawn and item");
        }
        return new Op.Action { Haul = new Op.HaulIntent { PawnId = pawn, ThingId = thing } };
    }

    /// <summary>Go's <c>movementAction</c>: the Actions/Apply move arm of one domain movement.</summary>
    private static Op.Action MovementAction(DomainAction action)
    {
        var m = action.Movement ?? throw Contract("not a movement action");
        ValidId(m.Pawn.Value);
        var cell = WireCell(m.Destination);
        MovementCell(cell);
        return new Op.Action { Move = new Op.MoveIntent { PawnId = m.Pawn.Value, Destination = cell } };
    }

    /// <summary>Go's <c>movementCell</c>: a present, non-negative cell with no fields the schema lacks.</summary>
    internal static void MovementCell(Cell? cell)
    {
        if (cell is null || !cell.HasX || !cell.HasZ || cell.X < 0 || cell.Z < 0)
        {
            throw Contract("movement destination missing or invalid");
        }
        BuildingUnknown(cell);
    }

    /// <summary>Go's <c>foundationRemovalAction</c>: the Actions/Apply remove_foundation arm of one foundation_removal.</summary>
    private static Op.Action FoundationRemovalAction(DomainAction action)
    {
        var f = action.FoundationRemoval ?? throw Contract("not a foundation removal action");
        ValidId(f.Definition);
        return Designate(new Op.DesignateIntent { Designation = Op.ThingDesignation.RemoveFoundation, Cell = WireCell(f.Cell), ExpectedDef = f.Definition });
    }

    /// <summary>Go's <c>floorRemovalAction</c>: the Actions/Apply remove_floor arm of one floor_removal.</summary>
    private static Op.Action FloorRemovalAction(DomainAction action)
    {
        var f = action.FloorRemoval ?? throw Contract("not a floor removal action");
        ValidId(f.Definition);
        return new Op.Action { RemoveFloor = new Op.RemoveFloorIntent { Cell = WireCell(f.Cell), DefName = f.Definition } };
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
    private static Op.Action BuildingAction(DomainAction action)
    {
        var b = action.Building ?? throw Contract("not a building action");
        if (WireRotation(b.Rotation) is not { } rotation || !IsValidId(b.Definition))
        {
            throw Contract("building intent requires a definition and a cardinal rotation");
        }
        var candidate = new WireCandidate { DefName = b.Definition, Stuff = b.Stuff, X = b.Cell.X, Z = b.Cell.Z, Rotation = rotation };
        var intent = new Op.BuildingIntent { Placement = candidate };
        if (action.FinishingSkill.TryGetValue(out var minimum))
        {
            intent.MinimumFinishingSkill = minimum;
        }
        if (action.Tier.TryGetValue(out var tier))
        {
            intent.Tier = (int)tier;
        }
        if (action.ConstructionTarget.Length != 0)
        {
            intent.ExistingTargetId = action.ConstructionTarget;
        }
        if (action.ReplacesWall)
        {
            intent.ReplaceWall = true;
        }
        return new Op.Action { Building = intent };
    }

    /// <summary>
    /// Go's <c>relocateAction</c>: the RelocateIntent of a move (the game's reinstall blueprint, or its install
    /// blueprint for a packed item's inner id) or an uninstall (the Uninstall designation where the building stands).
    /// Native checks every rule live; applied means ordered and the next building read decides progress.
    /// </summary>
    private static Op.Action RelocateAction(DomainAction action)
    {
        if (action.Relocation is not { } relocation)
        {
            throw Contract("not a relocation action");
        }
        var (m, uninstall) = relocation;
        if (WireRotation(m.Rotation) is not { } rotation)
        {
            throw Contract("relocate intent requires a building, a destination and a rotation");
        }
        return new Op.Action
        {
            Relocate = new Op.RelocateIntent { ThingId = m.Thing, Destination = WireCell(m.Cell), Rotation = rotation, Uninstall = uninstall },
        };
    }

    // building_patch.go: one settings change on one exact building, a cooler or heater target, a bed's medical flag or
    // prisoner use, a grower's crop, a claim. Native checks the building and the game's own rules live; applied means
    // set, and the next building read confirms it.
    private static Op.Action BuildingPatch(string thing, Action<Op.BuildingPatchIntent> change)
    {
        if (!IsValidId(thing))
        {
            throw Contract("building patch requires an exact building");
        }
        var intent = new Op.BuildingPatchIntent { ThingId = thing };
        change(intent);
        return new Op.Action { BuildingPatch = intent };
    }

    private static Op.Action BuildingTemperatureAction(DomainAction action)
    {
        if (action.BuildingTemperature is not { } v || !double.IsFinite(v.Celsius))
        {
            throw Contract("not a building temperature action");
        }
        return BuildingPatch(v.Thing, i => i.TargetTemperature = (float)v.Celsius);
    }

    private static Op.Action BedUseAction(DomainAction action)
    {
        var v = action.BedUse ?? throw Contract("not a bed use action");
        return BuildingPatch(
            v.Thing,
            i =>
            {
                if (v.Prisoners)
                {
                    i.ForPrisoners = new Op.Clear();
                }
                else if (v.Slaves)
                {
                    i.ForSlaves = new Op.Clear();
                }
                else
                {
                    i.Medical = v.Medical;
                }
            }
        );
    }

    private static Op.Action GrowerCropAction(DomainAction action)
    {
        if (action.GrowerCrop is not { } v || !IsValidId(v.Crop))
        {
            throw Contract("not a grower crop action");
        }
        return BuildingPatch(v.Thing, i => i.PlantDef = v.Crop);
    }

    private static Op.Action AutoRefuelAction(DomainAction action)
    {
        var v = action.AutoRefuel ?? throw Contract("not an auto refuel action");
        return BuildingPatch(v.Thing, i => i.AutoRefuel = v.Allow);
    }

    private static Op.Action ClaimBuildingAction(DomainAction action)
    {
        var v = action.ClaimBuilding ?? throw Contract("not a claim building action");
        return BuildingPatch(v.Thing, i => i.Claim = new Op.Clear());
    }

    /// <summary>Go's <c>designate</c>: wraps one DesignateIntent as an Actions/Apply action.</summary>
    private static Op.Action Designate(Op.DesignateIntent intent) => new() { Designate = intent };

    /// <summary>
    /// Go's <c>deconstructAction</c>: the DECONSTRUCT Designate of one exact building under the enclosure guard. Native
    /// checks the target's safety and the game designator live when it applies; a designation already standing applies
    /// again, and revoking authority releases every guarded designation natively.
    /// </summary>
    private static Op.Action DeconstructAction(DomainAction action)
    {
        var v = action.Deconstruction ?? throw Contract("not a deconstruction action");
        if (!IsValidId(v.Target))
        {
            throw Contract("deconstruction target invalid");
        }
        var intent = new Op.DesignateIntent { Designation = Op.ThingDesignation.Deconstruct, Target = NewRef(v.Target), Guard = Op.DesignationGuard.Enclosure };
        if (v.ReplacesWithWall)
        {
            intent.ReplaceWithWall = true;
        }
        return Designate(intent);
    }

    /// <summary>
    /// Go's <c>excavateAction</c>: the MINE Designate of one rock cell under the mine_safety guard. Native checks the
    /// rock, roof support and the game designator live when it applies, adopting a standing Mine designation; applied
    /// means designated, and the site read decides when the cell is cleared.
    /// </summary>
    private static Op.Action ExcavateAction(DomainAction action)
    {
        var v = action.Excavation ?? throw Contract("not an excavation action");
        return Designate(new Op.DesignateIntent { Designation = Op.ThingDesignation.Mine, Cell = WireCell(v.Cell), ExpectedDef = v.Definition, Guard = Op.DesignationGuard.MineSafety });
    }

    /// <summary>
    /// Go's <c>wallRemovalAction</c>: the wall_upgrade-guarded Deconstruct Designate of one stone-shell wall removal, the
    /// wall's cell and, for the original demolition, the wall identity the proposal observed. Native resolves the site
    /// live.
    /// </summary>
    private static Op.Action WallRemovalAction(DomainAction action)
    {
        var w = action.WallRemoval ?? throw Contract("not a wall removal action");
        var intent = new Op.DesignateIntent { Designation = Op.ThingDesignation.Deconstruct, Cell = WireCell(w.Cell), Guard = Op.DesignationGuard.WallUpgrade };
        if (w.Original.Length != 0)
        {
            intent.Target = NewRef(w.Original);
        }
        return new Op.Action { Designate = intent };
    }

    /// <summary>
    /// Go's <c>coverAction</c>: the Designate of one raider-cover clearance, the designation that removes the exact cover
    /// thing the defense census named standing at its cell. Native checks the thing and the guard live when it applies;
    /// one already standing applies again.
    /// </summary>
    private static Op.Action CoverAction(DomainAction action)
    {
        var v = action.CoverClearance ?? throw Contract("not a cover clearance action");
        // Go coverDesignations: the designation and guard that remove the thing.
        (Op.ThingDesignation Designation, Op.DesignationGuard Guard)? kind = v.Designation switch
        {
            CoverClearanceDesignation.Mine => (Op.ThingDesignation.Mine, Op.DesignationGuard.MineSafety),
            CoverClearanceDesignation.CutPlant => (Op.ThingDesignation.CutPlant, Op.DesignationGuard.Unspecified),
            CoverClearanceDesignation.Haul => (Op.ThingDesignation.Haul, Op.DesignationGuard.Unspecified),
            CoverClearanceDesignation.Deconstruct => (Op.ThingDesignation.Deconstruct, Op.DesignationGuard.Enclosure),
            _ => null,
        };
        if (!IsValidId(v.Thing) || kind is not { } known)
        {
            throw Contract("cover clearance target invalid");
        }
        var intent = new Op.DesignateIntent { Designation = known.Designation, Target = NewRef(v.Thing), Cell = WireCell(v.Cell) };
        if (known.Guard != Op.DesignationGuard.Unspecified)
        {
            intent.Guard = known.Guard;
        }
        return Designate(intent);
    }

    /// <summary>
    /// Go's <c>wastepackHaulAction</c>: the DesignateIntent HAUL of one exact wastepack under the wastepack guard. Native
    /// checks the pack and the guard live when it applies; a designation already standing applies again.
    /// </summary>
    private static Op.Action WastepackHaulAction(DomainAction action)
    {
        var v = action.WastepackHaul ?? throw Contract("not a wastepack haul action");
        if (!IsValidId(v.Thing))
        {
            throw Contract("wastepack haul target invalid");
        }
        return Designate(
            new Op.DesignateIntent
            {
                Designation = Op.ThingDesignation.Haul,
                Target = NewRef(v.Thing),
                Cell = WireCell(v.Cell),
                Guard = Op.DesignationGuard.Wastepack,
                ExpectedDef = v.Definition,
            }
        );
    }

    /// <summary>Go's <c>cutPlantAction</c>: the DesignateIntent that orders one exact blighted plant cut.</summary>
    private static Op.Action CutPlantAction(DomainAction action)
    {
        var v = action.CutPlant ?? throw Contract("not a cut plant action");
        if (!IsValidId(v.Plant))
        {
            throw Contract("cut plant target invalid");
        }
        return new Op.Action { Designate = new Op.DesignateIntent { ThingId = v.Plant, Designation = Op.ThingDesignation.CutPlant } };
    }

    /// <summary>
    /// Go's <c>supplyAction</c>: the DesignateIntent that allows or forbids one exact supply item. Native checks the item
    /// live when it applies; an item already in the wanted state applies again.
    /// </summary>
    private static Op.Action SupplyAction(DomainAction action)
    {
        var v = action.SupplyAllow ?? throw Contract("not a supply action");
        if (!IsValidId(v.Thing))
        {
            throw Contract("supply target invalid");
        }
        var designation = v.Forbidden ? Op.ThingDesignation.Forbid : Op.ThingDesignation.Allow;
        return new Op.Action { Designate = new Op.DesignateIntent { ThingId = v.Thing, Designation = designation } };
    }

    /// <summary>
    /// Go's <c>stripAction</c>: the DesignateIntent that places vanilla's Strip designation on one exact pawn or corpse.
    /// Native refuses a target that is not spawned, has nothing to strip, or is already designated.
    /// </summary>
    private static Op.Action StripAction(DomainAction action)
    {
        var v = action.Strip ?? throw Contract("not a strip action");
        if (!IsValidId(v.Target))
        {
            throw Contract("strip target invalid");
        }
        return new Op.Action { Designate = new Op.DesignateIntent { ThingId = v.Target, Designation = Op.ThingDesignation.Strip } };
    }

    /// <summary>
    /// Go's <c>acquireAction</c>: the acquisition-guarded Designate of an acquisition or a mine acquisition (designate the
    /// source), or of a stall withdraw (withdraw=true). The designation is the one the source takes: MINE for a mine
    /// acquisition, HUNT for a corpse resource, otherwise HARVEST_PLANT. Native checks the source live; a designation
    /// already in the requested state applies again, and the next census reads progress.
    /// </summary>
    private static Op.Action AcquireAction(DomainAction action)
    {
        if (action.AcquireIntent is not { } acquire)
        {
            throw Contract("not an acquisition action");
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
            Target = NewRef(acquisition.Thing),
            Cell = WireCell(acquisition.Cell),
            Guard = Op.DesignationGuard.Acquisition,
            ExpectedDef = acquisition.Definition,
        };
        if (withdraw)
        {
            intent.Withdraw = true;
        }
        return new Op.Action { Designate = intent };
    }

    /// <summary>Go's <c>autoHomeAreaAction</c>: the AutoHomeAreaIntent that sets the game's home-area auto-expand. A value that already holds applies again.</summary>
    private static Op.Action AutoHomeAreaAction(DomainAction action)
    {
        var enabled = action.AutoHomeArea ?? throw Contract("not an auto home area action");
        return new Op.Action { AutoHomeArea = new Op.AutoHomeAreaIntent { Enabled = enabled } };
    }
}
