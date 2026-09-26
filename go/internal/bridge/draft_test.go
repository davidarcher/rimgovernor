package bridge

import (
	"context"
	"encoding/json"
	"errors"
	"sync/atomic"
	"testing"
	"time"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"github.com/modelcontextprotocol/go-sdk/mcp"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
)

func draftTestAttempt() DraftAttempt {
	return DraftAttempt{Identity: pbIdentity(), Attempt: buildingPre().Attempt, NativeGeneration: 1, PawnID: "pawn"}
}
func draftTestPawn() *o.EntityPrecondition {
	return &o.EntityPrecondition{EntityId: proto.String("pawn"), ExpectedSnapshotToken: proto.String("before")}
}
func draftTestJob() *r.JobEffect {
	return &r.JobEffect{PawnId: proto.String("pawn"), Drafted: proto.Bool(true), Issued: proto.Bool(true), Verified: proto.Bool(true), DraftClaimId: proto.String("claim"), ResultingSnapshotToken: proto.String("after")}
}
func draftTestEvidence() *r.EffectEvidence {
	return &r.EffectEvidence{Effect: &r.EffectEvidence_Job{Job: draftTestJob()}}
}
func draftTestReceipt() *r.Receipt {
	return &r.Receipt{Attempt: buildingPre().Attempt, AdmittedContext: buildingAdmission().AdmittedContext, Outcome: &r.Receipt_Applied{Applied: &r.Applied{Observed: draftTestEvidence()}}}
}
func draftTestRelease() *o.ReleaseOwnedDraftRequest {
	return &o.ReleaseOwnedDraftRequest{Identity: pbIdentity(), Pawn: draftTestPawn(), ExpectedClaimId: proto.String("claim")}
}
func draftTestRequest(t *testing.T, arg nativeArgument, expected proto.Message) {
	t.Helper()
	var outer struct {
		Request string `json:"request"`
	}
	if err := json.Unmarshal(arg.Arguments, &outer); err != nil {
		t.Fatal(err)
	}
	actual := expected.ProtoReflect().New().Interface()
	if err := protojson.Unmarshal([]byte(outer.Request), actual); err != nil || !proto.Equal(actual, expected) {
		t.Fatal(actual, err)
	}
}
func TestDraftFixedCapabilityAndReplay(t *testing.T) {
	pre := buildingPre()
	pawn := draftTestPawn()
	calls := 0
	client := testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
		calls++
		if arg.Tool != "rimgovernor/operations_execute" {
			t.Fatal(arg.Tool)
		}
		draftTestRequest(t, arg, &o.ExecuteRequest{Precondition: buildingPre(), Operation: draftOperation(draftTestPawn())})
		return pbResult(&o.ExecuteReply{Outcome: &o.ExecuteReply_Receipt{Receipt: draftTestReceipt()}}), nil
	}}, time.Second)
	control, err := NewDraftControl(client)
	if err != nil {
		t.Fatal(err)
	}
	for range 2 {
		reply, raw, err := control.DraftPawn(context.Background(), pre, pawn)
		if err != nil || len(raw.Envelope) == 0 || !proto.Equal(reply.GetReceipt(), draftTestReceipt()) {
			t.Fatal(reply, err)
		}
	}
	if calls != 2 {
		t.Fatal(calls)
	} // Explicit caller replay; the adapter never retries.
	if _, err := NewDraftControl(nil); err == nil {
		t.Fatal("nil capability")
	}
}
func TestDraftReceiptCorrelationAndUncertainty(t *testing.T) {
	for name, edit := range map[string]func(*r.Receipt){"attempt": func(v *r.Receipt) { v.Attempt.AttemptId = proto.Uint64(2) }, "world": func(v *r.Receipt) { v.AdmittedContext.Identity.LoadToken = proto.String("other") }, "generation": func(v *r.Receipt) { v.AdmittedContext.NativeGeneration = proto.Uint64(2) }, "pawn": func(v *r.Receipt) { v.GetApplied().Observed.GetJob().PawnId = proto.String("other") }, "claim absent": func(v *r.Receipt) { v.GetApplied().Observed.GetJob().DraftClaimId = nil }, "unverified": func(v *r.Receipt) { v.GetApplied().Observed.GetJob().Verified = nil }, "unrelated effect": func(v *r.Receipt) { v.GetApplied().Observed = buildingEffect() }, "job order": func(v *r.Receipt) { v.GetApplied().Observed.GetJob().JobDef = proto.String("Attack") }, "unknown": func(v *r.Receipt) { v.GetApplied().Observed.GetJob().ProtoReflect().SetUnknown([]byte{0x18, 1}) }} {
		t.Run(name, func(t *testing.T) {
			v := draftTestReceipt()
			edit(v)
			if err := draftReceipt(v, draftTestAttempt()); err == nil {
				t.Fatal("accepted mismatch")
			}
		})
	}
	v := draftTestReceipt()
	v.GetApplied().Observed.GetJob().Issued = proto.Bool(false) // adopted player draft
	if err := draftReceipt(v, draftTestAttempt()); err != nil {
		t.Fatal(err)
	}
	job := draftTestJob()
	job.Issued = proto.Bool(true)
	v.Outcome = &r.Receipt_NoChange{NoChange: &r.NoChange{Observed: &r.EffectEvidence{Effect: &r.EffectEvidence_Job{Job: job}}}}
	if err := draftReceipt(v, draftTestAttempt()); err == nil {
		t.Fatal("no-change claimed an issued draft")
	}
	job.Issued = proto.Bool(false)
	if err := draftReceipt(v, draftTestAttempt()); err != nil {
		t.Fatal(err)
	}
	for _, evidence := range []*r.EffectEvidence{nil, {Effect: &r.EffectEvidence_Job{Job: &r.JobEffect{PawnId: proto.String("pawn"), DraftClaimId: proto.String("claim"), Verified: proto.Bool(false)}}}} {
		v.Outcome = &r.Receipt_Uncertain{Uncertain: &r.Uncertain{LastObserved: evidence}}
		if err := draftReceipt(v, draftTestAttempt()); err != nil {
			t.Fatal(err)
		}
	}
}
func TestDraftPreviewFixedAndNonAuthorizing(t *testing.T) {
	for _, accepted := range []bool{false, true} {
		client := testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
			if arg.Tool != "rimgovernor/operations_preview" {
				t.Fatal(arg.Tool)
			}
			draftTestRequest(t, arg, &o.PreviewRequest{Identity: pbIdentity(), Operation: draftOperation(draftTestPawn())})
			return pbResult(&o.PreviewReply{Outcome: &o.PreviewReply_Evaluated{Evaluated: &o.PreviewEvaluation{Context: buildingAdmission().AdmittedContext, Accepted: proto.Bool(accepted), Projected: &r.EffectEvidence{Effect: &r.EffectEvidence_Job{Job: &r.JobEffect{PawnId: proto.String("pawn"), Drafted: proto.Bool(true), CanTry: proto.Bool(accepted), Issued: proto.Bool(false), Verified: proto.Bool(false)}}}}}}), nil
		}}, time.Second)
		reply, _, err := client.PreviewDraft(context.Background(), pbIdentity(), draftTestPawn())
		if err != nil || reply.GetEvaluated().GetAccepted() != accepted {
			t.Fatal(reply, err)
		}
	}
}
func TestDraftLeaseFreeLookupAndProgress(t *testing.T) {
	for _, kind := range []string{"receipt", "inflight", "unknown"} {
		t.Run(kind, func(t *testing.T) {
			reply := &r.LookupReply{}
			switch kind {
			case "receipt":
				reply.Outcome = &r.LookupReply_Receipt{Receipt: draftTestReceipt()}
			case "inflight":
				reply.Outcome = &r.LookupReply_InFlight{InFlight: &r.InFlight{Attempt: buildingPre().Attempt, AdmittedContext: buildingAdmission().AdmittedContext}}
			case "unknown":
				reply.Outcome = &r.LookupReply_Unknown{Unknown: &r.UnknownAttempt{Context: buildingAdmission().AdmittedContext}}
			}
			client := testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
				if arg.Tool != "rimgovernor/receipts_lookup" {
					t.Fatal(arg.Tool)
				}
				draftTestRequest(t, arg, &r.LookupRequest{Identity: pbIdentity(), Attempt: buildingPre().Attempt})
				return pbResult(reply), nil
			}}, time.Second)
			got, _, err := client.LookupDraftAttempt(context.Background(), draftTestAttempt())
			if err != nil || !proto.Equal(got, reply) {
				t.Fatal(got, err)
			}
		})
	}
	// A lost/uncertain receipt without a claim does not permanently block reads.
	admitted := draftTestReceipt()
	admitted.Outcome = &r.Receipt_Uncertain{Uncertain: &r.Uncertain{}}
	v := &r.Progress{Attempt: buildingPre().Attempt, Context: buildingDone().Context, CompleteInspection: proto.Bool(true), Effect: &r.Progress_Completed{Completed: &r.CompletedEffect{Evidence: draftTestEvidence()}}}
	client := testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
		if arg.Tool != "rimgovernor/receipts_observe_progress" {
			t.Fatal(arg.Tool)
		}
		return pbResult(&r.ProgressReply{Outcome: &r.ProgressReply_Progress{Progress: v}}), nil
	}}, time.Second)
	got, _, err := client.ObserveDraftProgress(context.Background(), draftTestAttempt(), admitted)
	if err != nil || !proto.Equal(got.GetProgress(), v) {
		t.Fatal(got, err)
	}
	for name, edit := range map[string]func(*r.Progress){"attempt": func(v *r.Progress) { v.Attempt.AttemptId = proto.Uint64(2) }, "world": func(v *r.Progress) { v.Context.Identity.LoadToken = proto.String("other") }, "tick": func(v *r.Progress) { v.Context.Tick = proto.Int64(0) }, "incomplete": func(v *r.Progress) { v.CompleteInspection = nil }, "claim": func(v *r.Progress) { v.GetCompleted().Evidence.GetJob().DraftClaimId = proto.String("replacement") }, "pawn": func(v *r.Progress) { v.GetCompleted().Evidence.GetJob().PawnId = proto.String("other") }} {
		t.Run(name, func(t *testing.T) {
			changed := proto.Clone(v).(*r.Progress)
			edit(changed)
			if err := draftProgress(changed, draftTestAttempt(), draftTestReceipt()); err == nil {
				t.Fatal("accepted bad progress")
			}
		})
	}
	v.Effect = &r.Progress_Unknown{Unknown: &r.UnknownEffect{}}
	v.CompleteInspection = proto.Bool(false)
	if err := draftProgress(v, draftTestAttempt(), admitted); err != nil {
		t.Fatal(err)
	}
	v.Effect = &r.Progress_Pending{Pending: &r.PendingEffect{}}
	if err := draftProgress(v, draftTestAttempt(), admitted); err != nil {
		t.Fatal(err)
	}
	job := draftTestJob()
	job.DraftClaimId = nil
	job.Verified = proto.Bool(false)
	v.Effect = &r.Progress_Unsuccessful{Unsuccessful: &r.UnsuccessfulEffect{Reason: r.UnsuccessfulReason_UNSUCCESSFUL_REASON_INTERRUPTED.Enum(), Evidence: &r.EffectEvidence{Effect: &r.EffectEvidence_Job{Job: job}}}}
	v.CompleteInspection = proto.Bool(true)
	if err := draftProgress(v, draftTestAttempt(), draftTestReceipt()); err != nil {
		t.Fatal(err)
	}
}
func TestDraftCleanupExactClaimNoLease(t *testing.T) {
	for _, kind := range []string{"released", "already", "uncertain"} {
		t.Run(kind, func(t *testing.T) {
			request := draftTestRelease()
			job := draftTestJob()
			job.Drafted = proto.Bool(false)
			job.Issued = proto.Bool(kind == "released")
			value := &o.DraftRelease{Request: proto.Clone(request).(*o.ReleaseOwnedDraftRequest), Context: buildingDone().Context, Observed: job}
			reply := &o.ReleaseOwnedDraftReply{}
			switch kind {
			case "released":
				reply.Outcome = &o.ReleaseOwnedDraftReply_Released{Released: value}
			case "already":
				reply.Outcome = &o.ReleaseOwnedDraftReply_AlreadyReleased{AlreadyReleased: value}
			case "uncertain":
				reply.Outcome = &o.ReleaseOwnedDraftReply_Uncertain{Uncertain: &o.DraftReleaseUncertain{Request: request, Context: value.Context}}
			}
			client := testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
				if arg.Tool != "rimgovernor/operations_release_owned_draft" {
					t.Fatal(arg.Tool)
				}
				draftTestRequest(t, arg, request)
				return pbResult(reply), nil
			}}, time.Second)
			cleanup, _ := NewDraftCleanup(client)
			got, _, err := cleanup.ReleaseOwnedDraft(context.Background(), request)
			if err != nil || !proto.Equal(got, reply) {
				t.Fatal(got, err)
			}
			if kind == "released" {
				value.Request.ExpectedClaimId = proto.String("replacement")
				if err := draftRelease(value, request, true); err == nil {
					t.Fatal("changed request echo accepted")
				}
			}
		})
	}
}
func TestDraftFailureLostReplyAndCancellation(t *testing.T) {
	for _, kind := range []string{"conflict", "lost", "cancel"} {
		t.Run(kind, func(t *testing.T) {
			var calls atomic.Int32
			client := testClient(t, &testServer{schema: protoSchema, handler: func(ctx context.Context, _ nativeArgument) (*mcp.CallToolResult, error) {
				calls.Add(1)
				if kind == "lost" {
					return nil, errors.New("lost reply")
				}
				if kind == "cancel" {
					<-ctx.Done()
					return nil, ctx.Err()
				}
				reply := pbResult(&o.ExecuteReply{Outcome: &o.ExecuteReply_Failure{Failure: &c.Failure{Code: c.FailureCode_FAILURE_CODE_ATTEMPT_CONFLICT.Enum()}}})
				reply.IsError = true
				return reply, nil
			}}, time.Second)
			control, _ := NewDraftControl(client)
			ctx, cancel := context.WithTimeout(context.Background(), 30*time.Millisecond)
			defer cancel()
			reply, _, err := control.DraftPawn(ctx, buildingPre(), draftTestPawn())
			if err == nil || calls.Load() != 1 {
				t.Fatal(reply, err, calls.Load())
			}
			if kind == "conflict" {
				var failure *NativeFailure
				if !errors.As(err, &failure) {
					t.Fatal(err)
				}
			} else if reply != nil {
				t.Fatal("invented receipt")
			}
		})
	}
}
