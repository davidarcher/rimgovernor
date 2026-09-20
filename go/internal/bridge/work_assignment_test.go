package bridge

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"github.com/modelcontextprotocol/go-sdk/mcp"
	"google.golang.org/protobuf/proto"
	"testing"
	"time"
)

func workTestAssignment() domain.WorkAssignment {
	w, _ := domain.NewWorkAssignment("pawn", "before", true, []domain.WorkSetting{{Definition: "Cooking", Priority: 1}})
	return w
}
func workTestEffect() *r.EffectEvidence {
	return &r.EffectEvidence{Effect: &r.EffectEvidence_Settings{Settings: &r.SettingsEffect{Snapshot: &r.SnapshotEvidence{EntityId: proto.String("pawn"), BeforeToken: proto.String("before"), AfterToken: proto.String("after")}, Fields: []*r.FieldResult{{Field: r.SettingsField_SETTINGS_FIELD_WORK.Enum(), Outcome: r.FieldOutcome_FIELD_OUTCOME_APPLIED.Enum(), Entry: &r.FieldResult_WorkTypeDef{WorkTypeDef: "Cooking"}}}}}}
}
func workTestAttempt() WorkAttempt {
	return WorkAttempt{pbIdentity(), buildingPre().Attempt, 1, workTestAssignment()}
}
func workTestReceipt() *r.Receipt {
	v := draftTestReceipt()
	v.Outcome = &r.Receipt_Applied{Applied: &r.Applied{Observed: workTestEffect()}}
	return v
}
func TestWorkFixedCapabilityAndReceiptCorrelation(t *testing.T) {
	calls := 0
	client := testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
		calls++
		if arg.Tool != "rimgovernor/operations_execute" {
			t.Fatal(arg.Tool)
		}
		draftTestRequest(t, arg, &op.ExecuteRequest{Precondition: buildingPre(), Operation: &op.Operation{Command: &op.Operation_PatchPawn{PatchPawn: &op.PatchPawn{Pawn: &op.EntityPrecondition{EntityId: proto.String("pawn"), ExpectedSnapshotToken: proto.String("before")}, Work: []*op.WorkPriority{{WorkTypeDef: proto.String("Cooking"), Priority: proto.Int32(1)}}}}}})
		return pbResult(&op.ExecuteReply{Outcome: &op.ExecuteReply_Receipt{Receipt: workTestReceipt()}}), nil
	}}, time.Second)
	writer, err := NewWorkControl(client)
	if err != nil {
		t.Fatal(err)
	}
	if _, _, err = writer.AssignWork(context.Background(), buildingPre(), workTestAssignment()); err != nil || calls != 1 {
		t.Fatal(calls, err)
	}
	for name, edit := range map[string]func(*r.Receipt){
		"attempt": func(v *r.Receipt) { v.Attempt.AttemptId = proto.Uint64(2) },
		"pawn":    func(v *r.Receipt) { v.GetApplied().Observed.GetSettings().Snapshot.EntityId = proto.String("foreign") },
		"before": func(v *r.Receipt) {
			v.GetApplied().Observed.GetSettings().Snapshot.BeforeToken = proto.String("foreign")
		},
		"field": func(v *r.Receipt) {
			v.GetApplied().Observed.GetSettings().Fields[0].Field = r.SettingsField_SETTINGS_FIELD_MEDICAL_CARE.Enum()
		},
		"priority target": func(v *r.Receipt) {
			v.GetApplied().Observed.GetSettings().Fields[0].Entry = &r.FieldResult_WorkTypeDef{WorkTypeDef: "Doctor"}
		},
		"unknown": func(v *r.Receipt) {
			v.GetApplied().Observed.GetSettings().Fields[0].Outcome = r.FieldOutcome_FIELD_OUTCOME_UNKNOWN.Enum()
		},
	} {
		t.Run(name, func(t *testing.T) {
			v := workTestReceipt()
			edit(v)
			if err := workReceipt(v, workTestAttempt()); err == nil {
				t.Fatal("accepted foreign settings evidence")
			}
		})
	}
}

func TestMedicalCareOperationAndEvidence(t *testing.T) {
	w, err := domain.NewMedicalCareAssignment("pawn", "before", "HerbalOrWorse")
	if err != nil {
		t.Fatal(err)
	}
	patch := workOperation(w).GetPatchPawn()
	if patch.GetMedicalCare() != op.MedicalCare_MEDICAL_CARE_HERBAL_OR_WORSE || len(patch.Work) != 0 {
		t.Fatal(patch)
	}
	effect := workTestEffect()
	effect.GetSettings().Fields = []*r.FieldResult{{Field: r.SettingsField_SETTINGS_FIELD_MEDICAL_CARE.Enum(), Outcome: r.FieldOutcome_FIELD_OUTCOME_APPLIED.Enum()}}
	if err := workEffect(effect, w, true); err != nil {
		t.Fatal(err)
	}
	effect.GetSettings().Fields[0].Field = r.SettingsField_SETTINGS_FIELD_WORK.Enum()
	if err := workEffect(effect, w, true); err == nil {
		t.Fatal("work receipt certified care")
	}
}
