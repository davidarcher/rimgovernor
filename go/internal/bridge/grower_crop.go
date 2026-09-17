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

// GrowerCropAttempt is GrowerCrop's WorkAttempt-equivalent: the
// write/lookup/observe scoping for one PatchBuilding plant_def admission,
// mirroring BedMedicalAttempt's shape.
type GrowerCropAttempt struct {
	Identity   *c.Identity
	Attempt    *c.AttemptKey
	Generation uint64
	Crop       domain.GrowerCrop
}
type GrowerCropControl struct{ client *Client }

func NewGrowerCropControl(client *Client) (*GrowerCropControl, error) {
	if client == nil {
		return nil, contract("grower crop client missing")
	}
	return &GrowerCropControl{client}, nil
}
func validateGrowerCrop(t domain.GrowerCrop) error {
	_, err := domain.NewGrowerCrop(t.Thing(), t.Crop(), t.BeforeToken())
	return err
}
func growerCropOperation(t domain.GrowerCrop) *op.Operation {
	return &op.Operation{Command: &op.Operation_PatchBuilding{PatchBuilding: &op.PatchBuilding{
		Building: &op.EntityPrecondition{EntityId: proto.String(t.Thing()), ExpectedSnapshotToken: proto.String(t.BeforeToken())},
		PlantDef: proto.String(t.Crop()),
	}}}
}
func (client *Client) PreviewGrowerCrop(ctx context.Context, identity *c.Identity, target domain.GrowerCrop) (*op.PreviewReply, Result, error) {
	if ValidateIdentity(identity) != nil || validateGrowerCrop(target) != nil {
		return nil, Result{}, contract("invalid grower crop preview")
	}
	reply := &op.PreviewReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/operations_preview", &op.PreviewRequest{Identity: proto.Clone(identity).(*c.Identity), Operation: growerCropOperation(target)}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown grower crop preview fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	v := reply.GetEvaluated()
	if v == nil || v.Accepted == nil || !v.GetAccepted() || v.Preparation != nil || v.Projected != nil || buildingContext(v.Context, identity, 0, false) != nil {
		return nil, raw, contract("invalid grower crop preview evidence")
	}
	return reply, raw, nil
}
func (writer *GrowerCropControl) ApplyGrowerCrop(ctx context.Context, pre *a.WritePrecondition, target domain.GrowerCrop) (*op.ExecuteReply, Result, error) {
	if writer == nil || writer.client == nil || pre == nil || buildingUnknown(pre) != nil || ValidateIdentity(pre.Identity) != nil || buildingAttempt(pre.Attempt) != nil || pre.GetExpectedGeneration() == 0 || validateGrowerCrop(target) != nil {
		return nil, Result{}, contract("invalid grower crop execution")
	}
	reply := &op.ExecuteReply{}
	raw, err := writer.client.protoCall(ctx, "rimgovernor/operations_execute", &op.ExecuteRequest{Precondition: proto.Clone(pre).(*a.WritePrecondition), Operation: growerCropOperation(target)}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown grower crop execute fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	v := reply.GetReceipt()
	if v == nil {
		return nil, raw, contract("grower crop owner mismatch")
	}
	err = growerCropReceipt(v, GrowerCropAttempt{pre.Identity, pre.Attempt, pre.GetExpectedGeneration(), target})
	return reply, raw, err
}
func validGrowerCropAttempt(w GrowerCropAttempt) error {
	if ValidateIdentity(w.Identity) != nil || buildingAttempt(w.Attempt) != nil || w.Generation == 0 {
		return contract("invalid grower crop attempt")
	}
	return validateGrowerCrop(w.Crop)
}
func growerCropEffect(v *r.EffectEvidence, target domain.GrowerCrop, matches bool) error {
	effect := v.GetSettings()
	if effect == nil || effect.Snapshot == nil || effect.Snapshot.GetEntityId() != target.Thing() || effect.Snapshot.GetBeforeToken() != target.BeforeToken() || validID(effect.Snapshot.GetAfterToken()) != nil || len(effect.Fields) != 1 {
		return contract("grower crop effect mismatch")
	}
	want := r.FieldOutcome_FIELD_OUTCOME_APPLIED
	if !matches {
		want = r.FieldOutcome_FIELD_OUTCOME_REFUSED
	}
	field := effect.Fields[0]
	if field == nil || field.GetField() != r.SettingsField_SETTINGS_FIELD_GROWER_CROP || field.GetOutcome() != want {
		return contract("grower crop field mismatch")
	}
	return nil
}
func growerCropReceipt(v *r.Receipt, w GrowerCropAttempt) error {
	if v == nil || buildingUnknown(v) != nil || !proto.Equal(v.Attempt, w.Attempt) || buildingContext(v.AdmittedContext, w.Identity, w.Generation, true) != nil {
		return contract("grower crop admission mismatch")
	}
	switch out := v.Outcome.(type) {
	case *r.Receipt_Applied:
		return growerCropEffect(out.Applied.GetObserved(), w.Crop, true)
	case *r.Receipt_Uncertain:
		if out.Uncertain == nil {
			return contract("grower crop uncertainty missing")
		}
		if out.Uncertain.LastObserved != nil {
			return growerCropEffect(out.Uncertain.LastObserved, w.Crop, true)
		}
		return nil
	default:
		return contract("unsupported grower crop receipt")
	}
}
func (client *Client) LookupGrowerCrop(ctx context.Context, w GrowerCropAttempt) (*r.LookupReply, Result, error) {
	if err := validGrowerCropAttempt(w); err != nil {
		return nil, Result{}, err
	}
	reply := &r.LookupReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/receipts_lookup", &r.LookupRequest{Identity: w.Identity, Attempt: w.Attempt}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown grower crop lookup fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	switch v := reply.Outcome.(type) {
	case *r.LookupReply_Receipt:
		err = growerCropReceipt(v.Receipt, w)
	case *r.LookupReply_Unknown:
		err = buildingContext(v.Unknown.GetContext(), w.Identity, 0, false)
	case *r.LookupReply_InFlight:
		if v.InFlight == nil || !proto.Equal(v.InFlight.Attempt, w.Attempt) {
			return nil, raw, contract("grower crop in-flight mismatch")
		}
		err = buildingContext(v.InFlight.AdmittedContext, w.Identity, w.Generation, true)
	default:
		err = contract("grower crop lookup outcome missing")
	}
	return reply, raw, err
}
func (client *Client) ObserveGrowerCrop(ctx context.Context, w GrowerCropAttempt, admitted *r.Receipt) (*r.ProgressReply, Result, error) {
	if validGrowerCropAttempt(w) != nil || growerCropReceipt(admitted, w) != nil {
		return nil, Result{}, contract("grower crop observation admission mismatch")
	}
	reply := &r.ProgressReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/receipts_observe_progress", &r.ProgressRequest{Identity: w.Identity, Attempt: w.Attempt}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown grower crop progress fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	v := reply.GetProgress()
	if v == nil || !proto.Equal(v.Attempt, w.Attempt) || buildingContext(v.Context, w.Identity, 0, false) != nil || v.Context.GetTick() < admitted.AdmittedContext.GetTick() {
		return nil, raw, contract("grower crop progress scope mismatch")
	}
	switch out := v.Effect.(type) {
	case *r.Progress_Unknown:
		if out.Unknown == nil {
			return nil, raw, contract("missing grower crop uncertainty")
		}
	case *r.Progress_Completed:
		if !v.GetCompleteInspection() {
			return nil, raw, contract("incomplete grower crop completion")
		}
		err = growerCropEffect(out.Completed.GetEvidence(), w.Crop, true)
	case *r.Progress_Unsuccessful:
		if !v.GetCompleteInspection() || out.Unsuccessful.GetReason() != r.UnsuccessfulReason_UNSUCCESSFUL_REASON_OUTCOME_NOT_ACHIEVED {
			return nil, raw, contract("unverified grower crop failure")
		}
		err = growerCropEffect(out.Unsuccessful.GetEvidence(), w.Crop, false)
	default:
		err = contract("unsupported grower crop progress")
	}
	return reply, raw, err
}
