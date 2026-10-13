using System.Text.Json;
using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Store.Codec;

public sealed class BuildingOrderConverter : ObjectCodec<BuildingOrder>
{
    protected override BuildingOrder Read(JsonElement e, Codecs o) =>
        new(
            e.Get<Building>("Building", o),
            e.Get<Fact<int>>("FinishingSkill", o),
            e.StrOr("ConstructionTarget"),
            e.BoolOr("ReplaceWall"),
            e.Get<Fact<ConstructionTier>>("Tier", o)
        );

    protected override void WriteFields(Utf8JsonWriter w, BuildingOrder v, Codecs o)
    {
        w.Put("Building", v.Building, o);
        w.Put("FinishingSkill", v.FinishingSkill, o);
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
        return kind switch
        {
            ActionKind.Ability => DomainAction.NewAbilityAction(id, Payload<Ability>()),
            ActionKind.Acquisition => DomainAction.NewAcquisitionAction(id, Payload<Acquisition>()),
            ActionKind.AcquisitionWithdraw => DomainAction.NewAcquisitionWithdrawAction(id, Payload<Acquisition>()),
            ActionKind.MineAcquisition => DomainAction.NewMineAcquisitionAction(id, Payload<Acquisition>()),
            ActionKind.ApparelPolicy => DomainAction.NewApparelPolicyAction(id, Payload<ApparelPolicy>()),
            ActionKind.Area => DomainAction.NewAreaAction(id, Payload<Area>()),
            ActionKind.AreaPlantCut => DomainAction.NewAreaPlantCutAction(id, Payload<AreaPlantCut>()),
            ActionKind.Assign => DomainAction.NewAssignAction(id, Payload<Assign>()),
            ActionKind.AutoHomeArea => DomainAction.NewAutoHomeAreaAction(id, Payload<AutoHomeAreaOrder>().Enabled),
            ActionKind.AutoRefuel => DomainAction.NewAutoRefuelAction(id, Payload<AutoRefuel>()),
            ActionKind.BedUse => DomainAction.NewBedUseAction(id, Payload<BedUse>()),
            ActionKind.Building => ReadBuilding(id, Payload<BuildingOrder>()),
            ActionKind.BuildingTemperature => DomainAction.NewBuildingTemperatureAction(id, Payload<BuildingTemperature>()),
            ActionKind.BuildRoof => DomainAction.NewBuildRoofAction(id, Payload<BuildRoof>()),
            ActionKind.Capture => DomainAction.NewCaptureAction(id, Payload<Capture>()),
            ActionKind.CaravanDeparture => DomainAction.NewCaravanDepartureAction(id, Payload<CaravanDeparture>()),
            ActionKind.ClaimBuilding => DomainAction.NewClaimBuildingAction(id, Payload<ClaimBuilding>()),
            ActionKind.Clean => DomainAction.NewCleanAction(id, Payload<Clean>()),
            ActionKind.CombatBatch => DomainAction.NewCombatBatchAction(id, Payload<CombatBatch>()),
            ActionKind.CommsTradeRequest => DomainAction.NewCommsTradeRequestAction(id, Payload<CommsTradeRequest>()),
            ActionKind.CoverClearance => DomainAction.NewCoverClearanceAction(id, Payload<CoverClearance>()),
            ActionKind.CutPlant => DomainAction.NewCutPlantAction(id, Payload<CutPlant>()),
            ActionKind.Deconstruction => DomainAction.NewDeconstructionAction(id, Payload<Deconstruction>()),
            ActionKind.DialogAnswer => DomainAction.NewDialogAnswerAction(id, Payload<DialogAnswer>()),
            ActionKind.DoorControl => DomainAction.NewDoorControlAction(id, Payload<DoorControl>()),
            ActionKind.DropEquipment => DomainAction.NewDropEquipmentAction(id, Payload<DropEquipment>()),
            ActionKind.DrugPolicy => DomainAction.NewDrugPolicyAction(id, Payload<DrugPolicy>()),
            ActionKind.Equip => DomainAction.NewEquipAction(id, Payload<Equip>()),
            ActionKind.Excavation => DomainAction.NewExcavationAction(id, Payload<Excavation>()),
            ActionKind.FloorRemoval => DomainAction.NewFloorRemovalAction(id, Payload<FloorRemoval>()),
            ActionKind.FoodPolicy => DomainAction.NewFoodPolicyAction(id, Payload<FoodPolicy>()),
            ActionKind.FoundationRemoval => DomainAction.NewFoundationRemovalAction(id, Payload<FoundationRemoval>()),
            ActionKind.Gathering => DomainAction.NewGatheringAction(id, Payload<Gathering>()),
            ActionKind.GearReplace => DomainAction.NewGearReplaceAction(id, Payload<GearReplace>()),
            ActionKind.GiveItem => DomainAction.NewGiveItemAction(id, Payload<GiveItem>()),
            ActionKind.GrowerCrop => DomainAction.NewGrowerCropAction(id, Payload<GrowerCrop>()),
            ActionKind.HackDesignation => DomainAction.NewHackDesignationAction(id, Payload<HackDesignation>()),
            ActionKind.Haul => DomainAction.NewHaulAction(id, Payload<Haul>()),
            ActionKind.Husbandry => DomainAction.NewHusbandryAction(id, Payload<Husbandry>()),
            ActionKind.IdeoligionReform => DomainAction.NewIdeoligionReformAction(id, Payload<IdeoligionReform>()),
            ActionKind.Ignite => DomainAction.NewIgniteAction(id, Payload<Ignite>()),
            ActionKind.MoodRelief => DomainAction.NewMoodReliefAction(id, Payload<MoodRelief>()),
            ActionKind.MoveBuilding => DomainAction.NewMoveBuildingAction(id, Payload<MoveBuilding>()),
            ActionKind.UninstallBuilding => DomainAction.NewUninstallBuildingAction(id, Payload<MoveBuilding>()),
            ActionKind.Movement => DomainAction.NewMovementAction(id, Payload<Movement>()),
            ActionKind.OpenCasket => DomainAction.NewOpenCasketAction(id, Payload<OpenCasket>()),
            ActionKind.OwnedDraft => DomainAction.NewOwnedDraftAction(id, Payload<OwnedDraft>()),
            ActionKind.PawnSettings => DomainAction.NewPawnSettingsAction(id, Payload<PawnSettings>()),
            ActionKind.PolicyPrune => DomainAction.NewPolicyPruneAction(id, Payload<PolicyPrune>()),
            ActionKind.PrisonerInteraction => DomainAction.NewPrisonerInteractionAction(id, Payload<PrisonerInteraction>()),
            ActionKind.ProductionBill => DomainAction.NewProductionBillAction(id, Payload<ProductionBill>()),
            ActionKind.QuestAccept => DomainAction.NewQuestAcceptAction(id, Payload<QuestAccept>()),
            ActionKind.QuestShuttle => DomainAction.NewQuestShuttleAction(id, Payload<QuestShuttle>()),
            ActionKind.ReadingPolicy => DomainAction.NewReadingPolicyAction(id, Payload<ReadingPolicy>()),
            ActionKind.RecoveryService => DomainAction.NewRecoveryServiceAction(id, Payload<RecoveryService>()),
            ActionKind.RemoveProductionBill => DomainAction.NewRemoveProductionBillAction(id, Payload<RemoveProductionBill>()),
            ActionKind.RemoveRoof => DomainAction.NewRemoveRoofAction(id, Payload<RemoveRoof>()),
            ActionKind.Repair => DomainAction.NewRepairAction(id, Payload<Repair>()),
            ActionKind.Rescue => DomainAction.NewRescueAction(id, Payload<Rescue>()),
            ActionKind.ResearchSelect => DomainAction.NewResearchSelectAction(id, Payload<ResearchSelect>()),
            ActionKind.Ritual => DomainAction.NewRitualAction(id, Payload<Ritual>()),
            ActionKind.RulesAttach => DomainAction.NewRulesAttachAction(id, Payload<RulesAttach>()),
            ActionKind.StockpilePatch => DomainAction.NewStockpilePatchAction(id, Payload<StockpilePatch>()),
            ActionKind.Strip => DomainAction.NewStripAction(id, Payload<Strip>()),
            ActionKind.Subdue => DomainAction.NewSubdueAction(id, Payload<Subdue>()),
            ActionKind.SupplyAllow or ActionKind.SupplyForbid => DomainAction.NewSupplyAllowAction(id, Payload<SupplyAllow>()),
            ActionKind.Surgery => DomainAction.NewSurgeryAction(id, Payload<Surgery>()),
            ActionKind.Tend => DomainAction.NewTendAction(id, Payload<Tend>()),
            ActionKind.Trade => DomainAction.NewTradeAction(id, Payload<Trade>()),
            ActionKind.UseItem => DomainAction.NewUseItemAction(id, Payload<UseItem>()),
            ActionKind.WallRemoval => DomainAction.NewWallRemovalAction(id, Payload<WallRemoval>()),
            ActionKind.WastepackHaul => DomainAction.NewWastepackHaulAction(id, Payload<WastepackHaul>()),
            ActionKind.WorkAssignment => DomainAction.NewWorkAssignmentAction(id, Payload<WorkAssignment>()),
            ActionKind.ZoneCellEdit => DomainAction.NewZoneCellEditAction(id, Payload<ZoneCellEdit>()),
            ActionKind.ZoneCreate => DomainAction.NewZoneCreateAction(id, Payload<ZoneCreate>()),
            ActionKind.ZoneDelete => DomainAction.NewZoneDeleteAction(id, Payload<ZoneDelete>()),
            _ => throw new StoreCorruptException("unknown action kind"),
        };
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
        if (!order.Tier.TryGetValue(out var tier))
        {
            throw new StoreCorruptException("building action without a tier");
        }
        var action = DomainAction.NewBuildingAction(id, order.Building, tier);
        if (order.FinishingSkill.TryGetValue(out var skill))
        {
            action = action.WithFinishingSkill(skill, order.ConstructionTarget);
        }
        else if (order.ConstructionTarget.Length != 0)
        {
            action = action.WithTier(tier, order.ConstructionTarget);
        }
        return order.ReplaceWall ? action.WithWallReplacement() : action;
    }
}
