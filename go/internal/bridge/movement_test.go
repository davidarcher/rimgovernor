package bridge

import (
	"context"
	"errors"
	"testing"
	"time"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"github.com/modelcontextprotocol/go-sdk/mcp"
	"google.golang.org/protobuf/proto"
)

func movementTestCommand() *o.MovePawn {
	return &o.MovePawn{Pawn: draftTestPawn(), Destination: &c.Cell{X: proto.Int32(4), Z: proto.Int32(5)}}
}
func movementTestAttempt() MovementAttempt {
	d := draftTestAttempt()
	return MovementAttempt{d.Identity, d.Attempt, d.NativeGeneration, d.PawnID, movementTestCommand().Destination}
}
func movementTestReceipt(noChange bool) *r.Receipt {
	v := draftTestReceipt()
	j := draftTestJob()
	j.TargetA = &r.JobTarget{Target: &r.JobTarget_Cell{Cell: movementTestCommand().Destination}}
	if noChange {
		j.Issued = proto.Bool(false)
		v.Outcome = &r.Receipt_NoChange{NoChange: &r.NoChange{Observed: &r.EffectEvidence{Effect: &r.EffectEvidence_Job{Job: j}}}}
	} else {
		j.JobId = proto.Int32(42)
		j.JobDef = proto.String("Goto")
		v.GetApplied().Observed = &r.EffectEvidence{Effect: &r.EffectEvidence_Job{Job: j}}
	}
	return v
}
func movementTestProgress(noChange bool) *r.Progress {
	j := proto.Clone(draftObserved(movementTestReceipt(noChange))).(*r.JobEffect)
	j.Issued = proto.Bool(false)
	return &r.Progress{Attempt: buildingPre().Attempt, Context: buildingAdmission().AdmittedContext, CompleteInspection: proto.Bool(true), Effect: &r.Progress_Completed{Completed: &r.CompletedEffect{Evidence: &r.EffectEvidence{Effect: &r.EffectEvidence_Job{Job: j}}}}}
}
func TestMovementFixedSDKPreviewExecuteLookupProgress(t *testing.T) {
	for _, noChange := range []bool{false, true} {
		t.Run(map[bool]string{false: "issued", true: "already there"}[noChange], func(t *testing.T) {
			calls := 0
			client := testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
				calls++
				switch arg.Tool {
				case "rimgovernor/operations_preview":
					draftTestRequest(t, arg, &o.PreviewRequest{Identity: pbIdentity(), Operation: movementOperation(movementTestCommand())})
					return pbResult(&o.PreviewReply{Outcome: &o.PreviewReply_Evaluated{Evaluated: &o.PreviewEvaluation{Context: buildingAdmission().AdmittedContext, Accepted: proto.Bool(true), Projected: &r.EffectEvidence{Effect: &r.EffectEvidence_Job{Job: &r.JobEffect{PawnId: proto.String("pawn"), JobDef: proto.String("Goto"), TargetA: &r.JobTarget{Target: &r.JobTarget_Cell{Cell: movementTestCommand().Destination}}, CanTry: proto.Bool(true), Issued: proto.Bool(false), Verified: proto.Bool(false)}}}}}}), nil
				case "rimgovernor/operations_execute":
					draftTestRequest(t, arg, &o.ExecuteRequest{Precondition: buildingPre(), Operation: movementOperation(movementTestCommand())})
					return pbResult(&o.ExecuteReply{Outcome: &o.ExecuteReply_Receipt{Receipt: movementTestReceipt(noChange)}}), nil
				case "rimgovernor/receipts_lookup":
					draftTestRequest(t, arg, &r.LookupRequest{Identity: pbIdentity(), Attempt: buildingPre().Attempt})
					return pbResult(&r.LookupReply{Outcome: &r.LookupReply_Receipt{Receipt: movementTestReceipt(noChange)}}), nil
				case "rimgovernor/receipts_observe_progress":
					draftTestRequest(t, arg, &r.ProgressRequest{Identity: pbIdentity(), Attempt: buildingPre().Attempt})
					return pbResult(&r.ProgressReply{Outcome: &r.ProgressReply_Progress{Progress: movementTestProgress(noChange)}}), nil
				}
				t.Fatal(arg.Tool)
				return nil, nil
			}}, time.Second)
			if _, _, err := client.PreviewMovement(context.Background(), pbIdentity(), movementTestCommand()); err != nil {
				t.Fatal(err)
			}
			control, _ := NewMovementControl(client)
			receipt, _, err := control.MovePawn(context.Background(), buildingPre(), movementTestCommand())
			if err != nil {
				t.Fatal(err)
			}
			if _, _, err = client.LookupMovementAttempt(context.Background(), movementTestAttempt()); err != nil {
				t.Fatal(err)
			}
			if _, _, err = client.ObserveMovementProgress(context.Background(), movementTestAttempt(), receipt.GetReceipt()); err != nil {
				t.Fatal(err)
			}
			if calls != 4 {
				t.Fatal("unexpected retry", calls)
			}
		})
	}
}
func TestMovementReceiptAndCompletionCorrelation(t *testing.T) {
	for name, edit := range map[string]func(*r.Receipt){"pawn": func(v *r.Receipt) { draftObserved(v).PawnId = proto.String("other") }, "destination": func(v *r.Receipt) { draftObserved(v).TargetA.GetCell().X = proto.Int32(9) }, "generation": func(v *r.Receipt) { v.AdmittedContext.NativeGeneration = proto.Uint64(2) }, "job absent": func(v *r.Receipt) { draftObserved(v).JobId = nil }, "unverified": func(v *r.Receipt) { draftObserved(v).Verified = proto.Bool(false) }, "other operation": func(v *r.Receipt) { draftObserved(v).AutoDrafted = proto.Bool(true) }} {
		t.Run(name, func(t *testing.T) {
			v := movementTestReceipt(false)
			edit(v)
			if err := movementReceipt(v, movementTestAttempt()); !errors.Is(err, ErrContract) {
				t.Fatal(err)
			}
		})
	}
	for name, edit := range map[string]func(*r.Progress){"job": func(v *r.Progress) { v.GetCompleted().Evidence.GetJob().JobId = proto.Int32(99) }, "claim": func(v *r.Progress) { v.GetCompleted().Evidence.GetJob().DraftClaimId = proto.String("different") }, "inspection": func(v *r.Progress) { v.CompleteInspection = nil }, "generation": func(v *r.Progress) { v.Context.NativeGeneration = proto.Uint64(2) }, "issued": func(v *r.Progress) { v.GetCompleted().Evidence.GetJob().Issued = proto.Bool(true) }} {
		t.Run(name, func(t *testing.T) {
			v := movementTestProgress(false)
			edit(v)
			if err := movementProgress(v, movementTestAttempt(), movementTestReceipt(false)); !errors.Is(err, ErrContract) {
				t.Fatal(err)
			}
		})
	}
	if err := movementProgress(movementTestProgress(false), movementTestAttempt(), nil); !errors.Is(err, ErrContract) {
		t.Fatal("completion without original receipt", err)
	}
}
func TestMovementPendingInterruptionAndUnknownSDK(t *testing.T) {
	for _, kind := range []string{"pending", "interrupted", "unknown", "absent"} {
		t.Run(kind, func(t *testing.T) {
			v := movementTestProgress(false)
			e := v.GetCompleted().Evidence
			switch kind {
			case "pending":
				v.Effect = &r.Progress_Pending{Pending: &r.PendingEffect{Evidence: e}}
			case "interrupted":
				e.GetJob().DraftClaimId = nil
				e.GetJob().Drafted = proto.Bool(false)
				e.GetJob().Verified = proto.Bool(false)
				v.Context.NativeGeneration = proto.Uint64(2)
				v.Effect = &r.Progress_Unsuccessful{Unsuccessful: &r.UnsuccessfulEffect{Reason: r.UnsuccessfulReason_UNSUCCESSFUL_REASON_INTERRUPTED.Enum(), Evidence: e}}
			case "unknown":
				v.CompleteInspection = proto.Bool(false)
				v.Effect = &r.Progress_Unknown{Unknown: &r.UnknownEffect{Reason: proto.String("unavailable")}}
			case "absent":
				v.Effect = &r.Progress_Absent{Absent: &r.AbsentEffect{InspectionToken: proto.String("inspection")}}
			}
			client := testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, _ nativeArgument) (*mcp.CallToolResult, error) {
				return pbResult(&r.ProgressReply{Outcome: &r.ProgressReply_Progress{Progress: v}}), nil
			}}, time.Second)
			got, _, err := client.ObserveMovementProgress(context.Background(), movementTestAttempt(), movementTestReceipt(false))
			if err != nil || !proto.Equal(got.GetProgress(), v) {
				t.Fatal(got, err)
			}
		})
	}
}
func TestMovementMalformedRequestNeverCalls(t *testing.T) {
	calls := 0
	client := testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, _ nativeArgument) (*mcp.CallToolResult, error) { calls++; return nil, nil }}, time.Second)
	for _, command := range []*o.MovePawn{nil, {Pawn: draftTestPawn()}, {Pawn: draftTestPawn(), Destination: &c.Cell{X: proto.Int32(0)}}, {Pawn: draftTestPawn(), Destination: &c.Cell{X: proto.Int32(-1), Z: proto.Int32(0)}}} {
		if _, _, err := client.PreviewMovement(context.Background(), pbIdentity(), command); !errors.Is(err, ErrContract) {
			t.Fatal(err)
		}
	}
	if calls != 0 {
		t.Fatal(calls)
	}
}

func TestMovementLookupUnknownInflightAndRefusal(t *testing.T) {
	for _, kind := range []string{"unknown", "inflight", "failure"} {
		t.Run(kind, func(t *testing.T) {
			reply := &r.LookupReply{}
			switch kind {
			case "unknown":
				reply.Outcome = &r.LookupReply_Unknown{Unknown: &r.UnknownAttempt{Context: buildingAdmission().AdmittedContext}}
			case "inflight":
				reply.Outcome = &r.LookupReply_InFlight{InFlight: &r.InFlight{Attempt: buildingPre().Attempt, AdmittedContext: buildingAdmission().AdmittedContext}}
			case "failure":
				reply.Outcome = &r.LookupReply_Failure{Failure: &c.Failure{Code: c.FailureCode_FAILURE_CODE_UNAVAILABLE.Enum(), Detail: proto.String("unavailable")}}
			}
			client := testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, _ nativeArgument) (*mcp.CallToolResult, error) { return pbResult(reply), nil }}, time.Second)
			got, _, err := client.LookupMovementAttempt(context.Background(), movementTestAttempt())
			if kind == "failure" {
				var failure *NativeFailure
				if !errors.As(err, &failure) {
					t.Fatal(err)
				}
			} else if err != nil || !proto.Equal(got, reply) {
				t.Fatal(got, err)
			}
		})
	}
}

func TestMovementUncertainWithoutReadbackCompletes(t *testing.T) {
	admitted := movementTestReceipt(false)
	admitted.Outcome = &r.Receipt_Uncertain{Uncertain: &r.Uncertain{Detail: proto.String("post-write readback unavailable")}}
	for _, bad := range []string{"", "destination", "key", "generation", "corrupted receipt", "missing receipt"} {
		t.Run(bad, func(t *testing.T) {
			original := proto.Clone(admitted).(*r.Receipt)
			progress := movementTestProgress(false)
			switch bad {
			case "destination":
				progress.GetCompleted().Evidence.GetJob().TargetA.GetCell().X = proto.Int32(12)
			case "key":
				progress.Attempt.ActionId = proto.String("wrong")
			case "generation":
				progress.Context.NativeGeneration = proto.Uint64(7)
			case "corrupted receipt":
				original.Attempt.AttemptId = proto.Uint64(777)
			case "missing receipt":
				original = nil
			}
			calls := 0
			client := testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, _ nativeArgument) (*mcp.CallToolResult, error) {
				calls++
				return pbResult(&r.ProgressReply{Outcome: &r.ProgressReply_Progress{Progress: progress}}), nil
			}}, time.Second)
			reply, _, err := client.ObserveMovementProgress(context.Background(), movementTestAttempt(), original)
			if bad == "" {
				if err != nil || reply.GetProgress().GetCompleted() == nil || calls != 1 {
					t.Fatal(reply, calls, err)
				}
			} else if !errors.Is(err, ErrContract) {
				t.Fatal(bad, err)
			}
		})
	}
}
