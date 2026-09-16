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

// ExcavationTarget is one rock cell selected for staged excavation together
// with the per-cell CAS token ReadExcavationSite observed.
type ExcavationTarget struct {
	Excavation domain.Excavation
	Token      string
}
type ExcavationAttempt struct {
	Identity   *c.Identity
	Attempt    *c.AttemptKey
	Generation uint64
	Excavation domain.Excavation
}
type ExcavationControl struct{ client *Client }

func NewExcavationControl(client *Client) (*ExcavationControl, error) {
	if client == nil {
		return nil, contract("excavation client missing")
	}
	return &ExcavationControl{client}, nil
}
func excavationOperation(target ExcavationTarget) *op.Operation {
	cell := target.Excavation.Cell()
	return &op.Operation{Command: &op.Operation_ExcavateCell{ExcavateCell: &op.ExcavateCell{Cell: &c.Cell{X: proto.Int32(cell.X), Z: proto.Int32(cell.Z)}, ExpectedMineableDefName: proto.String(target.Excavation.Definition()), ExpectedSnapshotToken: proto.String(target.Token)}}}
}
func validExcavation(target ExcavationTarget) error {
	if _, err := domain.NewExcavation(target.Excavation.Cell(), target.Excavation.Definition()); err != nil {
		return err
	}
	return validID(target.Token)
}
func (client *Client) PreviewExcavation(ctx context.Context, identity *c.Identity, target ExcavationTarget) (*op.PreviewReply, Result, error) {
	if ValidateIdentity(identity) != nil || validExcavation(target) != nil {
		return nil, Result{}, contract("invalid excavation preview")
	}
	reply := &op.PreviewReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/operations_preview", &op.PreviewRequest{Identity: proto.Clone(identity).(*c.Identity), Operation: excavationOperation(target)}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown excavation preview fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	v := reply.GetEvaluated()
	if v == nil || v.Accepted == nil || !v.GetAccepted() || v.Preparation != nil || buildingContext(v.Context, identity, 0, false) != nil || v.Projected != nil {
		return nil, raw, contract("invalid excavation preview evidence")
	}
	return reply, raw, nil
}
func (writer *ExcavationControl) Excavate(ctx context.Context, pre *a.WritePrecondition, target ExcavationTarget) (*op.ExecuteReply, Result, error) {
	if writer == nil || writer.client == nil || pre == nil || buildingUnknown(pre) != nil || ValidateIdentity(pre.Identity) != nil || buildingAttempt(pre.Attempt) != nil || pre.GetExpectedGeneration() == 0 || validExcavation(target) != nil {
		return nil, Result{}, contract("invalid excavation execution")
	}
	reply := &op.ExecuteReply{}
	raw, err := writer.client.protoCall(ctx, "rimgovernor/operations_execute", &op.ExecuteRequest{Precondition: proto.Clone(pre).(*a.WritePrecondition), Operation: excavationOperation(target)}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown excavation execute fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	err = excavationReceipt(reply.GetReceipt(), ExcavationAttempt{pre.Identity, pre.Attempt, pre.GetExpectedGeneration(), target.Excavation})
	return reply, raw, err
}
func validExcavationAttempt(w ExcavationAttempt) error {
	if ValidateIdentity(w.Identity) != nil || buildingAttempt(w.Attempt) != nil || w.Generation == 0 {
		return contract("invalid excavation attempt")
	}
	_, err := domain.NewExcavation(w.Excavation.Cell(), w.Excavation.Definition())
	return err
}

// ValidateExcavationEffect checks the effect names exactly the admitted cell
// and rock definition and that cleared/designated/cancelled are mutually
// consistent: a cleared cell holds no rock, so it can be neither designated
// nor cancelled.
func ValidateExcavationEffect(v *r.EffectEvidence, excavation domain.Excavation) error {
	d := v.GetExcavation()
	if d == nil || buildingUnknown(v) != nil || d.GetMineableDefName() != excavation.Definition() || d.Cell == nil || d.Cell.X == nil || d.Cell.Z == nil || d.Cell.GetX() != excavation.Cell().X || d.Cell.GetZ() != excavation.Cell().Z || d.Designated == nil || d.AdoptedExistingDesignation == nil || d.Cleared == nil || d.Cancelled == nil || len(d.GetBlocker()) > 256 {
		return contract("excavation effect mismatch")
	}
	if d.GetCleared() && (d.GetDesignated() || d.GetCancelled()) || d.GetDesignated() && d.GetCancelled() {
		return contract("inconsistent excavation effect")
	}
	return nil
}
func excavationReceipt(v *r.Receipt, w ExcavationAttempt) error {
	if v == nil || buildingUnknown(v) != nil || !proto.Equal(v.Attempt, w.Attempt) || buildingContext(v.AdmittedContext, w.Identity, w.Generation, true) != nil {
		return contract("excavation admission mismatch")
	}
	switch out := v.Outcome.(type) {
	case *r.Receipt_Applied:
		if err := ValidateExcavationEffect(out.Applied.GetObserved(), w.Excavation); err != nil {
			return err
		}
		if !out.Applied.GetObserved().GetExcavation().GetDesignated() {
			return contract("excavation designation missing")
		}
		return nil
	case *r.Receipt_Uncertain:
		if out.Uncertain == nil {
			return contract("excavation uncertainty missing")
		}
		if out.Uncertain.LastObserved != nil {
			return ValidateExcavationEffect(out.Uncertain.LastObserved, w.Excavation)
		}
		return nil
	default:
		return contract("unsupported excavation receipt")
	}
}
func (client *Client) LookupExcavation(ctx context.Context, w ExcavationAttempt) (*r.LookupReply, Result, error) {
	if err := validExcavationAttempt(w); err != nil {
		return nil, Result{}, err
	}
	reply := &r.LookupReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/receipts_lookup", &r.LookupRequest{Identity: w.Identity, Attempt: w.Attempt}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown excavation lookup fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	switch v := reply.Outcome.(type) {
	case *r.LookupReply_Receipt:
		err = excavationReceipt(v.Receipt, w)
	case *r.LookupReply_Unknown:
		err = buildingContext(v.Unknown.GetContext(), w.Identity, 0, false)
	case *r.LookupReply_InFlight:
		if v.InFlight == nil || !proto.Equal(v.InFlight.Attempt, w.Attempt) {
			return nil, raw, contract("excavation in-flight mismatch")
		}
		err = buildingContext(v.InFlight.AdmittedContext, w.Identity, w.Generation, true)
	default:
		err = contract("excavation lookup outcome missing")
	}
	return reply, raw, err
}
func (client *Client) ObserveExcavation(ctx context.Context, w ExcavationAttempt, admitted *r.Receipt) (*r.ProgressReply, Result, error) {
	if validExcavationAttempt(w) != nil || excavationReceipt(admitted, w) != nil {
		return nil, Result{}, contract("excavation observation admission mismatch")
	}
	reply := &r.ProgressReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/receipts_observe_progress", &r.ProgressRequest{Identity: w.Identity, Attempt: w.Attempt}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown excavation progress fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	v := reply.GetProgress()
	if v == nil || !proto.Equal(v.Attempt, w.Attempt) || buildingContext(v.Context, w.Identity, 0, false) != nil || v.Context.GetTick() < admitted.AdmittedContext.GetTick() {
		return nil, raw, contract("excavation progress scope mismatch")
	}
	switch out := v.Effect.(type) {
	case *r.Progress_Unknown:
		if out.Unknown == nil {
			return nil, raw, contract("missing excavation uncertainty")
		}
	case *r.Progress_Completed:
		if !v.GetCompleteInspection() {
			return nil, raw, contract("incomplete excavation completion")
		}
		err = ValidateExcavationEffect(out.Completed.GetEvidence(), w.Excavation)
		if !out.Completed.GetEvidence().GetExcavation().GetCleared() {
			return nil, raw, contract("unverified excavation completion")
		}
	case *r.Progress_Unsuccessful:
		if !v.GetCompleteInspection() || out.Unsuccessful.GetReason() != r.UnsuccessfulReason_UNSUCCESSFUL_REASON_OUTCOME_NOT_ACHIEVED {
			return nil, raw, contract("unverified excavation failure")
		}
		err = ValidateExcavationEffect(out.Unsuccessful.GetEvidence(), w.Excavation)
		d := out.Unsuccessful.GetEvidence().GetExcavation()
		if d.GetCleared() || d.GetDesignated() {
			return nil, raw, contract("unverified excavation failure")
		}
	case *r.Progress_Pending:
		err = ValidateExcavationEffect(out.Pending.GetEvidence(), w.Excavation)
		d := out.Pending.GetEvidence().GetExcavation()
		if d.GetCleared() || !d.GetDesignated() {
			return nil, raw, contract("invalid excavation pending")
		}
	default:
		err = contract("unsupported excavation progress")
	}
	return reply, raw, err
}
