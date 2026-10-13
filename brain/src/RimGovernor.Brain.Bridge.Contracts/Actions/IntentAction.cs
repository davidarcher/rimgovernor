using System.Collections.Frozen;
using DomainCell = RimGovernor.Brain.Domain.Cell;
using Op = RimGovernor.Contracts.Wire.Operations;
using WireRef = RimGovernor.Contracts.Wire.Common.Ref;

namespace RimGovernor.Brain.Bridge.Contracts;

// Go actions.go: the intent-kind registry, IntentAction and the wire helpers every builder shares. The builders
// themselves sit in the sibling files of this folder, one group per Go file family. The Actions/Apply writer
// (ActionsWriter.Apply) is wire plumbing and is not part of this file.
public static partial class BridgeFunctions
{
    /// <summary>
    /// Go's <c>ActionsApplyMethod</c>: a batch of idempotent intents that native applies in order, each validated
    /// against live state and applied or refused on its own. A resent key returns its first result.
    /// </summary>
    public const string ActionsApplyMethod = "rimgovernor/operations_apply";

    // Go's intentKinds: the Actions/Apply builder of every intent-mode kind. Adding a kind: a new Action oneof arm in
    // operations.proto, a native IActionHandler for it in NativeActionDispatch.cs, and a builder listed here.
    private static readonly FrozenDictionary<ActionKind, Func<DomainAction, Op.Action>> IntentKinds = new Dictionary<ActionKind, Func<DomainAction, Op.Action>>
    {
        [ActionKind.Trade] = TradeAction,
        [ActionKind.Haul] = HaulAction,
        [ActionKind.OwnedDraft] = DraftAction,
        [ActionKind.Subdue] = SubdueAction,
        [ActionKind.Building] = BuildingAction,
        [ActionKind.Movement] = MovementAction,
        [ActionKind.ApparelPolicy] = ApparelPolicyAction,
        [ActionKind.ResearchSelect] = ResearchAction,
        [ActionKind.DialogAnswer] = DialogAction,
        [ActionKind.PrisonerInteraction] = PrisonerInteractionAction,
        [ActionKind.QuestAccept] = QuestAcceptAction,
        [ActionKind.QuestShuttle] = QuestShuttleAction,
        [ActionKind.HackDesignation] = HackDesignationAction,
        [ActionKind.GiveItem] = GiveItemAction,
        [ActionKind.Ritual] = RitualAction,
        [ActionKind.IdeoligionReform] = IdeoligionReformAction,
        [ActionKind.Ability] = AbilityAction,
        [ActionKind.Ignite] = IgniteAction,
        [ActionKind.Gathering] = GatheringAction,
        [ActionKind.RemoveProductionBill] = RemoveProductionBillAction,
        [ActionKind.DoorControl] = DoorControlAction,
        [ActionKind.CaravanDeparture] = CaravanDepartureAction,
        [ActionKind.Assign] = AssignAction,
        [ActionKind.WorkAssignment] = WorkSettingsAction,
        [ActionKind.ProductionBill] = ProductionBillAction,
        [ActionKind.Husbandry] = HusbandryAction,
        [ActionKind.ZoneCreate] = ZoneCreateAction,
        [ActionKind.ZoneDelete] = ZoneDeleteAction,
        [ActionKind.ZoneCellEdit] = ZoneCellsAction,
        [ActionKind.StockpilePatch] = StockpileAction,
        [ActionKind.FoundationRemoval] = FoundationRemovalAction,
        [ActionKind.FloorRemoval] = FloorRemovalAction,
        [ActionKind.CoverClearance] = CoverAction,
        [ActionKind.WastepackHaul] = WastepackHaulAction,
        [ActionKind.CutPlant] = CutPlantAction,
        [ActionKind.SupplyAllow] = SupplyAction,
        [ActionKind.SupplyForbid] = SupplyAction,
        [ActionKind.Deconstruction] = DeconstructAction,
        [ActionKind.Excavation] = ExcavateAction,
        [ActionKind.WallRemoval] = WallRemovalAction,
        [ActionKind.RecoveryService] = RecoverAction,
        [ActionKind.MoveBuilding] = RelocateAction,
        [ActionKind.UninstallBuilding] = RelocateAction,
        [ActionKind.Acquisition] = AcquireAction,
        [ActionKind.MineAcquisition] = AcquireAction,
        [ActionKind.AcquisitionWithdraw] = AcquireAction,
        [ActionKind.BuildingTemperature] = BuildingTemperatureAction,
        [ActionKind.BedUse] = BedUseAction,
        [ActionKind.GrowerCrop] = GrowerCropAction,
        [ActionKind.ClaimBuilding] = ClaimBuildingAction,
        [ActionKind.AutoRefuel] = AutoRefuelAction,
        [ActionKind.Surgery] = SurgeryAction,
        [ActionKind.Area] = AreaAction,
        [ActionKind.PolicyPrune] = PolicyPruneAction,
        [ActionKind.RemoveRoof] = RemoveRoofAction,
        [ActionKind.BuildRoof] = BuildRoofAction,
        [ActionKind.AreaPlantCut] = AreaPlantCutAction,
        [ActionKind.ReadingPolicy] = ReadingPolicyAction,
        [ActionKind.DrugPolicy] = DrugPolicyAction,
        [ActionKind.FoodPolicy] = FoodPolicyAction,
        [ActionKind.PawnSettings] = PawnSettingsAction,
        [ActionKind.AutoHomeArea] = AutoHomeAreaAction,
        [ActionKind.Repair] = RepairAction,
        [ActionKind.Clean] = CleanAction,
        [ActionKind.OpenCasket] = OpenCasketAction,
        [ActionKind.Tend] = TendAction,
        [ActionKind.Equip] = EquipAction,
        [ActionKind.DropEquipment] = DropEquipmentAction,
        [ActionKind.Rescue] = RescueAction,
        [ActionKind.Capture] = CaptureAction,
        [ActionKind.MoodRelief] = MoodReliefAction,
        [ActionKind.GearReplace] = WearAction,
        [ActionKind.UseItem] = UseItemAction,
        [ActionKind.Strip] = StripAction,
        [ActionKind.RulesAttach] = RulesAttachAction,
    }.ToFrozenDictionary();

    /// <summary>
    /// Go's <c>registerIntentKind</c> calls from <c>init</c>, made explicit: marks every kind that has an
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

    /// <summary>Go's <c>IntentAction</c>: the wire action for an intent-mode domain action, sent under <paramref name="key"/>.</summary>
    /// <exception cref="BridgeContractException">The kind is not intent-mode, the key is invalid, or the action does not build.</exception>
    public static Op.Action IntentAction(string key, DomainAction action)
    {
        if (!IntentKinds.TryGetValue(action.Kind, out var build))
        {
            throw Contract($"{Domain.Wire.Name(action.Kind)} is not an intent-mode kind");
        }
        if (!IsValidId(key))
        {
            throw Contract("intent key invalid");
        }
        var wire = build(action);
        wire = wire with { Key = key };
        return wire;
    }

    /// <summary>Go's <c>NewRef</c>: points at <paramref name="id"/>; null for an empty id (the field stays absent).</summary>
    public static WireRef? NewRef(string id) => id.Length == 0 ? null : new WireRef { Id = id };

    /// <summary>Go's <c>NewRefs</c>: points at each of <paramref name="ids"/>.</summary>
    public static IReadOnlyList<WireRef> NewRefs(IEnumerable<string> ids) => [.. ids.Select(id => new WireRef { Id = id })];

    private static Cell WireCell(DomainCell cell) => new() { X = cell.X, Z = cell.Z };

    private static IEnumerable<Cell> WireCells(IEnumerable<DomainCell> cells) => cells.Select(WireCell);
}
