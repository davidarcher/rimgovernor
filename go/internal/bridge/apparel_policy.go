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

type ApparelPolicyAttempt struct {
	Identity   *c.Identity
	Attempt    *c.AttemptKey
	Generation uint64
	Value      domain.ApparelPolicy
}

type ApparelPolicyControl struct{ client *Client }

func NewApparelPolicyControl(client *Client) (*ApparelPolicyControl, error) {
	if client == nil {
		return nil, contract("apparel policy client missing")
	}
	return &ApparelPolicyControl{client}, nil
}
func validApparelPolicy(v domain.ApparelPolicy) error {
	_, err := domain.NewApparelPolicyAction("check", v)
	return err
}
func apparelPolicyOperation(v domain.ApparelPolicy) *op.Operation {
	s := v.Spec()
	return &op.Operation{Command: &op.Operation_SetApparelPolicy{SetApparelPolicy: &op.SetApparelPolicy{Pawn: &op.EntityPrecondition{EntityId: proto.String(string(s.Pawn)), ExpectedSnapshotToken: proto.String(s.Token)}, Name: proto.String(s.Name), AllowedDefs: s.Definitions, MinHitPoints: proto.Float32(float32(s.MinHP)), MaxHitPoints: proto.Float32(float32(s.MaxHP)), MinQuality: proto.Int32(s.MinQuality), MaxQuality: proto.Int32(s.MaxQuality)}}}
}
func (client *Client) PreviewSetApparelPolicy(ctx context.Context, identity *c.Identity, value domain.ApparelPolicy) (*op.PreviewReply, Result, error) {
	if ValidateIdentity(identity) != nil || validApparelPolicy(value) != nil {
		return nil, Result{}, contract("invalid apparel policy preview")
	}
	reply := &op.PreviewReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/operations_preview", &op.PreviewRequest{Identity: proto.Clone(identity).(*c.Identity), Operation: apparelPolicyOperation(value)}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown apparel policy preview fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	v := reply.GetEvaluated()
	if v == nil || v.Accepted == nil || v.Preparation != nil || v.Projected != nil || buildingContext(v.Context, identity, 0, false) != nil {
		return nil, raw, contract("invalid apparel policy preview evidence")
	}
	return reply, raw, nil
}
func (writer *ApparelPolicyControl) SetApparelPolicy(ctx context.Context, pre *a.WritePrecondition, value domain.ApparelPolicy) (*op.ExecuteReply, Result, error) {
	if writer == nil || writer.client == nil || pre == nil || buildingUnknown(pre) != nil || ValidateIdentity(pre.Identity) != nil || buildingAttempt(pre.Attempt) != nil || pre.GetExpectedGeneration() == 0 || validApparelPolicy(value) != nil {
		return nil, Result{}, contract("invalid apparel policy execution")
	}
	reply := &op.ExecuteReply{}
	raw, err := writer.client.protoCall(ctx, "rimgovernor/operations_execute", &op.ExecuteRequest{Precondition: proto.Clone(pre).(*a.WritePrecondition), Operation: apparelPolicyOperation(value)}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown apparel policy execute fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	v := reply.GetReceipt()
	if v == nil {
		return nil, raw, contract("apparel policy owner mismatch")
	}
	err = apparelPolicyReceipt(v, ApparelPolicyAttempt{pre.Identity, pre.Attempt, pre.GetExpectedGeneration(), value})
	return reply, raw, err
}
func validApparelPolicyAttempt(w ApparelPolicyAttempt) error {
	if ValidateIdentity(w.Identity) != nil || buildingAttempt(w.Attempt) != nil || w.Generation == 0 {
		return contract("invalid apparel policy attempt")
	}
	return validApparelPolicy(w.Value)
}

func apparelPolicyEffect(v *r.EffectEvidence, w ApparelPolicyAttempt, applied bool) error {
	effect := v.GetSettings().GetSnapshot()
	spec := w.Value.Spec()
	if effect == nil || buildingUnknown(v) != nil || effect.GetEntityId() != string(spec.Pawn) || effect.GetBeforeToken() != spec.Token || validID(effect.GetAfterToken()) != nil {
		return contract("apparel policy effect mismatch")
	}
	return nil
}
func apparelPolicyReceipt(v *r.Receipt, w ApparelPolicyAttempt) error {
	if v == nil || buildingUnknown(v) != nil || !proto.Equal(v.Attempt, w.Attempt) || buildingContext(v.AdmittedContext, w.Identity, w.Generation, true) != nil {
		return contract("apparel policy admission mismatch")
	}
	switch out := v.Outcome.(type) {
	case *r.Receipt_Applied:
		return apparelPolicyEffect(out.Applied.GetObserved(), w, true)
	case *r.Receipt_Uncertain:
		if out.Uncertain == nil {
			return contract("apparel policy uncertainty missing")
		}
		if out.Uncertain.LastObserved != nil {
			return apparelPolicyEffect(out.Uncertain.LastObserved, w, false)
		}
		return nil
	default:
		return contract("unsupported apparel policy receipt")
	}
}
func (client *Client) LookupSetApparelPolicy(ctx context.Context, w ApparelPolicyAttempt) (*r.LookupReply, Result, error) {
	if err := validApparelPolicyAttempt(w); err != nil {
		return nil, Result{}, err
	}
	reply := &r.LookupReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/receipts_lookup", &r.LookupRequest{Identity: w.Identity, Attempt: w.Attempt}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown apparel policy lookup fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	switch v := reply.Outcome.(type) {
	case *r.LookupReply_Receipt:
		err = apparelPolicyReceipt(v.Receipt, w)
	case *r.LookupReply_Unknown:
		err = buildingContext(v.Unknown.GetContext(), w.Identity, 0, false)
	case *r.LookupReply_InFlight:
		if v.InFlight == nil || !proto.Equal(v.InFlight.Attempt, w.Attempt) {
			return nil, raw, contract("apparel policy in-flight mismatch")
		}
		err = buildingContext(v.InFlight.AdmittedContext, w.Identity, w.Generation, true)
	default:
		err = contract("apparel policy lookup outcome missing")
	}
	return reply, raw, err
}
func (client *Client) ObserveSetApparelPolicyProgress(ctx context.Context, w ApparelPolicyAttempt, admitted *r.Receipt) (*r.ProgressReply, Result, error) {
	if validApparelPolicyAttempt(w) != nil {
		return nil, Result{}, contract("apparel policy observation admission mismatch")
	}
	if admitted != nil {
		if err := apparelPolicyReceipt(admitted, w); err != nil {
			return nil, Result{}, err
		}
	}
	reply := &r.ProgressReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/receipts_observe_progress", &r.ProgressRequest{Identity: w.Identity, Attempt: w.Attempt}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown apparel policy progress fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	v := reply.GetProgress()
	if v == nil || !proto.Equal(v.Attempt, w.Attempt) || buildingContext(v.Context, w.Identity, 0, false) != nil || (admitted != nil && v.Context.GetTick() < admitted.AdmittedContext.GetTick()) {
		return nil, raw, contract("apparel policy progress scope mismatch")
	}
	switch out := v.Effect.(type) {
	case *r.Progress_Unknown:
		if out.Unknown == nil {
			return nil, raw, contract("missing apparel policy uncertainty")
		}
	case *r.Progress_Completed:
		if !v.GetCompleteInspection() {
			return nil, raw, contract("incomplete apparel policy completion")
		}
		err = apparelPolicyEffect(out.Completed.GetEvidence(), w, true)
	case *r.Progress_Unsuccessful:
		if !v.GetCompleteInspection() || out.Unsuccessful.GetReason() != r.UnsuccessfulReason_UNSUCCESSFUL_REASON_OUTCOME_NOT_ACHIEVED {
			return nil, raw, contract("unverified apparel policy failure")
		}
		err = apparelPolicyEffect(out.Unsuccessful.GetEvidence(), w, false)
	default:
		err = contract("unsupported apparel policy progress")
	}
	return reply, raw, err
}
