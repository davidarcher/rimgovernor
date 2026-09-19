package bridge

import (
	"context"

	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

// DeconstructionAttempt identifies an admitted non-colony target. Native repeats
// occupant, eligibility and roof support checks; no snapshot token is sent.
type DeconstructionAttempt struct {
	Identity   *c.Identity
	Attempt    *c.AttemptKey
	Generation uint64
	Target     string
}

func deconstructionEntity(target string) *o.EntityPrecondition {
	return &o.EntityPrecondition{EntityId: proto.String(target)}
}

func deconstructionOperation(target string) *o.Operation {
	return &o.Operation{Command: &o.Operation_Deconstruct{Deconstruct: &o.Deconstruct{Target: deconstructionEntity(target)}}}
}

func deconstructionAttempt(v DeconstructionAttempt) (DeconstructionAttempt, error) {
	if err := ValidateIdentity(v.Identity); err != nil {
		return DeconstructionAttempt{}, err
	}
	if err := buildingAttempt(v.Attempt); err != nil {
		return DeconstructionAttempt{}, err
	}
	if v.Generation == 0 {
		return DeconstructionAttempt{}, contract("deconstruction admission owner or generation mismatch")
	}
	if validID(v.Target) != nil {
		return DeconstructionAttempt{}, contract("invalid deconstruction target")
	}
	v.Identity = proto.Clone(v.Identity).(*c.Identity)
	v.Attempt = proto.Clone(v.Attempt).(*c.AttemptKey)
	return v, nil
}

func deconstructionEvidence(evidence *r.EffectEvidence, expected DeconstructionAttempt) (*r.DeconstructEffect, error) {
	wall := evidence.GetDeconstruct()
	if wall == nil || wall.TargetId == nil || wall.GetTargetId() != expected.Target {
		return nil, contract("deconstruction target mismatch")
	}
	allowed := &r.DeconstructEffect{TargetId: wall.TargetId, DesignationId: wall.DesignationId, WorkerIds: wall.WorkerIds, DemolitionObserved: wall.DemolitionObserved, Site: wall.Site}
	if !proto.Equal(wall, allowed) || buildingUnknown(evidence) != nil || validID(wall.GetDesignationId()) != nil || wall.DemolitionObserved == nil || wall.Site == nil || wall.Site.GetEntityId() != expected.Target || validID(wall.Site.GetBeforeToken()) != nil {
		return nil, contract("deconstruction effect fields missing or unsupported")
	}
	for _, worker := range wall.WorkerIds {
		if validID(worker) != nil {
			return nil, contract("invalid deconstruction worker")
		}
	}
	return wall, nil
}

func deconstructionReceipt(v *r.Receipt, expected DeconstructionAttempt) error {
	if v == nil || buildingUnknown(v) != nil || !proto.Equal(v.Attempt, expected.Attempt) {
		return contract("deconstruction admission mismatch")
	}
	if err := buildingContext(v.AdmittedContext, expected.Identity, expected.Generation, true); err != nil {
		return err
	}
	switch outcome := v.Outcome.(type) {
	case *r.Receipt_Applied:
		if outcome.Applied == nil {
			return contract("deconstruction applied missing")
		}
		_, err := deconstructionEvidence(outcome.Applied.GetObserved(), expected)
		return err
	case *r.Receipt_Uncertain:
		if outcome.Uncertain == nil {
			return contract("deconstruction uncertainty missing")
		}
		if outcome.Uncertain.LastObserved != nil {
			_, err := deconstructionEvidence(outcome.Uncertain.LastObserved, expected)
			return err
		}
		return nil
	default:
		return contract("unsupported deconstruction receipt")
	}
}

type DeconstructionWriter struct{ client *Client }

func NewDeconstructionWriter(client *Client) (*DeconstructionWriter, error) {
	if client == nil {
		return nil, contract("deconstruction client missing")
	}
	return &DeconstructionWriter{client}, nil
}

// ApplyDeconstruction dispatches one already-admitted clearance demolition.
func (writer *DeconstructionWriter) ApplyDeconstruction(ctx context.Context, pre *a.WritePrecondition, target string) (*o.ExecuteReply, Result, error) {
	if writer == nil || writer.client == nil || pre == nil || buildingUnknown(pre) != nil || ValidateIdentity(pre.Identity) != nil || buildingAttempt(pre.Attempt) != nil || pre.GetExpectedGeneration() == 0 {
		return nil, Result{}, contract("invalid deconstruction execution")
	}
	if validID(target) != nil {
		return nil, Result{}, contract("invalid deconstruction target")
	}
	expected, err := deconstructionAttempt(DeconstructionAttempt{Identity: pre.Identity, Attempt: pre.Attempt, Generation: pre.GetExpectedGeneration(), Target: target})
	if err != nil {
		return nil, Result{}, err
	}
	pre = proto.Clone(pre).(*a.WritePrecondition)
	reply := &o.ExecuteReply{}
	raw, err := writer.client.protoCall(ctx, "rimgovernor/operations_execute", &o.ExecuteRequest{Precondition: pre, Operation: deconstructionOperation(target)}, reply)
	if err != nil {
		return nil, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return reply, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *o.ExecuteReply_Receipt:
		err = deconstructionReceipt(v.Receipt, expected)
	case *o.ExecuteReply_Failure:
		err = failure(v.Failure, raw)
	default:
		err = contract("deconstruction execute outcome missing")
	}
	return reply, raw, err
}

func (client *Client) LookupDeconstruction(ctx context.Context, w DeconstructionAttempt) (*r.LookupReply, Result, error) {
	expected, err := deconstructionAttempt(w)
	if err != nil {
		return nil, Result{}, err
	}
	reply := &r.LookupReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/receipts_lookup", &r.LookupRequest{Identity: expected.Identity, Attempt: expected.Attempt}, reply)
	if err != nil {
		return nil, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return reply, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *r.LookupReply_Receipt:
		err = deconstructionReceipt(v.Receipt, expected)
	case *r.LookupReply_InFlight:
		if v.InFlight == nil || !proto.Equal(v.InFlight.Attempt, expected.Attempt) {
			err = contract("deconstruction in-flight attempt mismatch")
		} else {
			err = buildingContext(v.InFlight.AdmittedContext, expected.Identity, expected.Generation, true)
		}
	case *r.LookupReply_Unknown:
		if v.Unknown == nil {
			err = contract("deconstruction unknown context missing")
		} else {
			err = buildingContext(v.Unknown.Context, expected.Identity, 0, false)
		}
	case *r.LookupReply_Failure:
		err = failure(v.Failure, raw)
	default:
		err = contract("deconstruction lookup outcome missing")
	}
	return reply, raw, err
}

func (client *Client) ObserveDeconstructionProgress(ctx context.Context, w DeconstructionAttempt, admitted *r.Receipt) (*r.ProgressReply, Result, error) {
	expected, err := deconstructionAttempt(w)
	if err != nil {
		return nil, Result{}, err
	}
	if admitted != nil {
		if err = deconstructionReceipt(admitted, expected); err != nil {
			return nil, Result{}, err
		}
		admitted = proto.Clone(admitted).(*r.Receipt)
	}
	reply := &r.ProgressReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/receipts_observe_progress", &r.ProgressRequest{Identity: expected.Identity, Attempt: expected.Attempt}, reply)
	if err != nil {
		return nil, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return reply, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *r.ProgressReply_Progress:
		err = deconstructionProgress(v.Progress, expected, admitted)
	case *r.ProgressReply_Failure:
		err = failure(v.Failure, raw)
	default:
		err = contract("deconstruction progress outcome missing")
	}
	return reply, raw, err
}

func deconstructionProgress(v *r.Progress, expected DeconstructionAttempt, admitted *r.Receipt) error {
	if v == nil || buildingUnknown(v) != nil || v.CompleteInspection == nil || !proto.Equal(v.Attempt, expected.Attempt) {
		return contract("deconstruction progress attempt mismatch")
	}
	if err := buildingContext(v.Context, expected.Identity, 0, false); err != nil {
		return err
	}
	if admitted != nil && v.Context.GetTick() < admitted.AdmittedContext.GetTick() {
		return contract("deconstruction progress predates admission")
	}
	switch outcome := v.Effect.(type) {
	case *r.Progress_Unknown:
		if outcome.Unknown == nil || !diagnostic(outcome.Unknown.Reason) {
			return contract("deconstruction unknown progress missing")
		}
		return nil
	case *r.Progress_Pending:
		if outcome.Pending == nil || !v.GetCompleteInspection() {
			return contract("deconstruction pending missing")
		}
		_, err := deconstructionEvidence(outcome.Pending.Evidence, expected)
		return err
	case *r.Progress_Completed:
		if outcome.Completed == nil || !v.GetCompleteInspection() {
			return contract("deconstruction completed missing")
		}
		effect, err := deconstructionEvidence(outcome.Completed.Evidence, expected)
		if err == nil && !effect.GetDemolitionObserved() {
			return contract("deconstruction completion lacks native demolition")
		}
		return err
	case *r.Progress_Absent:
		if outcome.Absent == nil || validID(outcome.Absent.GetInspectionToken()) != nil || !v.GetCompleteInspection() {
			return contract("deconstruction absence lacks inspection")
		}
		return nil
	case *r.Progress_Unsuccessful:
		if outcome.Unsuccessful == nil || outcome.Unsuccessful.Reason == nil || outcome.Unsuccessful.GetReason() == 0 || !v.GetCompleteInspection() || !diagnostic(outcome.Unsuccessful.Detail) {
			return contract("deconstruction unsuccessful reason missing")
		}
		_, err := deconstructionEvidence(outcome.Unsuccessful.Evidence, expected)
		return err
	default:
		return contract("deconstruction progress state missing")
	}
}

// ReleaseDeconstructions retires only designations still owned by the controller.
func (writer *DeconstructionWriter) ReleaseDeconstructions(ctx context.Context, pre *a.WritePrecondition) (*o.ExecuteReply, Result, error) {
	if writer == nil || writer.client == nil || pre == nil || buildingUnknown(pre) != nil || ValidateIdentity(pre.Identity) != nil || buildingAttempt(pre.Attempt) != nil || pre.GetExpectedGeneration() == 0 {
		return nil, Result{}, contract("invalid deconstruction release")
	}
	pre = proto.Clone(pre).(*a.WritePrecondition)
	reply := &o.ExecuteReply{}
	raw, err := writer.client.protoCall(ctx, "rimgovernor/operations_execute", &o.ExecuteRequest{Precondition: pre, Operation: &o.Operation{Command: &o.Operation_ReleaseDeconstructions{ReleaseDeconstructions: &o.ReleaseDeconstructions{}}}}, reply)
	if err != nil {
		return nil, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return reply, raw, err
	}
	if v := reply.GetFailure(); v != nil {
		return reply, raw, failure(v, raw)
	}
	v := reply.GetReceipt()
	if v == nil || !proto.Equal(v.Attempt, pre.Attempt) || buildingContext(v.AdmittedContext, pre.Identity, pre.GetExpectedGeneration(), true) != nil {
		return reply, raw, contract("deconstruction release admission mismatch")
	}
	var evidence *r.EffectEvidence
	switch outcome := v.Outcome.(type) {
	case *r.Receipt_Applied:
		evidence = outcome.Applied.GetObserved()
	case *r.Receipt_Uncertain:
		if outcome.Uncertain == nil {
			return reply, raw, contract("deconstruction release uncertainty missing")
		}
		evidence = outcome.Uncertain.LastObserved
		if evidence == nil {
			return reply, raw, nil
		}
	default:
		return reply, raw, contract("unsupported deconstruction release receipt")
	}
	effect := evidence.GetReleaseDeconstructions()
	if effect == nil || effect.ReleasedCount == nil || effect.GetReleasedCount() < 0 {
		return reply, raw, contract("deconstruction release count missing or invalid")
	}
	return reply, raw, nil
}
