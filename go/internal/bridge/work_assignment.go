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

type WorkAttempt struct {
	Identity   *c.Identity
	Attempt    *c.AttemptKey
	Generation uint64
	Work       domain.WorkAssignment
}
type WorkControl struct{ client *Client }

func NewWorkControl(client *Client) (*WorkControl, error) {
	if client == nil {
		return nil, contract("work client missing")
	}
	return &WorkControl{client}, nil
}
func validateWork(w domain.WorkAssignment) error {
	canonical, err := w.Canonical()
	if err != nil || canonical != w {
		return contract("invalid work assignment")
	}
	return nil
}
func workOperation(w domain.WorkAssignment) *op.Operation {
	patch := &op.PatchPawn{Pawn: &op.EntityPrecondition{EntityId: proto.String(string(w.Pawn())), ExpectedSnapshotToken: proto.String(w.BeforeToken())}}
	switch w.MedicalCare() {
	case "NoMeds":
		patch.MedicalCare = op.MedicalCare_MEDICAL_CARE_NO_MEDICINE.Enum()
	case "HerbalOrWorse":
		patch.MedicalCare = op.MedicalCare_MEDICAL_CARE_HERBAL_OR_WORSE.Enum()
	case "NormalOrWorse":
		patch.MedicalCare = op.MedicalCare_MEDICAL_CARE_NORMAL_OR_WORSE.Enum()
	}
	for _, setting := range w.Settings() {
		patch.Work = append(patch.Work, &op.WorkPriority{WorkTypeDef: proto.String(setting.Definition), Priority: proto.Int32(setting.Priority)})
	}
	if w.HasArea() {
		if w.AreaClear() {
			patch.AllowedArea = &op.Assignment{Value: &op.Assignment_Clear{Clear: &op.Clear{}}}
		} else {
			patch.AllowedArea = &op.Assignment{Value: &op.Assignment_EntityId{EntityId: w.Area()}}
		}
	}
	if w.HasSchedule() {
		patch.Schedule = &op.Schedule{AssignmentDefs: append([]string(nil), w.Schedule()...)}
	}
	if defs := w.FoodAllow(); len(defs) > 0 {
		patch.FoodAllow = &op.DefinitionList{Defs: defs}
	}
	return &op.Operation{Command: &op.Operation_PatchPawn{PatchPawn: patch}}
}
func (client *Client) PreviewWorkAssignment(ctx context.Context, identity *c.Identity, target domain.WorkAssignment) (*op.PreviewReply, Result, error) {
	if ValidateIdentity(identity) != nil || validateWork(target) != nil {
		return nil, Result{}, contract("invalid work preview")
	}
	reply := &op.PreviewReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/operations_preview", &op.PreviewRequest{Identity: proto.Clone(identity).(*c.Identity), Operation: workOperation(target)}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown work preview fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	v := reply.GetEvaluated()
	if v == nil || v.Accepted == nil || !v.GetAccepted() || v.Preparation != nil || v.Projected != nil || buildingContext(v.Context, identity, 0, false) != nil {
		return nil, raw, contract("invalid work preview evidence")
	}
	return reply, raw, nil
}
func (writer *WorkControl) AssignWork(ctx context.Context, pre *a.WritePrecondition, target domain.WorkAssignment) (*op.ExecuteReply, Result, error) {
	if writer == nil || writer.client == nil || pre == nil || buildingUnknown(pre) != nil || ValidateIdentity(pre.Identity) != nil || buildingAttempt(pre.Attempt) != nil || pre.GetExpectedGeneration() == 0 || validateWork(target) != nil {
		return nil, Result{}, contract("invalid work execution")
	}
	reply := &op.ExecuteReply{}
	raw, err := writer.client.protoCall(ctx, "rimgovernor/operations_execute", &op.ExecuteRequest{Precondition: proto.Clone(pre).(*a.WritePrecondition), Operation: workOperation(target)}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown work execute fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	// The runtime additionally compares full owner/direction against its admission.
	v := reply.GetReceipt()
	if v == nil {
		return nil, raw, contract("work owner mismatch")
	}
	err = workReceipt(v, WorkAttempt{pre.Identity, pre.Attempt, pre.GetExpectedGeneration(), target})
	return reply, raw, err
}
func validWorkAttempt(w WorkAttempt) error {
	if ValidateIdentity(w.Identity) != nil || buildingAttempt(w.Attempt) != nil || w.Generation == 0 {
		return contract("invalid work attempt")
	}
	canonical, err := w.Work.Canonical()
	if err != nil || canonical != w.Work {
		return contract("invalid work assignment")
	}
	return nil
}
func workEffect(v *r.EffectEvidence, work domain.WorkAssignment, matches bool) error {
	effect := v.GetSettings()
	expectedFields := len(work.Settings())
	if work.MedicalCare() != "" {
		expectedFields++
	}
	if len(work.FoodAllow()) > 0 {
		expectedFields++
	}
	if work.HasArea() {
		expectedFields++
	}
	if work.HasSchedule() {
		expectedFields++
	}
	if effect == nil || effect.Snapshot == nil || effect.Snapshot.GetEntityId() != string(work.Pawn()) || effect.Snapshot.GetBeforeToken() != work.BeforeToken() || validID(effect.Snapshot.GetAfterToken()) != nil || len(effect.Fields) != expectedFields {
		return contract("work effect mismatch")
	}
	seen := map[string]bool{}
	for _, setting := range work.Settings() {
		seen[setting.Definition] = true
	}
	areaSeen := !work.HasArea()
	scheduleSeen := !work.HasSchedule()
	foodSeen := len(work.FoodAllow()) == 0
	careSeen := work.MedicalCare() == ""
	want := r.FieldOutcome_FIELD_OUTCOME_APPLIED
	if !matches {
		want = r.FieldOutcome_FIELD_OUTCOME_REFUSED
	}
	for _, field := range effect.Fields {
		if field == nil || field.GetOutcome() != want {
			return contract("work field mismatch")
		}
		switch field.GetField() {
		case r.SettingsField_SETTINGS_FIELD_MEDICAL_CARE:
			if careSeen {
				return contract("work field mismatch")
			}
			careSeen = true
		case r.SettingsField_SETTINGS_FIELD_FOOD_RESTRICTION:
			if foodSeen {
				return contract("duplicate food field")
			}
			foodSeen = true
		case r.SettingsField_SETTINGS_FIELD_WORK:
			if !seen[field.GetWorkTypeDef()] {
				return contract("work field mismatch")
			}
			delete(seen, field.GetWorkTypeDef())
		case r.SettingsField_SETTINGS_FIELD_ALLOWED_AREA:
			if areaSeen {
				return contract("work field mismatch")
			}
			areaSeen = true
		case r.SettingsField_SETTINGS_FIELD_SCHEDULE:
			if scheduleSeen {
				return contract("work field mismatch")
			}
			scheduleSeen = true
		default:
			return contract("work field mismatch")
		}
	}
	if len(seen) != 0 || !areaSeen || !scheduleSeen || !foodSeen || !careSeen {
		return contract("work field mismatch")
	}
	return nil
}
func workReceipt(v *r.Receipt, w WorkAttempt) error {
	if v == nil || buildingUnknown(v) != nil || !proto.Equal(v.Attempt, w.Attempt) || buildingContext(v.AdmittedContext, w.Identity, w.Generation, true) != nil {
		return contract("work admission mismatch")
	}
	switch out := v.Outcome.(type) {
	case *r.Receipt_Applied:
		return workEffect(out.Applied.GetObserved(), w.Work, true)
	case *r.Receipt_Uncertain:
		if out.Uncertain == nil {
			return contract("work uncertainty missing")
		}
		if out.Uncertain.LastObserved != nil {
			return workEffect(out.Uncertain.LastObserved, w.Work, true)
		}
		return nil
	default:
		return contract("unsupported work receipt")
	}
}
func (client *Client) LookupWorkAssignment(ctx context.Context, w WorkAttempt) (*r.LookupReply, Result, error) {
	if err := validWorkAttempt(w); err != nil {
		return nil, Result{}, err
	}
	reply := &r.LookupReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/receipts_lookup", &r.LookupRequest{Identity: w.Identity, Attempt: w.Attempt}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown work lookup fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	switch v := reply.Outcome.(type) {
	case *r.LookupReply_Receipt:
		err = workReceipt(v.Receipt, w)
	case *r.LookupReply_Unknown:
		err = buildingContext(v.Unknown.GetContext(), w.Identity, 0, false)
	case *r.LookupReply_InFlight:
		if v.InFlight == nil || !proto.Equal(v.InFlight.Attempt, w.Attempt) {
			return nil, raw, contract("work in-flight mismatch")
		}
		err = buildingContext(v.InFlight.AdmittedContext, w.Identity, w.Generation, true)
	default:
		err = contract("work lookup outcome missing")
	}
	return reply, raw, err
}
func (client *Client) ObserveWorkAssignment(ctx context.Context, w WorkAttempt, admitted *r.Receipt) (*r.ProgressReply, Result, error) {
	if validWorkAttempt(w) != nil || workReceipt(admitted, w) != nil {
		return nil, Result{}, contract("work observation admission mismatch")
	}
	reply := &r.ProgressReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/receipts_observe_progress", &r.ProgressRequest{Identity: w.Identity, Attempt: w.Attempt}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown work progress fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	v := reply.GetProgress()
	if v == nil || !proto.Equal(v.Attempt, w.Attempt) || buildingContext(v.Context, w.Identity, 0, false) != nil || v.Context.GetTick() < admitted.AdmittedContext.GetTick() {
		return nil, raw, contract("work progress scope mismatch")
	}
	switch out := v.Effect.(type) {
	case *r.Progress_Unknown:
		if out.Unknown == nil {
			return nil, raw, contract("missing work uncertainty")
		}
	case *r.Progress_Completed:
		if !v.GetCompleteInspection() {
			return nil, raw, contract("incomplete work completion")
		}
		err = workEffect(out.Completed.GetEvidence(), w.Work, true)
	case *r.Progress_Unsuccessful:
		if !v.GetCompleteInspection() || out.Unsuccessful.GetReason() != r.UnsuccessfulReason_UNSUCCESSFUL_REASON_OUTCOME_NOT_ACHIEVED {
			return nil, raw, contract("unverified work failure")
		}
		err = workEffect(out.Unsuccessful.GetEvidence(), w.Work, false)
	default:
		err = contract("unsupported work progress")
	}
	return reply, raw, err
}
