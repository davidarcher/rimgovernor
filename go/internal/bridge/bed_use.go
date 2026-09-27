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

// BedUseAttempt is BedUse's WorkAttempt-equivalent: the
// write/lookup/observe scoping for one PatchBuilding medical admission,
// mirroring BuildingTemperatureAttempt's shape.
type BedUseAttempt struct {
	Identity   *c.Identity
	Attempt    *c.AttemptKey
	Generation uint64
	Medical    domain.BedUse
}
type BedUseControl struct{ client *Client }

func NewBedUseControl(client *Client) (*BedUseControl, error) {
	if client == nil {
		return nil, contract("bed medical client missing")
	}
	return &BedUseControl{client}, nil
}
func validateBedUse(t domain.BedUse) error {
	canonical, err := domain.NewBedMedical(t.Thing(), t.Medical(), t.BeforeToken())
	if t.Prisoners() {
		canonical, err = domain.NewBedPrisoners(t.Thing(), t.BeforeToken())
	}
	if err == nil && canonical != t {
		return contract("invalid bed medical")
	}
	return err
}
func bedUseOperation(t domain.BedUse) *op.Operation {
	patch := &op.PatchBuilding{Building: &op.EntityPrecondition{EntityId: proto.String(t.Thing()), ExpectedSnapshotToken: proto.String(t.BeforeToken())}}
	if t.Prisoners() {
		patch.ForPrisoners = proto.Bool(true)
	} else {
		patch.Medical = proto.Bool(t.Medical())
	}
	return &op.Operation{Command: &op.Operation_PatchBuilding{PatchBuilding: patch}}
}
func (client *Client) PreviewBedUse(ctx context.Context, identity *c.Identity, target domain.BedUse) (*op.PreviewReply, Result, error) {
	if ValidateIdentity(identity) != nil || validateBedUse(target) != nil {
		return nil, Result{}, contract("invalid bed medical preview")
	}
	reply := &op.PreviewReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/operations_preview", &op.PreviewRequest{Identity: proto.Clone(identity).(*c.Identity), Operation: bedUseOperation(target)}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown bed medical preview fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	v := reply.GetEvaluated()
	if v == nil || v.Accepted == nil || !v.GetAccepted() || v.Preparation != nil || v.Projected != nil || buildingContext(v.Context, identity, 0, false) != nil {
		return nil, raw, contract("invalid bed medical preview evidence")
	}
	return reply, raw, nil
}
func (writer *BedUseControl) ApplyBedUse(ctx context.Context, pre *a.WritePrecondition, target domain.BedUse) (*op.ExecuteReply, Result, error) {
	if writer == nil || writer.client == nil || pre == nil || buildingUnknown(pre) != nil || ValidateIdentity(pre.Identity) != nil || buildingAttempt(pre.Attempt) != nil || pre.GetExpectedGeneration() == 0 || validateBedUse(target) != nil {
		return nil, Result{}, contract("invalid bed medical execution")
	}
	reply := &op.ExecuteReply{}
	raw, err := writer.client.protoCall(ctx, "rimgovernor/operations_execute", &op.ExecuteRequest{Precondition: proto.Clone(pre).(*a.WritePrecondition), Operation: bedUseOperation(target)}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown bed medical execute fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	v := reply.GetReceipt()
	if v == nil {
		return nil, raw, contract("bed medical owner mismatch")
	}
	err = bedUseReceipt(v, BedUseAttempt{pre.Identity, pre.Attempt, pre.GetExpectedGeneration(), target})
	return reply, raw, err
}
func validBedUseAttempt(w BedUseAttempt) error {
	if ValidateIdentity(w.Identity) != nil || buildingAttempt(w.Attempt) != nil || w.Generation == 0 {
		return contract("invalid bed medical attempt")
	}
	return validateBedUse(w.Medical)
}
func bedUseEffect(v *r.EffectEvidence, target domain.BedUse, matches bool) error {
	effect := v.GetSettings()
	if effect == nil || effect.Snapshot == nil || effect.Snapshot.GetEntityId() != target.Thing() || effect.Snapshot.GetBeforeToken() != target.BeforeToken() || validID(effect.Snapshot.GetAfterToken()) != nil || len(effect.Fields) != 1 {
		return contract("bed medical effect mismatch")
	}
	want := r.FieldOutcome_FIELD_OUTCOME_APPLIED
	if !matches {
		want = r.FieldOutcome_FIELD_OUTCOME_REFUSED
	}
	kind := r.SettingsField_SETTINGS_FIELD_MEDICAL_BED
	if target.Prisoners() {
		kind = r.SettingsField_SETTINGS_FIELD_PRISONER_BED
	}
	field := effect.Fields[0]
	if field == nil || field.GetField() != kind || field.GetOutcome() != want {
		return contract("bed medical field mismatch")
	}
	return nil
}
func bedUseReceipt(v *r.Receipt, w BedUseAttempt) error {
	if v == nil || buildingUnknown(v) != nil || !proto.Equal(v.Attempt, w.Attempt) || buildingContext(v.AdmittedContext, w.Identity, w.Generation, true) != nil {
		return contract("bed medical admission mismatch")
	}
	switch out := v.Outcome.(type) {
	case *r.Receipt_Applied:
		return bedUseEffect(out.Applied.GetObserved(), w.Medical, true)
	case *r.Receipt_Uncertain:
		if out.Uncertain == nil {
			return contract("bed medical uncertainty missing")
		}
		if out.Uncertain.LastObserved != nil {
			return bedUseEffect(out.Uncertain.LastObserved, w.Medical, true)
		}
		return nil
	default:
		return contract("unsupported bed medical receipt")
	}
}
func (client *Client) LookupBedUse(ctx context.Context, w BedUseAttempt) (*r.LookupReply, Result, error) {
	if err := validBedUseAttempt(w); err != nil {
		return nil, Result{}, err
	}
	reply := &r.LookupReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/receipts_lookup", &r.LookupRequest{Identity: w.Identity, Attempt: w.Attempt}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown bed medical lookup fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	switch v := reply.Outcome.(type) {
	case *r.LookupReply_Receipt:
		err = bedUseReceipt(v.Receipt, w)
	case *r.LookupReply_Unknown:
		err = buildingContext(v.Unknown.GetContext(), w.Identity, 0, false)
	case *r.LookupReply_InFlight:
		if v.InFlight == nil || !proto.Equal(v.InFlight.Attempt, w.Attempt) {
			return nil, raw, contract("bed medical in-flight mismatch")
		}
		err = buildingContext(v.InFlight.AdmittedContext, w.Identity, w.Generation, true)
	default:
		err = contract("bed medical lookup outcome missing")
	}
	return reply, raw, err
}
func (client *Client) ObserveBedUse(ctx context.Context, w BedUseAttempt, admitted *r.Receipt) (*r.ProgressReply, Result, error) {
	if validBedUseAttempt(w) != nil || bedUseReceipt(admitted, w) != nil {
		return nil, Result{}, contract("bed medical observation admission mismatch")
	}
	reply := &r.ProgressReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/receipts_observe_progress", &r.ProgressRequest{Identity: w.Identity, Attempt: w.Attempt}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown bed medical progress fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	v := reply.GetProgress()
	if v == nil || !proto.Equal(v.Attempt, w.Attempt) || buildingContext(v.Context, w.Identity, 0, false) != nil || v.Context.GetTick() < admitted.AdmittedContext.GetTick() {
		return nil, raw, contract("bed medical progress scope mismatch")
	}
	switch out := v.Effect.(type) {
	case *r.Progress_Unknown:
		if out.Unknown == nil {
			return nil, raw, contract("missing bed medical uncertainty")
		}
	case *r.Progress_Completed:
		if !v.GetCompleteInspection() {
			return nil, raw, contract("incomplete bed medical completion")
		}
		err = bedUseEffect(out.Completed.GetEvidence(), w.Medical, true)
	case *r.Progress_Unsuccessful:
		if !v.GetCompleteInspection() || out.Unsuccessful.GetReason() != r.UnsuccessfulReason_UNSUCCESSFUL_REASON_OUTCOME_NOT_ACHIEVED {
			return nil, raw, contract("unverified bed medical failure")
		}
		err = bedUseEffect(out.Unsuccessful.GetEvidence(), w.Medical, false)
	default:
		err = contract("unsupported bed medical progress")
	}
	return reply, raw, err
}
