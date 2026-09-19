package bridge

import (
	"context"
	"testing"
	"time"

	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"github.com/modelcontextprotocol/go-sdk/mcp"
	"google.golang.org/protobuf/proto"
)

func deconstructionTestAttempt() DeconstructionAttempt {
	return DeconstructionAttempt{pbIdentity(), buildingPre().Attempt, 1, "ruin"}
}
func deconstructionTestEvidence() *r.EffectEvidence {
	return &r.EffectEvidence{Effect: &r.EffectEvidence_Deconstruct{Deconstruct: &r.DeconstructEffect{
		TargetId: proto.String("ruin"), DesignationId: proto.String("designation"), DemolitionObserved: proto.Bool(false),
		Site: &r.SnapshotEvidence{EntityId: proto.String("ruin"), BeforeToken: proto.String("before")},
	}}}
}
func deconstructionTestReceipt() *r.Receipt {
	v := draftTestReceipt()
	v.Outcome = &r.Receipt_Applied{Applied: &r.Applied{Observed: deconstructionTestEvidence()}}
	return v
}
func TestDeconstructionExactWriteWithoutCAS(t *testing.T) {
	client := testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
		if arg.Tool != "rimgovernor/operations_execute" {
			t.Fatal(arg.Tool)
		}
		draftTestRequest(t, arg, &o.ExecuteRequest{Precondition: buildingPre(), Operation: deconstructionOperation("ruin")})
		return pbResult(&o.ExecuteReply{Outcome: &o.ExecuteReply_Receipt{Receipt: deconstructionTestReceipt()}}), nil
	}}, time.Second)
	writer, _ := NewDeconstructionWriter(client)
	if _, _, err := writer.ApplyDeconstruction(context.Background(), buildingPre(), "ruin"); err != nil {
		t.Fatal(err)
	}
	if deconstructionOperation("ruin").GetDeconstruct().Target.ExpectedSnapshotToken != nil {
		t.Fatal("deconstruction must not send CAS")
	}
	for name, edit := range map[string]func(*r.Receipt){
		"attempt":     func(v *r.Receipt) { v.Attempt.AttemptId = proto.Uint64(9) },
		"generation":  func(v *r.Receipt) { v.AdmittedContext.NativeGeneration = proto.Uint64(9) },
		"target":      func(v *r.Receipt) { v.GetApplied().Observed.GetDeconstruct().TargetId = proto.String("other") },
		"designation": func(v *r.Receipt) { v.GetApplied().Observed.GetDeconstruct().DesignationId = nil },
		"unknown": func(v *r.Receipt) {
			v.GetApplied().Observed.GetDeconstruct().ProtoReflect().SetUnknown([]byte{0x38, 1})
		},
		"site unknown": func(v *r.Receipt) {
			v.GetApplied().Observed.GetDeconstruct().Site.ProtoReflect().SetUnknown([]byte{0x38, 1})
		},
		"site target":         func(v *r.Receipt) { v.GetApplied().Observed.GetDeconstruct().Site.EntityId = proto.String("other") },
		"demolition presence": func(v *r.Receipt) { v.GetApplied().Observed.GetDeconstruct().DemolitionObserved = nil },
		"worker":              func(v *r.Receipt) { v.GetApplied().Observed.GetDeconstruct().WorkerIds = []string{""} },
		"wrong effect":        func(v *r.Receipt) { v.GetApplied().Observed = draftTestEvidence() },
	} {
		t.Run(name, func(t *testing.T) {
			v := deconstructionTestReceipt()
			edit(v)
			if deconstructionReceipt(v, deconstructionTestAttempt()) == nil {
				t.Fatal("invalid receipt accepted")
			}
		})
	}
}
func TestDeconstructionCompletionRequiresNativeJobEvidence(t *testing.T) {
	expected := deconstructionTestAttempt()
	progress := &r.Progress{Attempt: expected.Attempt, Context: buildingAdmission().AdmittedContext, CompleteInspection: proto.Bool(true), Effect: &r.Progress_Completed{Completed: &r.CompletedEffect{Evidence: deconstructionTestEvidence()}}}
	if deconstructionProgress(progress, expected, nil) == nil {
		t.Fatal("disappearance treated as demolition")
	}
	progress.GetCompleted().Evidence.GetDeconstruct().DemolitionObserved = proto.Bool(true)
	if err := deconstructionProgress(progress, expected, nil); err != nil {
		t.Fatal(err)
	}
	progress.CompleteInspection = proto.Bool(false)
	if deconstructionProgress(progress, expected, nil) == nil {
		t.Fatal("incomplete inspection accepted")
	}
}
func TestDeconstructionLookupAndProgress(t *testing.T) {
	expected := deconstructionTestAttempt()
	receipt := deconstructionTestReceipt()
	client := testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
		switch arg.Tool {
		case "rimgovernor/receipts_lookup":
			draftTestRequest(t, arg, &r.LookupRequest{Identity: expected.Identity, Attempt: expected.Attempt})
			return pbResult(&r.LookupReply{Outcome: &r.LookupReply_Receipt{Receipt: receipt}}), nil
		case "rimgovernor/receipts_observe_progress":
			draftTestRequest(t, arg, &r.ProgressRequest{Identity: expected.Identity, Attempt: expected.Attempt})
			return pbResult(&r.ProgressReply{Outcome: &r.ProgressReply_Progress{Progress: &r.Progress{Attempt: expected.Attempt, Context: buildingAdmission().AdmittedContext, CompleteInspection: proto.Bool(true), Effect: &r.Progress_Pending{Pending: &r.PendingEffect{Evidence: deconstructionTestEvidence()}}}}}), nil
		default:
			t.Fatalf("unexpected tool %s", arg.Tool)
			return nil, nil
		}
	}}, time.Second)
	if _, _, err := client.LookupDeconstruction(context.Background(), expected); err != nil {
		t.Fatal(err)
	}
	if _, _, err := client.ObserveDeconstructionProgress(context.Background(), expected, receipt); err != nil {
		t.Fatal(err)
	}
}
func TestReleaseDeconstructionsValidatesCount(t *testing.T) {
	for _, count := range []int32{0, 2, -1} {
		t.Run(string(rune('a'+count+1)), func(t *testing.T) {
			client := testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
				draftTestRequest(t, arg, &o.ExecuteRequest{Precondition: buildingPre(), Operation: &o.Operation{Command: &o.Operation_ReleaseDeconstructions{ReleaseDeconstructions: &o.ReleaseDeconstructions{}}}})
				v := draftTestReceipt()
				v.GetApplied().Observed = &r.EffectEvidence{Effect: &r.EffectEvidence_ReleaseDeconstructions{ReleaseDeconstructions: &r.ReleaseDeconstructionsEffect{ReleasedCount: proto.Int32(count)}}}
				return pbResult(&o.ExecuteReply{Outcome: &o.ExecuteReply_Receipt{Receipt: v}}), nil
			}}, time.Second)
			writer, _ := NewDeconstructionWriter(client)
			_, _, err := writer.ReleaseDeconstructions(context.Background(), buildingPre())
			if (err != nil) != (count < 0) {
				t.Fatal(count, err)
			}
		})
	}
}
