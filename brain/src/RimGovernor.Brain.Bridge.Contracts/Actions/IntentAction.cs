using System.Collections.Frozen;
using DomainCell = RimGovernor.Brain.Domain.Cell;
using Op = RimGovernor.Contracts.Wire.Operations;
using WireRef = RimGovernor.Contracts.Wire.Common.Ref;

namespace RimGovernor.Brain.Bridge.Contracts;

// The intent-kind registry, IntentAction and the wire helpers every builder shares. The builders
// themselves sit in the sibling files of this folder, one group per family. The Actions/Apply writer
// (ActionsWriter.Apply) is wire plumbing and is not part of this file.
public static class IntentActionRules
{
    /// <summary>
    /// A batch of idempotent intents that native applies in order, each validated
    /// against live state and applied or refused on its own. A resent key returns its first result.
    /// </summary>
    public const string ActionsApplyMethod = "rimgovernor/operations_apply";

    // The Actions/Apply builder of every intent-mode kind. Adding a kind: a new Action oneof arm in
    // operations.proto, a native IActionHandler for it in NativeActionDispatch.cs, and a builder listed here.
    private static readonly FrozenDictionary<ActionKind, Func<DomainAction, Op.Action>> IntentKinds = new Dictionary<ActionKind, Func<DomainAction, Op.Action>>
    {
        [ActionKind.Trade] = TradeRules.TradeAction,
        [ActionKind.Haul] = BuildingsRules.HaulAction,
        [ActionKind.OwnedDraft] = GiveJobRules.DraftAction,
        [ActionKind.Subdue] = GiveJobRules.SubdueAction,
        [ActionKind.Building] = BuildingsRules.BuildingAction,
        [ActionKind.Movement] = BuildingsRules.MovementAction,
        [ActionKind.ApparelPolicy] = Orders.ApparelPolicyAction,
        [ActionKind.ResearchSelect] = Orders.ResearchAction,
        [ActionKind.DialogAnswer] = Orders.DialogAction,
        [ActionKind.PrisonerInteraction] = Orders.PrisonerInteractionAction,
        [ActionKind.QuestAccept] = Orders.QuestAcceptAction,
        [ActionKind.QuestShuttle] = Orders.QuestShuttleAction,
        [ActionKind.HackDesignation] = Orders.HackDesignationAction,
        [ActionKind.GiveItem] = Orders.GiveItemAction,
        [ActionKind.Ritual] = Orders.RitualAction,
        [ActionKind.IdeoligionReform] = Orders.IdeoligionReformAction,
        [ActionKind.Conversion] = Orders.ConversionAction,
        [ActionKind.Ability] = Orders.AbilityAction,
        [ActionKind.Ignite] = Orders.IgniteAction,
        [ActionKind.Gathering] = Orders.GatheringAction,
        [ActionKind.RemoveProductionBill] = Production.RemoveProductionBillAction,
        [ActionKind.DoorControl] = Orders.DoorControlAction,
        [ActionKind.CaravanDeparture] = Orders.CaravanDepartureAction,
        [ActionKind.Assign] = Orders.AssignAction,
        [ActionKind.WorkAssignment] = Production.WorkSettingsAction,
        [ActionKind.ProductionBill] = Production.ProductionBillAction,
        [ActionKind.Husbandry] = Orders.HusbandryAction,
        [ActionKind.ZoneCreate] = Zones.ZoneCreateAction,
        [ActionKind.ZoneDelete] = Zones.ZoneDeleteAction,
        [ActionKind.ZoneCellEdit] = Zones.ZoneCellsAction,
        [ActionKind.StockpilePatch] = Zones.StockpileAction,
        [ActionKind.FoundationRemoval] = BuildingsRules.FoundationRemovalAction,
        [ActionKind.FloorRemoval] = BuildingsRules.FloorRemovalAction,
        [ActionKind.CoverClearance] = BuildingsRules.CoverAction,
        [ActionKind.WastepackHaul] = BuildingsRules.WastepackHaulAction,
        [ActionKind.CutPlant] = BuildingsRules.CutPlantAction,
        [ActionKind.SupplyAllow] = BuildingsRules.SupplyAction,
        [ActionKind.SupplyForbid] = BuildingsRules.SupplyAction,
        [ActionKind.Deconstruction] = BuildingsRules.DeconstructAction,
        [ActionKind.Excavation] = BuildingsRules.ExcavateAction,
        [ActionKind.WallRemoval] = BuildingsRules.WallRemovalAction,
        [ActionKind.RecoveryService] = GiveJobRules.RecoverAction,
        [ActionKind.MoveBuilding] = BuildingsRules.RelocateAction,
        [ActionKind.UninstallBuilding] = BuildingsRules.RelocateAction,
        [ActionKind.Acquisition] = BuildingsRules.AcquireAction,
        [ActionKind.MineAcquisition] = BuildingsRules.AcquireAction,
        [ActionKind.AcquisitionWithdraw] = BuildingsRules.AcquireAction,
        [ActionKind.BuildingTemperature] = BuildingsRules.BuildingTemperatureAction,
        [ActionKind.BedUse] = BuildingsRules.BedUseAction,
        [ActionKind.GrowerCrop] = BuildingsRules.GrowerCropAction,
        [ActionKind.ClaimBuilding] = BuildingsRules.ClaimBuildingAction,
        [ActionKind.AutoRefuel] = BuildingsRules.AutoRefuelAction,
        [ActionKind.Surgery] = Production.SurgeryAction,
        [ActionKind.Area] = Zones.AreaAction,
        [ActionKind.PolicyPrune] = Zones.PolicyPruneAction,
        [ActionKind.RemoveRoof] = Zones.RemoveRoofAction,
        [ActionKind.BuildRoof] = Zones.BuildRoofAction,
        [ActionKind.AreaPlantCut] = Zones.AreaPlantCutAction,
        [ActionKind.ReadingPolicy] = PolicyActions.ReadingPolicyAction,
        [ActionKind.DrugPolicy] = PolicyActions.DrugPolicyAction,
        [ActionKind.FoodPolicy] = PolicyActions.FoodPolicyAction,
        [ActionKind.PawnSettings] = PolicyActions.PawnSettingsAction,
        [ActionKind.AutoHomeArea] = BuildingsRules.AutoHomeAreaAction,
        [ActionKind.Repair] = GiveJobRules.RepairAction,
        [ActionKind.PawnActivities] = a => new Op.Action { Intent = new Op.PawnActivitiesIntent { Batch = a.PawnActivities } },
        [ActionKind.Clean] = GiveJobRules.CleanAction,
        [ActionKind.Tend] = GiveJobRules.TendAction,
        [ActionKind.Equip] = GiveJobRules.EquipAction,
        [ActionKind.DropEquipment] = GiveJobRules.DropEquipmentAction,
        [ActionKind.Rescue] = GiveJobRules.RescueAction,
        [ActionKind.Capture] = GiveJobRules.CaptureAction,
        [ActionKind.GearReplace] = GiveJobRules.WearAction,
        [ActionKind.UseItem] = GiveJobRules.UseItemAction,
        [ActionKind.Strip] = BuildingsRules.StripAction,
        [ActionKind.CombatBatch] = CombatBatchActionRules.CombatBatchAction,
    }.ToFrozenDictionary();

    /// <summary>
    /// Marks every kind that has an
    /// Actions/Apply builder as intent-mode in the domain (<see cref="ActionKindExtensions.IntentMode"/>). The
    /// brain's startup calls it once, before any progress is built; calling it again is harmless.
    /// </summary>
    public static void RegisterIntentKinds()
    {
        foreach (var kind in IntentKinds.Keys)
        {
            ActionKindExtensions.RegisterIntentKind(kind);
        }
    }

    /// <summary>The wire action for an intent-mode domain action, sent under <paramref name="key"/>.</summary>
    /// <exception cref="BridgeContractException">The kind is not intent-mode or the action does not build.</exception>
    public static Op.Action IntentAction(string key, DomainAction action)
    {
        if (!IntentKinds.TryGetValue(action.Kind, out var build))
        {
            throw BridgeContractException.Contract($"{Domain.Wire.Name(action.Kind)} is not an intent-mode kind");
        }
        var wire = build(action);
        wire = wire with { Key = key };
        return wire;
    }

    /// <summary>Points at <paramref name="id"/>; null for an empty id (the field stays absent).</summary>
    public static WireRef? NewRef(string id) => id.Length == 0 ? null : new WireRef { Id = id };

    /// <summary>Points at each of <paramref name="ids"/>.</summary>
    public static IReadOnlyList<WireRef> NewRefs(IEnumerable<string> ids) => [.. ids.Select(id => new WireRef { Id = id })];

    internal static Cell WireCell(DomainCell cell) => new() { X = cell.X, Z = cell.Z };

    internal static IEnumerable<Cell> WireCells(IEnumerable<DomainCell> cells) => cells.Select(WireCell);
}
