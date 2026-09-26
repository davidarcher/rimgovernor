package bridge

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	p "github.com/davidarcher/RimGovernor/go/internal/wire/placementpb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

// MoveBuilding is the move family's operation half (#808): InstallBuilding
// on one exact installed building places the game's reinstall blueprint at
// the destination (GenConstruct.PlaceBlueprintForReinstall, the Reinstall
// gizmo's own write); ordinary construction work uninstalls and installs
// the piece. The InstallationEffect names the same inner building, its
// definition, the destination and the stage: QUEUED while the blueprint
// stands, INSTALLED once the building stands at the destination. Native
// re-evaluates every rule at apply, so no snapshot token is sent.
type MoveBuildingAttempt struct {
	Identity   *c.Identity
	Attempt    *c.AttemptKey
	Generation uint64
	Move       domain.MoveBuilding
}
type MoveBuildingControl struct{ client *Client }

func NewMoveBuildingControl(client *Client) (*MoveBuildingControl, error) {
	if client == nil {
		return nil, contract("move building client missing")
	}
	return &MoveBuildingControl{client}, nil
}

var moveRotations = map[domain.Rotation]p.Rotation{domain.North: p.Rotation_ROTATION_NORTH, domain.East: p.Rotation_ROTATION_EAST, domain.South: p.Rotation_ROTATION_SOUTH, domain.West: p.Rotation_ROTATION_WEST}

func validMove(m domain.MoveBuilding) error {
	_, err := domain.NewMoveBuilding(m.Thing(), m.Definition(), m.Cell(), m.Rotation())
	return err
}
func moveBuildingOperation(m domain.MoveBuilding) *op.Operation {
	return &op.Operation{Command: &op.Operation_InstallBuilding{InstallBuilding: &op.InstallBuilding{
		PackedOrInner: &op.EntityPrecondition{EntityId: proto.String(m.Thing())},
		Destination:   &c.Cell{X: proto.Int32(m.Cell().X), Z: proto.Int32(m.Cell().Z)},
		Rotation:      moveRotations[m.Rotation()].Enum()}}}
}

// moveBuildingEffect checks the installation evidence names this exact move;
// stage is the one the reply's position requires (0 = any stage but INSTALLED).
func moveBuildingEffect(v *r.EffectEvidence, m domain.MoveBuilding, stage r.InstallationStage) error {
	e := v.GetInstallation()
	if e == nil || e.GetInnerThingId() != m.Thing() || e.GetDefName() != m.Definition() || e.Cell == nil || e.Cell.X == nil || e.Cell.Z == nil ||
		e.Cell.GetX() != m.Cell().X || e.Cell.GetZ() != m.Cell().Z || e.Rotation == nil || e.GetRotation() != moveRotations[m.Rotation()] || e.Stage == nil {
		return contract("move building effect mismatch")
	}
	if stage == 0 {
		if e.GetStage() == r.InstallationStage_INSTALLATION_STAGE_INSTALLED {
			return contract("move building failure reports installation")
		}
		return nil
	}
	if e.GetStage() != stage {
		return contract("move building stage mismatch")
	}
	if stage == r.InstallationStage_INSTALLATION_STAGE_QUEUED && validID(e.GetBlueprintId()) != nil {
		return contract("queued move building lacks its blueprint")
	}
	return nil
}

func (client *Client) PreviewMoveBuilding(ctx context.Context, identity *c.Identity, m domain.MoveBuilding) (*op.PreviewReply, Result, error) {
	if ValidateIdentity(identity) != nil || validMove(m) != nil {
		return nil, Result{}, contract("invalid move building preview")
	}
	reply := &op.PreviewReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/operations_preview", &op.PreviewRequest{Identity: proto.Clone(identity).(*c.Identity), Operation: moveBuildingOperation(m)}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown move building preview fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	v := reply.GetEvaluated()
	if v == nil || v.Accepted == nil || !v.GetAccepted() || v.Preparation != nil || buildingContext(v.Context, identity, 0, false) != nil || moveBuildingEffect(v.Projected, m, r.InstallationStage_INSTALLATION_STAGE_PLACEABLE) != nil {
		return nil, raw, contract("invalid move building preview evidence")
	}
	return reply, raw, nil
}

func (writer *MoveBuildingControl) ApplyMoveBuilding(ctx context.Context, pre *a.WritePrecondition, m domain.MoveBuilding) (*op.ExecuteReply, Result, error) {
	if writer == nil || writer.client == nil || pre == nil || buildingUnknown(pre) != nil || ValidateIdentity(pre.Identity) != nil || buildingAttempt(pre.Attempt) != nil || pre.GetExpectedGeneration() == 0 || validMove(m) != nil {
		return nil, Result{}, contract("invalid move building execution")
	}
	reply := &op.ExecuteReply{}
	raw, err := writer.client.protoCall(ctx, "rimgovernor/operations_execute", &op.ExecuteRequest{Precondition: proto.Clone(pre).(*a.WritePrecondition), Operation: moveBuildingOperation(m)}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown move building execute fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	v := reply.GetReceipt()
	if v == nil {
		return nil, raw, contract("move building receipt missing")
	}
	err = moveBuildingReceipt(v, MoveBuildingAttempt{pre.Identity, pre.Attempt, pre.GetExpectedGeneration(), m})
	return reply, raw, err
}

func validMoveBuildingAttempt(w MoveBuildingAttempt) error {
	if ValidateIdentity(w.Identity) != nil || buildingAttempt(w.Attempt) != nil || w.Generation == 0 {
		return contract("invalid move building attempt")
	}
	return validMove(w.Move)
}
func moveBuildingReceipt(v *r.Receipt, w MoveBuildingAttempt) error {
	if v == nil || buildingUnknown(v) != nil || !proto.Equal(v.Attempt, w.Attempt) || buildingContext(v.AdmittedContext, w.Identity, w.Generation, true) != nil {
		return contract("move building admission mismatch")
	}
	switch out := v.Outcome.(type) {
	case *r.Receipt_Applied:
		return moveBuildingEffect(out.Applied.GetObserved(), w.Move, r.InstallationStage_INSTALLATION_STAGE_QUEUED)
	case *r.Receipt_Uncertain:
		if out.Uncertain == nil {
			return contract("move building uncertainty missing")
		}
		if out.Uncertain.LastObserved != nil {
			return moveBuildingEffect(out.Uncertain.LastObserved, w.Move, r.InstallationStage_INSTALLATION_STAGE_QUEUED)
		}
		return nil
	default:
		return contract("unsupported move building receipt")
	}
}

func (client *Client) LookupMoveBuilding(ctx context.Context, w MoveBuildingAttempt) (*r.LookupReply, Result, error) {
	if err := validMoveBuildingAttempt(w); err != nil {
		return nil, Result{}, err
	}
	reply := &r.LookupReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/receipts_lookup", &r.LookupRequest{Identity: w.Identity, Attempt: w.Attempt}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown move building lookup fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	switch v := reply.Outcome.(type) {
	case *r.LookupReply_Receipt:
		err = moveBuildingReceipt(v.Receipt, w)
	case *r.LookupReply_Unknown:
		err = buildingContext(v.Unknown.GetContext(), w.Identity, 0, false)
	case *r.LookupReply_InFlight:
		if v.InFlight == nil || !proto.Equal(v.InFlight.Attempt, w.Attempt) {
			return nil, raw, contract("move building in-flight mismatch")
		}
		err = buildingContext(v.InFlight.AdmittedContext, w.Identity, w.Generation, true)
	default:
		err = contract("move building lookup outcome missing")
	}
	return reply, raw, err
}

func (client *Client) ObserveMoveBuilding(ctx context.Context, w MoveBuildingAttempt, admitted *r.Receipt) (*r.ProgressReply, Result, error) {
	if validMoveBuildingAttempt(w) != nil || moveBuildingReceipt(admitted, w) != nil {
		return nil, Result{}, contract("move building observation admission mismatch")
	}
	reply := &r.ProgressReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/receipts_observe_progress", &r.ProgressRequest{Identity: w.Identity, Attempt: w.Attempt}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown move building progress fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	v := reply.GetProgress()
	if v == nil || !proto.Equal(v.Attempt, w.Attempt) || buildingContext(v.Context, w.Identity, 0, false) != nil || v.Context.GetTick() < admitted.AdmittedContext.GetTick() {
		return nil, raw, contract("move building progress scope mismatch")
	}
	switch out := v.Effect.(type) {
	case *r.Progress_Unknown:
		if out.Unknown == nil {
			return nil, raw, contract("missing move building uncertainty")
		}
	case *r.Progress_Completed:
		if !v.GetCompleteInspection() {
			return nil, raw, contract("incomplete move building completion")
		}
		err = moveBuildingEffect(out.Completed.GetEvidence(), w.Move, r.InstallationStage_INSTALLATION_STAGE_INSTALLED)
	case *r.Progress_Unsuccessful:
		if !v.GetCompleteInspection() || out.Unsuccessful.GetReason() != r.UnsuccessfulReason_UNSUCCESSFUL_REASON_OUTCOME_NOT_ACHIEVED {
			return nil, raw, contract("unverified move building failure")
		}
		err = moveBuildingEffect(out.Unsuccessful.GetEvidence(), w.Move, 0)
	case *r.Progress_Pending:
		// The reinstall blueprint stands: the move waits on ordinary
		// construction work, which waits on the piece's reservation.
		if !v.GetCompleteInspection() {
			return nil, raw, contract("incomplete move building pending")
		}
		err = moveBuildingEffect(out.Pending.GetEvidence(), w.Move, r.InstallationStage_INSTALLATION_STAGE_QUEUED)
	default:
		err = contract("unsupported move building progress")
	}
	return reply, raw, err
}
