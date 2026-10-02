package bridge

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/domain"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

// ActionsApplyMethod is Actions/Apply (#856): a batch of idempotent intents
// that native applies in order, each validated against live state and
// applied or refused on its own. A resent key returns its first result.
const ActionsApplyMethod = "rimgovernor/operations_apply"

// intentKinds holds the Actions/Apply builder of every intent-mode kind. A
// kind registers here, from init, and that registration is what makes
// domain.ActionKind.IntentMode true for it. Adding a kind: a new Action
// oneof arm in operations.proto, a native IActionHandler for it in
// NativeActionDispatch.cs, and a builder registered below.
var intentKinds = map[domain.ActionKind]func(domain.Action) (*o.Action, error){}

func registerIntentKind(kind domain.ActionKind, build func(domain.Action) (*o.Action, error)) {
	intentKinds[kind] = build
	domain.RegisterIntentKind(kind)
}

func init() {
	registerIntentKind(domain.TradeAction, tradeAction)
	registerIntentKind(domain.HaulAction, haulAction)
	registerIntentKind(domain.OwnedDraftAction, draftAction)
	registerIntentKind(domain.SubdueAction, subdueAction)
	registerIntentKind(domain.BuildingAction, buildingAction)
	registerIntentKind(domain.MovementAction, movementAction)
	registerIntentKind(domain.ApparelPolicyAction, apparelPolicyAction)
	registerIntentKind(domain.ResearchSelectAction, researchAction)
	registerIntentKind(domain.NamingConfirmationAction, namingAction)
	registerIntentKind(domain.DialogAnswerAction, dialogAction)
	registerIntentKind(domain.PrisonerInteractionAction, prisonerInteractionAction)
	registerIntentKind(domain.QuestAcceptAction, questAcceptAction)
	registerIntentKind(domain.CaravanDepartureAction, caravanDepartureAction)
	registerIntentKind(domain.BedAssignAction, bedAssignAction)
	registerIntentKind(domain.WorkAssignmentAction, workSettingsAction)
	registerIntentKind(domain.ProductionBillAction, productionBillAction)
	registerIntentKind(domain.HusbandryAction, husbandryAction)
	registerIntentKind(domain.ZoneCreateAction, zoneCreateAction)
	registerIntentKind(domain.ZoneDeleteAction, zoneDeleteAction)
	registerIntentKind(domain.ZoneCellEditAction, zoneCellsAction)
	registerIntentKind(domain.StockpilePatchAction, stockpileAction)
	registerIntentKind(domain.FoundationRemovalAction, foundationRemovalAction)
	registerIntentKind(domain.FloorRemovalAction, floorRemovalAction)
	registerIntentKind(domain.CoverClearanceAction, coverAction)
	registerIntentKind(domain.CutPlantAction, cutPlantAction)
	registerIntentKind(domain.SupplyAllowAction, supplyAction)
	registerIntentKind(domain.SupplyForbidAction, supplyAction)
	registerIntentKind(domain.DeconstructionAction, deconstructAction)
	registerIntentKind(domain.ExcavationAction, excavateAction)
	registerIntentKind(domain.WasteAction, wasteAction)
	registerIntentKind(domain.WallRemovalAction, wallRemovalAction)
	registerIntentKind(domain.RecoveryServiceAction, recoverAction)
	registerIntentKind(domain.MoveBuildingAction, relocateAction)
	registerIntentKind(domain.UninstallBuildingAction, relocateAction)
	registerIntentKind(domain.AcquisitionAction, acquireAction)
	registerIntentKind(domain.MineAcquisitionAction, acquireAction)
	registerIntentKind(domain.AcquisitionWithdrawAction, acquireAction)
	registerIntentKind(domain.BuildingTemperatureAction, buildingTemperatureAction)
	registerIntentKind(domain.BedUseAction, bedUseAction)
	registerIntentKind(domain.GrowerCropAction, growerCropAction)
	registerIntentKind(domain.ClaimBuildingAction, claimBuildingAction)
	registerIntentKind(domain.AutoRefuelAction, autoRefuelAction)
	registerIntentKind(domain.SurgeryAction, surgeryAction)
	registerIntentKind(domain.AreaAction, areaAction)
	registerIntentKind(domain.PolicyPruneAction, policyPruneAction)
	registerIntentKind(domain.RemoveRoofAction, removeRoofAction)
	registerIntentKind(domain.ReadingPolicyAction, readingPolicyAction)
	registerIntentKind(domain.DrugPolicyAction, drugPolicyAction)
	registerIntentKind(domain.PawnSettingsAction, pawnSettingsAction)
	registerIntentKind(domain.AutoHomeAreaAction, autoHomeAreaAction)
	registerIntentKind(domain.RepairAction, repairAction)
	registerIntentKind(domain.CleanAction, cleanAction)
	registerIntentKind(domain.OpenCasketAction, openCasketAction)
	registerIntentKind(domain.TendAction, tendAction)
	registerIntentKind(domain.EquipAction, equipAction)
	registerIntentKind(domain.RescueAction, rescueAction)
	registerIntentKind(domain.CaptureAction, captureAction)
	registerIntentKind(domain.MoodReliefAction, moodReliefAction)
	registerIntentKind(domain.GearReplaceAction, wearAction)
	registerIntentKind(domain.UseItemAction, useItemAction)
	registerIntentKind(domain.StripAction, stripAction)
}

// movementAction is the Actions/Apply move arm of one domain movement.
func movementAction(action domain.Action) (*o.Action, error) {
	m, ok := action.Movement()
	if !ok {
		return nil, contract("not a movement action")
	}
	if err := validID(string(m.Pawn())); err != nil {
		return nil, err
	}
	cell := &c.Cell{X: proto.Int32(m.Destination().X), Z: proto.Int32(m.Destination().Z)}
	if err := movementCell(cell); err != nil {
		return nil, err
	}
	return &o.Action{Intent: &o.Action_Move{Move: &o.MoveIntent{PawnId: proto.String(string(m.Pawn())), Destination: cell}}}, nil
}

// foundationRemovalAction is the Actions/Apply remove_foundation arm of one
// foundation_removal (#954).
func foundationRemovalAction(action domain.Action) (*o.Action, error) {
	f, ok := action.FoundationRemoval()
	if !ok {
		return nil, contract("not a foundation removal action")
	}
	if err := validID(f.Definition()); err != nil {
		return nil, err
	}
	return &o.Action{Intent: &o.Action_RemoveFoundation{RemoveFoundation: &o.RemoveFoundationIntent{
		Cell: &c.Cell{X: proto.Int32(f.Cell().X), Z: proto.Int32(f.Cell().Z)}, DefName: proto.String(f.Definition())}}}, nil
}

// floorRemovalAction is the Actions/Apply remove_floor arm of one
// floor_removal (epic #1249).
func floorRemovalAction(action domain.Action) (*o.Action, error) {
	f, ok := action.FloorRemoval()
	if !ok {
		return nil, contract("not a floor removal action")
	}
	if err := validID(f.Definition()); err != nil {
		return nil, err
	}
	return &o.Action{Intent: &o.Action_RemoveFloor{RemoveFloor: &o.RemoveFloorIntent{
		Cell: &c.Cell{X: proto.Int32(f.Cell().X), Z: proto.Int32(f.Cell().Z)}, DefName: proto.String(f.Definition())}}}, nil
}

func movementCell(cell *c.Cell) error {
	if cell == nil || cell.X == nil || cell.Z == nil || cell.GetX() < 0 || cell.GetZ() < 0 {
		return contract("movement destination missing or invalid")
	}
	return buildingUnknown(cell)
}

// IntentAction is the wire action for an intent-mode domain action, sent
// under key.
func IntentAction(key string, action domain.Action) (*o.Action, error) {
	build, ok := intentKinds[action.Kind()]
	if !ok {
		return nil, contract("%s is not an intent-mode kind", action.Kind())
	}
	if validID(key) != nil {
		return nil, contract("intent key invalid")
	}
	wire, err := build(action)
	if err != nil {
		return nil, err
	}
	wire.Key = proto.String(key)
	return wire, nil
}

// ActionsWriter sends intent batches.
type ActionsWriter struct{ client *Client }

func NewActionsWriter(client *Client) (*ActionsWriter, error) {
	if client == nil {
		return nil, contract("actions client missing")
	}
	return &ActionsWriter{client}, nil
}

// Apply sends actions and checks the reply carries one result per action,
// in order, under the same key. A batch_failure is returned as a
// *NativeFailure: nothing applied.
func (writer *ActionsWriter) Apply(ctx context.Context, identity *c.Identity, actions []*o.Action) (*o.ApplyReply, Result, error) {
	if writer == nil || writer.client == nil || ValidateIdentity(identity) != nil || len(actions) == 0 {
		return nil, Result{}, contract("invalid actions batch")
	}
	seen := map[string]bool{}
	for _, action := range actions {
		if action == nil || validID(action.GetKey()) != nil || seen[action.GetKey()] || action.GetIntent() == nil {
			return nil, Result{}, contract("action requires a unique key and an intent")
		}
		seen[action.GetKey()] = true
	}
	request := &o.ApplyRequest{Identity: proto.Clone(identity).(*c.Identity), Actions: stampPurpose(ctx, actions)}
	if DeferredSnapshot(ctx) {
		request.DeferSnapshot = proto.Bool(true)
	}
	reply := &o.ApplyReply{}
	raw, err := writer.client.protoCall(ctx, ActionsApplyMethod, request, reply)
	if err != nil {
		return nil, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return reply, raw, err
	}
	if reply.BatchFailure != nil {
		return reply, raw, failure(reply.BatchFailure, raw)
	}
	if len(reply.Results) != len(actions) {
		return reply, raw, contract("actions reply result count mismatch")
	}
	for i, result := range reply.Results {
		if result.GetKey() != actions[i].GetKey() {
			return reply, raw, contract("actions reply key mismatch")
		}
		switch v := result.Outcome.(type) {
		case *o.ActionResult_Applied:
			if err := actionReceipt(v.Applied, identity); err != nil {
				return reply, raw, err
			}
		case *o.ActionResult_Refused:
			if v.Refused == nil || !diagnostic(v.Refused.Reason) {
				return reply, raw, contract("action refusal invalid")
			}
		case *o.ActionResult_Failed:
			if v.Failed == nil || v.Failed.Code == nil {
				return reply, raw, contract("action failure invalid")
			}
		default:
			return reply, raw, contract("action outcome missing")
		}
	}
	return reply, raw, nil
}

func actionReceipt(v *r.Receipt, identity *c.Identity) error {
	if v == nil {
		return contract("action receipt missing")
	}
	if err := buildingContext(v.AdmittedContext, identity, 0, false); err != nil {
		return err
	}
	switch outcome := v.Outcome.(type) {
	case *r.Receipt_Applied:
		if outcome.Applied == nil || outcome.Applied.Observed == nil {
			return contract("action applied evidence missing")
		}
	case *r.Receipt_Uncertain:
		if outcome.Uncertain == nil {
			return contract("action uncertainty missing")
		}
	default:
		return contract("unsupported action receipt")
	}
	return nil
}
