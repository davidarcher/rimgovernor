using System.Text.Json;
using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Store.Codec;

public sealed class BuildingOrderConverter : ObjectCodec<BuildingOrder>
{
    protected override BuildingOrder Read(JsonElement e, Codecs o) =>
        new(
            e.Get<Building>("Building", o),
            e.Has("FinishingSkill") ? new FinishingSkillFloor(e.I32("FinishingSkill")) : null,
            e.StrOr("ConstructionTarget"),
            e.BoolOr("ReplaceWall"),
            e.Get<ConstructionTier>("Tier", o)
        );

    protected override void WriteFields(Utf8JsonWriter w, BuildingOrder v, Codecs o)
    {
        w.Put("Building", v.Building, o);
        if (v.FinishingSkill is { } floor)
        {
            w.WriteNum("FinishingSkill", floor.MinimumSkill);
        }
        w.WriteString("ConstructionTarget", v.ConstructionTarget);
        w.WriteBoolean("ReplaceWall", v.ReplaceWall);
        w.Put("Tier", v.Tier, o);
    }
}

/// <summary>
/// An action persists as its identity, kind and family payload. Reading dispatches on the kind and rebuilds the
/// action through its validated factory, so a stored row can never hold an action the constructors would refuse.
/// </summary>
public sealed class DomainActionConverter : ObjectCodec<DomainAction>
{
    protected override DomainAction Read(JsonElement e, Codecs o)
    {
        var id = e.Get<ActionId>("Id", o);
        var kind = e.Get<ActionKind>("Kind", o);
        if (!e.TryGetProperty("Payload", out var p))
        {
            throw new StoreCorruptException("action without payload");
        }
        T Payload<T>() => o.Read<T>(p);
        switch (kind)
        {
            case ActionKind.Ability:
                return DomainAction.NewAbilityAction(id, Payload<Ability>());
            case ActionKind.Acquisition:
                return DomainAction.NewAcquisitionAction(id, Payload<Acquisition>());
            case ActionKind.AcquisitionWithdraw:
                return DomainAction.NewAcquisitionWithdrawAction(id, Payload<Acquisition>());
            case ActionKind.MineAcquisition:
                return DomainAction.NewMineAcquisitionAction(id, Payload<Acquisition>());
            case ActionKind.ApparelPolicy:
                return DomainAction.NewApparelPolicyAction(id, Payload<ApparelPolicy>());
            case ActionKind.Area:
                return DomainAction.NewAreaAction(id, Payload<Area>());
            case ActionKind.AreaPlantCut:
                return DomainAction.NewAreaPlantCutAction(id, Payload<AreaPlantCut>());
            case ActionKind.Assign:
                return DomainAction.NewAssignAction(id, Payload<Assign>());
            case ActionKind.AutoHomeArea:
                return DomainAction.NewAutoHomeAreaAction(id, Payload<AutoHomeAreaOrder>().Enabled);
            case ActionKind.AutoRefuel:
                return DomainAction.NewAutoRefuelAction(id, Payload<AutoRefuel>());
            case ActionKind.BedUse:
                return DomainAction.NewBedUseAction(id, Payload<BedUse>());
            case ActionKind.Building:
                return ReadBuilding(id, Payload<BuildingOrder>());
            case ActionKind.BuildingTemperature:
                return DomainAction.NewBuildingTemperatureAction(
                    id,
                    Payload<BuildingTemperature>()
                );
            case ActionKind.BuildRoof:
                return DomainAction.NewBuildRoofAction(id, Payload<BuildRoof>());
            case ActionKind.Capture:
                return DomainAction.NewCaptureAction(id, Payload<Capture>());
            case ActionKind.CaravanDeparture:
                return DomainAction.NewCaravanDepartureAction(id, Payload<CaravanDeparture>());
            case ActionKind.ClaimBuilding:
                return DomainAction.NewClaimBuildingAction(id, Payload<ClaimBuilding>());
            case ActionKind.PawnActivities:
                return DomainAction.NewPawnActivitiesAction(id, Payload<PawnActivities>());
            case ActionKind.CombatBatch:
                return DomainAction.NewCombatBatchAction(id, Payload<CombatBatch>());
            case ActionKind.CommsTradeRequest:
                return DomainAction.NewCommsTradeRequestAction(id, Payload<CommsTradeRequest>());
            case ActionKind.CoverClearance:
                return DomainAction.NewCoverClearanceAction(id, Payload<CoverClearance>());
            case ActionKind.CutPlant:
                return DomainAction.NewCutPlantAction(id, Payload<CutPlant>());
            case ActionKind.Deconstruction:
                return DomainAction.NewDeconstructionAction(id, Payload<Deconstruction>());
            case ActionKind.DialogAnswer:
                return DomainAction.NewDialogAnswerAction(id, Payload<DialogAnswer>());
            case ActionKind.DoorControl:
                return DomainAction.NewDoorControlAction(id, Payload<DoorControl>());
            case ActionKind.DropEquipment:
                return DomainAction.NewDropEquipmentAction(id, Payload<DropEquipment>());
            case ActionKind.DrugPolicy:
                return DomainAction.NewDrugPolicyAction(id, Payload<DrugPolicy>());
            case ActionKind.Equip:
                return DomainAction.NewEquipAction(id, Payload<Equip>());
            case ActionKind.Excavation:
                return DomainAction.NewExcavationAction(id, Payload<Excavation>());
            case ActionKind.FloorRemoval:
                return DomainAction.NewFloorRemovalAction(id, Payload<FloorRemoval>());
            case ActionKind.FoodPolicy:
                return DomainAction.NewFoodPolicyAction(id, Payload<FoodPolicy>());
            case ActionKind.FoundationRemoval:
                return DomainAction.NewFoundationRemovalAction(id, Payload<FoundationRemoval>());
            case ActionKind.Gathering:
                return DomainAction.NewGatheringAction(id, Payload<Gathering>());
            case ActionKind.Conversion:
                return DomainAction.NewConversionAction(id, Payload<Conversion>());
            case ActionKind.GearReplace:
                return DomainAction.NewGearReplaceAction(id, Payload<GearReplace>());
            case ActionKind.GiveItem:
                return DomainAction.NewGiveItemAction(id, Payload<GiveItem>());
            case ActionKind.GrowerCrop:
                return DomainAction.NewGrowerCropAction(id, Payload<GrowerCrop>());
            case ActionKind.HackDesignation:
                return DomainAction.NewHackDesignationAction(id, Payload<HackDesignation>());
            case ActionKind.Haul:
                return DomainAction.NewHaulAction(id, Payload<Haul>());
            case ActionKind.Husbandry:
                return DomainAction.NewHusbandryAction(id, Payload<Husbandry>());
            case ActionKind.IdeoligionReform:
                return DomainAction.NewIdeoligionReformAction(id, Payload<IdeoligionReform>());
            case ActionKind.MoveBuilding:
                return DomainAction.NewMoveBuildingAction(id, Payload<MoveBuilding>());
            case ActionKind.UninstallBuilding:
                return DomainAction.NewUninstallBuildingAction(id, Payload<MoveBuilding>());
            case ActionKind.Movement:
                return DomainAction.NewMovementAction(id, Payload<Movement>());
            case ActionKind.OwnedDraft:
                return DomainAction.NewOwnedDraftAction(id, Payload<OwnedDraft>());
            case ActionKind.Undraft:
                return DomainAction.NewUndraftAction(id, Payload<Undraft>());
            case ActionKind.PawnSettings:
                return DomainAction.NewPawnSettingsAction(id, Payload<PawnSettings>());
            case ActionKind.PolicyPrune:
                return DomainAction.NewPolicyPruneAction(id, Payload<PolicyPrune>());
            case ActionKind.PrisonerInteraction:
                return DomainAction.NewPrisonerInteractionAction(
                    id,
                    Payload<PrisonerInteraction>()
                );
            case ActionKind.ProductionBill:
                return DomainAction.NewProductionBillAction(id, Payload<ProductionBill>());
            case ActionKind.QuestAccept:
                return DomainAction.NewQuestAcceptAction(id, Payload<QuestAccept>());
            case ActionKind.QuestShuttle:
                return DomainAction.NewQuestShuttleAction(id, Payload<QuestShuttle>());
            case ActionKind.ReadingPolicy:
                return DomainAction.NewReadingPolicyAction(id, Payload<ReadingPolicy>());
            case ActionKind.RecoveryService:
                return DomainAction.NewRecoveryServiceAction(id, Payload<RecoveryService>());
            case ActionKind.RemoveProductionBill:
                return DomainAction.NewRemoveProductionBillAction(
                    id,
                    Payload<RemoveProductionBill>()
                );
            case ActionKind.RemoveRoof:
                return DomainAction.NewRemoveRoofAction(id, Payload<RemoveRoof>());
            case ActionKind.Repair:
                return DomainAction.NewRepairAction(id, Payload<Repair>());
            case ActionKind.Rescue:
                return DomainAction.NewRescueAction(id, Payload<Rescue>());
            case ActionKind.ResearchSelect:
                return DomainAction.NewResearchSelectAction(id, Payload<ResearchSelect>());
            case ActionKind.Ritual:
                return DomainAction.NewRitualAction(id, Payload<Ritual>());
            case ActionKind.StockpilePatch:
                return DomainAction.NewStockpilePatchAction(id, Payload<StockpilePatch>());
            case ActionKind.Strip:
                return DomainAction.NewStripAction(id, Payload<Strip>());
            case ActionKind.Subdue:
                return DomainAction.NewSubdueAction(id, Payload<Subdue>());
            case ActionKind.SupplyAllow:
            case ActionKind.SupplyForbid:
                return DomainAction.NewSupplyAllowAction(id, Payload<SupplyAllow>());
            case ActionKind.Surgery:
                return DomainAction.NewSurgeryAction(id, Payload<Surgery>());
            case ActionKind.Tend:
                return DomainAction.NewTendAction(id, Payload<Tend>());
            case ActionKind.Trade:
                return DomainAction.NewTradeAction(id, Payload<Trade>());
            case ActionKind.UseItem:
                return DomainAction.NewUseItemAction(id, Payload<UseItem>());
            case ActionKind.WallRemoval:
                return DomainAction.NewWallRemovalAction(id, Payload<WallRemoval>());
            case ActionKind.WastepackHaul:
                return DomainAction.NewWastepackHaulAction(id, Payload<WastepackHaul>());
            case ActionKind.WorkAssignment:
                return DomainAction.NewWorkAssignmentAction(id, Payload<WorkAssignment>());
            case ActionKind.ZoneCellEdit:
                return DomainAction.NewZoneCellEditAction(id, Payload<ZoneCellEdit>());
            case ActionKind.ZoneCreate:
                return DomainAction.NewZoneCreateAction(id, Payload<ZoneCreate>());
            case ActionKind.ZoneDelete:
                return DomainAction.NewZoneDeleteAction(id, Payload<ZoneDelete>());
        }
        throw new StoreCorruptException("unknown action kind");
    }

    protected override void WriteFields(Utf8JsonWriter w, DomainAction v, Codecs o)
    {
        w.Put("Id", v.Id, o);
        w.Put("Kind", v.Kind, o);
        w.WritePropertyName("Payload");
        o.WriteBoxed(w, v.Payload);
    }

    private static DomainAction ReadBuilding(ActionId id, BuildingOrder order)
    {
        var action = DomainAction.NewBuildingAction(id, order.Building, order.Tier);
        if (order.FinishingSkill is { } floor)
        {
            action = action.WithFinishingSkill(floor.MinimumSkill, order.ConstructionTarget);
        }
        else if (order.ConstructionTarget.Length != 0)
        {
            action = action.WithTier(order.Tier, order.ConstructionTarget);
        }
        return order.ReplaceWall ? action.WithWallReplacement() : action;
    }
}
