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

// ClaimBuildingAttempt is the write/lookup/observe scoping for one
// PatchBuilding claim admission (#459), mirroring BedMedicalAttempt.
type ClaimBuildingAttempt struct {
	Identity   *c.Identity
	Attempt    *c.AttemptKey
	Generation uint64
	Claim      domain.ClaimBuilding
}
type ClaimBuildingControl struct{ client *Client }

func NewClaimBuildingControl(client *Client) (*ClaimBuildingControl, error) {
	if client == nil {
		return nil, contract("claim building client missing")
	}
	return &ClaimBuildingControl{client}, nil
}
func validateClaimBuilding(t domain.ClaimBuilding) error {
	_, err := domain.NewClaimBuilding(t.Thing(), t.BeforeToken())
	return err
}
func claimBuildingOperation(t domain.ClaimBuilding) *op.Operation {
	return &op.Operation{Command: &op.Operation_PatchBuilding{PatchBuilding: &op.PatchBuilding{
		Building: &op.EntityPrecondition{EntityId: proto.String(t.Thing()), ExpectedSnapshotToken: proto.String(t.BeforeToken())},
		Claim:    proto.Bool(true),
	}}}
}
func (client *Client) PreviewClaimBuilding(ctx context.Context, identity *c.Identity, target domain.ClaimBuilding) (*op.PreviewReply, Result, error) {
	if ValidateIdentity(identity) != nil || validateClaimBuilding(target) != nil {
		return nil, Result{}, contract("invalid claim building preview")
	}
	reply := &op.PreviewReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/operations_preview", &op.PreviewRequest{Identity: proto.Clone(identity).(*c.Identity), Operation: claimBuildingOperation(target)}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown claim building preview fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	v := reply.GetEvaluated()
	if v == nil || v.Accepted == nil || !v.GetAccepted() || v.Preparation != nil || v.Projected != nil || buildingContext(v.Context, identity, 0, false) != nil {
		return nil, raw, contract("invalid claim building preview evidence")
	}
	return reply, raw, nil
}
func (writer *ClaimBuildingControl) ApplyClaimBuilding(ctx context.Context, pre *a.WritePrecondition, target domain.ClaimBuilding) (*op.ExecuteReply, Result, error) {
	if writer == nil || writer.client == nil || pre == nil || buildingUnknown(pre) != nil || ValidateIdentity(pre.Identity) != nil || buildingAttempt(pre.Attempt) != nil || pre.GetExpectedGeneration() == 0 || validateClaimBuilding(target) != nil {
		return nil, Result{}, contract("invalid claim building execution")
	}
	reply := &op.ExecuteReply{}
	raw, err := writer.client.protoCall(ctx, "rimgovernor/operations_execute", &op.ExecuteRequest{Precondition: proto.Clone(pre).(*a.WritePrecondition), Operation: claimBuildingOperation(target)}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown claim building execute fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	v := reply.GetReceipt()
	if v == nil {
		return nil, raw, contract("claim building owner mismatch")
	}
	err = claimBuildingReceipt(v, ClaimBuildingAttempt{pre.Identity, pre.Attempt, pre.GetExpectedGeneration(), target})
	return reply, raw, err
}
func validClaimBuildingAttempt(w ClaimBuildingAttempt) error {
	if ValidateIdentity(w.Identity) != nil || buildingAttempt(w.Attempt) != nil || w.Generation == 0 {
		return contract("invalid claim building attempt")
	}
	return validateClaimBuilding(w.Claim)
}
func claimBuildingEffect(v *r.EffectEvidence, target domain.ClaimBuilding, matches bool) error {
	effect := v.GetSettings()
	if effect == nil || effect.Snapshot == nil || effect.Snapshot.GetEntityId() != target.Thing() || effect.Snapshot.GetBeforeToken() != target.BeforeToken() || validID(effect.Snapshot.GetAfterToken()) != nil || len(effect.Fields) != 1 {
		return contract("claim building effect mismatch")
	}
	want := r.FieldOutcome_FIELD_OUTCOME_APPLIED
	if !matches {
		want = r.FieldOutcome_FIELD_OUTCOME_REFUSED
	}
	field := effect.Fields[0]
	if field == nil || field.GetField() != r.SettingsField_SETTINGS_FIELD_CLAIM || field.GetOutcome() != want {
		return contract("claim building field mismatch")
	}
	return nil
}
func claimBuildingReceipt(v *r.Receipt, w ClaimBuildingAttempt) error {
	if v == nil || buildingUnknown(v) != nil || !proto.Equal(v.Attempt, w.Attempt) || buildingContext(v.AdmittedContext, w.Identity, w.Generation, true) != nil {
		return contract("claim building admission mismatch")
	}
	switch out := v.Outcome.(type) {
	case *r.Receipt_Applied:
		return claimBuildingEffect(out.Applied.GetObserved(), w.Claim, true)
	case *r.Receipt_Uncertain:
		if out.Uncertain == nil {
			return contract("claim building uncertainty missing")
		}
		if out.Uncertain.LastObserved != nil {
			return claimBuildingEffect(out.Uncertain.LastObserved, w.Claim, true)
		}
		return nil
	default:
		return contract("unsupported claim building receipt")
	}
}
func (client *Client) LookupClaimBuilding(ctx context.Context, w ClaimBuildingAttempt) (*r.LookupReply, Result, error) {
	if err := validClaimBuildingAttempt(w); err != nil {
		return nil, Result{}, err
	}
	reply := &r.LookupReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/receipts_lookup", &r.LookupRequest{Identity: w.Identity, Attempt: w.Attempt}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown claim building lookup fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	switch v := reply.Outcome.(type) {
	case *r.LookupReply_Receipt:
		err = claimBuildingReceipt(v.Receipt, w)
	case *r.LookupReply_Unknown:
		err = buildingContext(v.Unknown.GetContext(), w.Identity, 0, false)
	case *r.LookupReply_InFlight:
		if v.InFlight == nil || !proto.Equal(v.InFlight.Attempt, w.Attempt) {
			return nil, raw, contract("claim building in-flight mismatch")
		}
		err = buildingContext(v.InFlight.AdmittedContext, w.Identity, w.Generation, true)
	default:
		err = contract("claim building lookup outcome missing")
	}
	return reply, raw, err
}
func (client *Client) ObserveClaimBuilding(ctx context.Context, w ClaimBuildingAttempt, admitted *r.Receipt) (*r.ProgressReply, Result, error) {
	if validClaimBuildingAttempt(w) != nil || claimBuildingReceipt(admitted, w) != nil {
		return nil, Result{}, contract("claim building observation admission mismatch")
	}
	reply := &r.ProgressReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/receipts_observe_progress", &r.ProgressRequest{Identity: w.Identity, Attempt: w.Attempt}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown claim building progress fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	v := reply.GetProgress()
	if v == nil || !proto.Equal(v.Attempt, w.Attempt) || buildingContext(v.Context, w.Identity, 0, false) != nil || v.Context.GetTick() < admitted.AdmittedContext.GetTick() {
		return nil, raw, contract("claim building progress scope mismatch")
	}
	switch out := v.Effect.(type) {
	case *r.Progress_Unknown:
		if out.Unknown == nil {
			return nil, raw, contract("missing claim building uncertainty")
		}
	case *r.Progress_Completed:
		if !v.GetCompleteInspection() {
			return nil, raw, contract("incomplete claim building completion")
		}
		err = claimBuildingEffect(out.Completed.GetEvidence(), w.Claim, true)
	case *r.Progress_Unsuccessful:
		if !v.GetCompleteInspection() || out.Unsuccessful.GetReason() != r.UnsuccessfulReason_UNSUCCESSFUL_REASON_OUTCOME_NOT_ACHIEVED {
			return nil, raw, contract("unverified claim building failure")
		}
		err = claimBuildingEffect(out.Unsuccessful.GetEvidence(), w.Claim, false)
	default:
		err = contract("unsupported claim building progress")
	}
	return reply, raw, err
}
