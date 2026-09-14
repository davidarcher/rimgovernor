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

// BuildingTemperatureAttempt is BuildingTemperature's WorkAttempt-equivalent:
// the write/lookup/observe scoping for one PatchBuilding target-temperature
// admission, mirroring WorkAttempt's shape.
type BuildingTemperatureAttempt struct {
	Identity    *c.Identity
	Attempt     *c.AttemptKey
	Owner       *a.Owner
	Generation  uint64
	Temperature domain.BuildingTemperature
}
type BuildingTemperatureControl struct{ client *Client }

func NewBuildingTemperatureControl(client *Client) (*BuildingTemperatureControl, error) {
	if client == nil {
		return nil, contract("building temperature client missing")
	}
	return &BuildingTemperatureControl{client}, nil
}
func validateBuildingTemperature(t domain.BuildingTemperature) error {
	_, err := domain.NewBuildingTemperature(t.Thing(), t.Celsius(), t.BeforeToken())
	return err
}
func buildingTemperatureOperation(t domain.BuildingTemperature) *op.Operation {
	return &op.Operation{Command: &op.Operation_PatchBuilding{PatchBuilding: &op.PatchBuilding{
		Building:          &op.EntityPrecondition{EntityId: proto.String(t.Thing()), ExpectedSnapshotToken: proto.String(t.BeforeToken())},
		TargetTemperature: proto.Float32(float32(t.Celsius())),
	}}}
}
func (client *Client) PreviewBuildingTemperature(ctx context.Context, identity *c.Identity, target domain.BuildingTemperature) (*op.PreviewReply, Result, error) {
	if ValidateIdentity(identity) != nil || validateBuildingTemperature(target) != nil {
		return nil, Result{}, contract("invalid building temperature preview")
	}
	reply := &op.PreviewReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/operations_preview", &op.PreviewRequest{Identity: proto.Clone(identity).(*c.Identity), Operation: buildingTemperatureOperation(target)}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown building temperature preview fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	v := reply.GetEvaluated()
	if v == nil || v.Accepted == nil || !v.GetAccepted() || v.Preparation != nil || v.Projected != nil || buildingContext(v.Context, identity, 0, false) != nil {
		return nil, raw, contract("invalid building temperature preview evidence")
	}
	return reply, raw, nil
}
func (writer *BuildingTemperatureControl) ApplyBuildingTemperature(ctx context.Context, pre *a.WritePrecondition, target domain.BuildingTemperature) (*op.ExecuteReply, Result, error) {
	if writer == nil || writer.client == nil || pre == nil || buildingUnknown(pre) != nil || ValidateIdentity(pre.Identity) != nil || buildingAttempt(pre.Attempt) != nil || pre.GetExpectedGeneration() == 0 || validID(pre.GetLeaseId()) != nil || validateBuildingTemperature(target) != nil {
		return nil, Result{}, contract("invalid building temperature execution")
	}
	reply := &op.ExecuteReply{}
	raw, err := writer.client.protoCall(ctx, "rimgovernor/operations_execute", &op.ExecuteRequest{Precondition: proto.Clone(pre).(*a.WritePrecondition), Operation: buildingTemperatureOperation(target)}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown building temperature execute fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	v := reply.GetReceipt()
	if v == nil || v.AuthorizingOwner == nil || v.AuthorizingOwner.GetControllerSessionId() != pre.Attempt.GetControllerSessionId() {
		return nil, raw, contract("building temperature owner mismatch")
	}
	err = buildingTemperatureReceipt(v, BuildingTemperatureAttempt{pre.Identity, pre.Attempt, v.AuthorizingOwner, pre.GetExpectedGeneration(), target})
	return reply, raw, err
}
func validBuildingTemperatureAttempt(w BuildingTemperatureAttempt) error {
	if ValidateIdentity(w.Identity) != nil || buildingAttempt(w.Attempt) != nil || authorityOwner(w.Owner) != nil || buildingUnknown(w.Owner) != nil || w.Generation == 0 || w.Owner.GetControllerSessionId() != w.Attempt.GetControllerSessionId() {
		return contract("invalid building temperature attempt")
	}
	return validateBuildingTemperature(w.Temperature)
}
func buildingTemperatureEffect(v *r.EffectEvidence, target domain.BuildingTemperature, matches bool) error {
	effect := v.GetSettings()
	if effect == nil || effect.Snapshot == nil || effect.Snapshot.GetEntityId() != target.Thing() || effect.Snapshot.GetBeforeToken() != target.BeforeToken() || validID(effect.Snapshot.GetAfterToken()) != nil || len(effect.Fields) != 1 {
		return contract("building temperature effect mismatch")
	}
	want := r.FieldOutcome_FIELD_OUTCOME_APPLIED
	if !matches {
		want = r.FieldOutcome_FIELD_OUTCOME_REFUSED
	}
	field := effect.Fields[0]
	if field == nil || field.GetField() != r.SettingsField_SETTINGS_FIELD_TEMPERATURE || field.GetOutcome() != want {
		return contract("building temperature field mismatch")
	}
	return nil
}
func buildingTemperatureReceipt(v *r.Receipt, w BuildingTemperatureAttempt) error {
	if v == nil || buildingUnknown(v) != nil || !proto.Equal(v.Attempt, w.Attempt) || !proto.Equal(v.AuthorizingOwner, w.Owner) || buildingContext(v.AdmittedContext, w.Identity, w.Generation, true) != nil {
		return contract("building temperature admission mismatch")
	}
	switch out := v.Outcome.(type) {
	case *r.Receipt_Applied:
		return buildingTemperatureEffect(out.Applied.GetObserved(), w.Temperature, true)
	case *r.Receipt_Uncertain:
		if out.Uncertain == nil {
			return contract("building temperature uncertainty missing")
		}
		if out.Uncertain.LastObserved != nil {
			return buildingTemperatureEffect(out.Uncertain.LastObserved, w.Temperature, true)
		}
		return nil
	default:
		return contract("unsupported building temperature receipt")
	}
}
func (client *Client) LookupBuildingTemperature(ctx context.Context, w BuildingTemperatureAttempt) (*r.LookupReply, Result, error) {
	if err := validBuildingTemperatureAttempt(w); err != nil {
		return nil, Result{}, err
	}
	reply := &r.LookupReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/receipts_lookup", &r.LookupRequest{Identity: w.Identity, Attempt: w.Attempt}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown building temperature lookup fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	switch v := reply.Outcome.(type) {
	case *r.LookupReply_Receipt:
		err = buildingTemperatureReceipt(v.Receipt, w)
	case *r.LookupReply_Unknown:
		err = buildingContext(v.Unknown.GetContext(), w.Identity, 0, false)
	case *r.LookupReply_InFlight:
		if v.InFlight == nil || !proto.Equal(v.InFlight.Attempt, w.Attempt) {
			return nil, raw, contract("building temperature in-flight mismatch")
		}
		err = buildingContext(v.InFlight.AdmittedContext, w.Identity, w.Generation, true)
	default:
		err = contract("building temperature lookup outcome missing")
	}
	return reply, raw, err
}
func (client *Client) ObserveBuildingTemperature(ctx context.Context, w BuildingTemperatureAttempt, admitted *r.Receipt) (*r.ProgressReply, Result, error) {
	if validBuildingTemperatureAttempt(w) != nil || buildingTemperatureReceipt(admitted, w) != nil {
		return nil, Result{}, contract("building temperature observation admission mismatch")
	}
	reply := &r.ProgressReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/receipts_observe_progress", &r.ProgressRequest{Identity: w.Identity, Attempt: w.Attempt}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown building temperature progress fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	v := reply.GetProgress()
	if v == nil || !proto.Equal(v.Attempt, w.Attempt) || buildingContext(v.Context, w.Identity, 0, false) != nil || v.Context.GetTick() < admitted.AdmittedContext.GetTick() {
		return nil, raw, contract("building temperature progress scope mismatch")
	}
	switch out := v.Effect.(type) {
	case *r.Progress_Unknown:
		if out.Unknown == nil {
			return nil, raw, contract("missing building temperature uncertainty")
		}
	case *r.Progress_Completed:
		if !v.GetCompleteInspection() {
			return nil, raw, contract("incomplete building temperature completion")
		}
		err = buildingTemperatureEffect(out.Completed.GetEvidence(), w.Temperature, true)
	case *r.Progress_Unsuccessful:
		if !v.GetCompleteInspection() || out.Unsuccessful.GetReason() != r.UnsuccessfulReason_UNSUCCESSFUL_REASON_OUTCOME_NOT_ACHIEVED {
			return nil, raw, contract("unverified building temperature failure")
		}
		err = buildingTemperatureEffect(out.Unsuccessful.GetEvidence(), w.Temperature, false)
	default:
		err = contract("unsupported building temperature progress")
	}
	return reply, raw, err
}
