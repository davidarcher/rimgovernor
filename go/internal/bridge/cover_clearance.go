package bridge

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"

	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

// CoverClearance is the raider-cover clearance operation half (#581): the
// ClearCover designation on one exact cover thing the defense site census
// identified (DefenseCell.Cover), the same CAS-bound designate/lookup/observe
// shape as CutPlant. The census token is read through ReadDefenseSite over
// the thing's cell.
type CoverClearanceTarget struct {
	Clearance domain.CoverClearance
	Token     string
}
type CoverClearanceRead struct {
	Context *c.ObservationContext
	Targets []CoverClearanceTarget
}
type CoverClearanceAttempt struct {
	Identity   *c.Identity
	Attempt    *c.AttemptKey
	Generation uint64
	Clearance  domain.CoverClearance
}
type CoverClearanceControl struct{ client *Client }

func NewCoverClearanceControl(client *Client) (*CoverClearanceControl, error) {
	if client == nil {
		return nil, contract("cover clearance client missing")
	}
	return &CoverClearanceControl{client}, nil
}

func coverClearanceOperation(target CoverClearanceTarget) *op.Operation {
	cl := target.Clearance
	return &op.Operation{Command: &op.Operation_ClearCover{ClearCover: &op.ClearCover{Target: &op.EntityPrecondition{EntityId: proto.String(cl.Thing()), ExpectedSnapshotToken: proto.String(target.Token)},
		DesignationDef: proto.String(cl.Designation()), Cell: &c.Cell{X: proto.Int32(cl.Cell().X), Z: proto.Int32(cl.Cell().Z)}}}}
}
func validCoverClearance(target CoverClearanceTarget) error {
	if _, err := domain.NewCoverClearance(target.Clearance.Thing(), target.Clearance.Definition(), target.Clearance.Designation(), target.Clearance.Cell()); err != nil {
		return err
	}
	return validID(target.Token)
}
func (client *Client) PreviewCoverClearance(ctx context.Context, identity *c.Identity, target CoverClearanceTarget) (*op.PreviewReply, Result, error) {
	if ValidateIdentity(identity) != nil || validCoverClearance(target) != nil {
		return nil, Result{}, contract("invalid cover clearance preview")
	}
	reply := &op.PreviewReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/operations_preview", &op.PreviewRequest{Identity: proto.Clone(identity).(*c.Identity), Operation: coverClearanceOperation(target)}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown cover clearance preview fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	v := reply.GetEvaluated()
	if v == nil || v.Accepted == nil || !v.GetAccepted() || v.Preparation != nil || buildingContext(v.Context, identity, 0, false) != nil || coverClearanceEffect(v.Projected, target.Clearance, true) != nil {
		return nil, raw, contract("invalid cover clearance preview evidence")
	}
	return reply, raw, nil
}
func (writer *CoverClearanceControl) DesignateCoverClearance(ctx context.Context, pre *a.WritePrecondition, target CoverClearanceTarget) (*op.ExecuteReply, Result, error) {
	if writer == nil || writer.client == nil || pre == nil || buildingUnknown(pre) != nil || ValidateIdentity(pre.Identity) != nil || buildingAttempt(pre.Attempt) != nil || pre.GetExpectedGeneration() == 0 || validCoverClearance(target) != nil {
		return nil, Result{}, contract("invalid cover clearance execution")
	}
	reply := &op.ExecuteReply{}
	raw, err := writer.client.protoCall(ctx, "rimgovernor/operations_execute", &op.ExecuteRequest{Precondition: proto.Clone(pre).(*a.WritePrecondition), Operation: coverClearanceOperation(target)}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown cover clearance execute fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	// The runtime additionally compares full owner/direction against its admission.
	v := reply.GetReceipt()
	if v == nil {
		return nil, raw, contract("cover clearance owner mismatch")
	}
	err = coverClearanceReceipt(v, CoverClearanceAttempt{pre.Identity, pre.Attempt, pre.GetExpectedGeneration(), target.Clearance})
	return reply, raw, err
}
func validCoverClearanceAttempt(w CoverClearanceAttempt) error {
	if ValidateIdentity(w.Identity) != nil || buildingAttempt(w.Attempt) != nil || w.Generation == 0 {
		return contract("invalid cover clearance attempt")
	}
	_, err := domain.NewCoverClearance(w.Clearance.Thing(), w.Clearance.Definition(), w.Clearance.Designation(), w.Clearance.Cell())
	return err
}
func coverClearanceEffect(v *r.EffectEvidence, clearance domain.CoverClearance, allowed bool) error {
	d := v.GetDesignation()
	if d == nil || d.Present == nil || d.GetPresent() != allowed || d.GetThingId() != clearance.Thing() || d.GetResourceDef() != clearance.Definition() || d.GetDesignationDef() != clearance.Designation() || d.Cell == nil || d.Cell.X == nil || d.Cell.Z == nil || d.Cell.GetX() != clearance.Cell().X || d.Cell.GetZ() != clearance.Cell().Z {
		return contract("cover clearance effect mismatch")
	}
	return nil
}
func coverClearanceReceipt(v *r.Receipt, w CoverClearanceAttempt) error {
	if v == nil || buildingUnknown(v) != nil || !proto.Equal(v.Attempt, w.Attempt) || buildingContext(v.AdmittedContext, w.Identity, w.Generation, true) != nil {
		return contract("cover clearance admission mismatch")
	}
	switch out := v.Outcome.(type) {
	case *r.Receipt_Applied:
		return coverClearanceEffect(out.Applied.GetObserved(), w.Clearance, true)
	case *r.Receipt_Uncertain:
		if out.Uncertain == nil {
			return contract("cover clearance uncertainty missing")
		}
		if out.Uncertain.LastObserved != nil {
			return coverClearanceEffect(out.Uncertain.LastObserved, w.Clearance, true)
		}
		return nil
	default:
		return contract("unsupported cover clearance receipt")
	}
}
func (client *Client) LookupCoverClearance(ctx context.Context, w CoverClearanceAttempt) (*r.LookupReply, Result, error) {
	if err := validCoverClearanceAttempt(w); err != nil {
		return nil, Result{}, err
	}
	reply := &r.LookupReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/receipts_lookup", &r.LookupRequest{Identity: w.Identity, Attempt: w.Attempt}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown cover clearance lookup fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	switch v := reply.Outcome.(type) {
	case *r.LookupReply_Receipt:
		err = coverClearanceReceipt(v.Receipt, w)
	case *r.LookupReply_Unknown:
		err = buildingContext(v.Unknown.GetContext(), w.Identity, 0, false)
	case *r.LookupReply_InFlight:
		if v.InFlight == nil || !proto.Equal(v.InFlight.Attempt, w.Attempt) {
			return nil, raw, contract("cover clearance in-flight mismatch")
		}
		err = buildingContext(v.InFlight.AdmittedContext, w.Identity, w.Generation, true)
	default:
		err = contract("cover clearance lookup outcome missing")
	}
	return reply, raw, err
}
func (client *Client) ObserveCoverClearance(ctx context.Context, w CoverClearanceAttempt, admitted *r.Receipt) (*r.ProgressReply, Result, error) {
	if validCoverClearanceAttempt(w) != nil || coverClearanceReceipt(admitted, w) != nil {
		return nil, Result{}, contract("cover clearance observation admission mismatch")
	}
	reply := &r.ProgressReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/receipts_observe_progress", &r.ProgressRequest{Identity: w.Identity, Attempt: w.Attempt}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown cover clearance progress fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	v := reply.GetProgress()
	if v == nil || !proto.Equal(v.Attempt, w.Attempt) || buildingContext(v.Context, w.Identity, 0, false) != nil || v.Context.GetTick() < admitted.AdmittedContext.GetTick() {
		return nil, raw, contract("cover clearance progress scope mismatch")
	}
	switch out := v.Effect.(type) {
	case *r.Progress_Unknown:
		if out.Unknown == nil {
			return nil, raw, contract("missing cover clearance uncertainty")
		}
	case *r.Progress_Completed:
		if !v.GetCompleteInspection() {
			return nil, raw, contract("incomplete cover clearance completion")
		}
		err = coverClearanceEffect(out.Completed.GetEvidence(), w.Clearance, true)
	case *r.Progress_Unsuccessful:
		if !v.GetCompleteInspection() || out.Unsuccessful.GetReason() != r.UnsuccessfulReason_UNSUCCESSFUL_REASON_OUTCOME_NOT_ACHIEVED {
			return nil, raw, contract("unverified cover clearance failure")
		}
		err = coverClearanceEffect(out.Unsuccessful.GetEvidence(), w.Clearance, false)
	case *r.Progress_Pending:
		// The thing still stands with its designation: the removal is pending
		// ordinary work.
		if !v.GetCompleteInspection() {
			return nil, raw, contract("incomplete cover clearance pending")
		}
		err = coverClearanceEffect(out.Pending.GetEvidence(), w.Clearance, true)
	default:
		err = contract("unsupported cover clearance progress")
	}
	return reply, raw, err
}
