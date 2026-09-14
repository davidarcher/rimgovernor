package bridge

import (
	"context"
	"encoding/json"
	"errors"
	"testing"
	"time"

	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"github.com/modelcontextprotocol/go-sdk/mcp"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
)

func recoveryServicePre() *a.WritePrecondition {
	return &a.WritePrecondition{Identity: pbIdentity(), ExpectedGeneration: proto.Uint64(1), LeaseId: proto.String("lease"), Attempt: &c.AttemptKey{ControllerSessionId: proto.String("controller"), ActionId: proto.String("action"), AttemptId: proto.Uint64(1)}}
}
func recoveryServiceEffectEvidence() *r.EffectEvidence {
	return &r.EffectEvidence{Effect: &r.EffectEvidence_Job{Job: &r.JobEffect{
		PawnId:   proto.String("pawn"),
		JobDef:   proto.String(recoveryServiceJobDef[RecoveryServiceRepair]),
		TargetA:  &r.JobTarget{Target: &r.JobTarget_ThingId{ThingId: "thing"}},
		CanTry:   proto.Bool(true),
		Issued:   proto.Bool(true),
		Verified: proto.Bool(false),
	}}}
}
func recoveryServiceAdmission() *r.Receipt {
	return &r.Receipt{Attempt: recoveryServicePre().Attempt, AdmittedContext: &c.ObservationContext{Identity: pbIdentity(), Tick: proto.Int64(10), NativeGeneration: proto.Uint64(1)}, AuthorizingOwner: &a.Owner{ControllerSessionId: proto.String("controller"), PlayerDirection: proto.Uint64(1)}, Outcome: &r.Receipt_Applied{Applied: &r.Applied{Observed: recoveryServiceEffectEvidence()}}}
}
func recoveryServiceAttemptFixture() RecoveryServiceAttempt {
	admission := recoveryServiceAdmission()
	return RecoveryServiceAttempt{Identity: pbIdentity(), Attempt: recoveryServicePre().Attempt, Owner: admission.AuthorizingOwner, Generation: 1, Pawn: "pawn", Thing: "thing", PawnToken: "pawn-token", ThingToken: "thing-token", Method: RecoveryServiceRepair}
}

func TestPreviewRecoveryServiceAcceptedAndRejections(t *testing.T) {
	valid := &op.PreviewReply{Outcome: &op.PreviewReply_Evaluated{Evaluated: &op.PreviewEvaluation{
		Context:   pbContext(),
		Accepted:  proto.Bool(true),
		Projected: &r.EffectEvidence{Effect: &r.EffectEvidence_Job{Job: &r.JobEffect{PawnId: proto.String("pawn"), JobDef: proto.String(recoveryServiceJobDef[RecoveryServiceRepair]), TargetA: &r.JobTarget{Target: &r.JobTarget_ThingId{ThingId: "thing"}}, CanTry: proto.Bool(true), Issued: proto.Bool(false), Verified: proto.Bool(false), TargetSnapshotToken: proto.String("thing-token")}}},
	}}}
	for _, test := range []struct {
		name   string
		change func(*op.PreviewReply)
		ok     bool
	}{
		{"valid", func(*op.PreviewReply) {}, true},
		{"not accepted", func(v *op.PreviewReply) { v.GetEvaluated().Accepted = proto.Bool(false) }, false},
		{"missing accepted", func(v *op.PreviewReply) { v.GetEvaluated().Accepted = nil }, false},
		{"missing job projection", func(v *op.PreviewReply) { v.GetEvaluated().Projected = &r.EffectEvidence{} }, false},
		{"target mismatch", func(v *op.PreviewReply) {
			v.GetEvaluated().Projected.GetJob().TargetA = &r.JobTarget{Target: &r.JobTarget_ThingId{ThingId: "other"}}
		}, false},
		{"foreign world", func(v *op.PreviewReply) { v.GetEvaluated().Context.Identity.LoadToken = proto.String("other") }, false},
		{"failure outcome", func(v *op.PreviewReply) {
			v.Outcome = &op.PreviewReply_Failure{Failure: &c.Failure{Code: c.FailureCode_FAILURE_CODE_AUTHORITY_REQUIRED.Enum()}}
		}, false},
	} {
		t.Run(test.name, func(t *testing.T) {
			reply := proto.Clone(valid).(*op.PreviewReply)
			test.change(reply)
			s := &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
				if arg.Tool != "rimgovernor/operations_preview" {
					t.Fatal(arg.Tool)
				}
				var outer struct {
					Request string `json:"request"`
				}
				if err := json.Unmarshal(arg.Arguments, &outer); err != nil {
					t.Fatal(err)
				}
				req := &op.PreviewRequest{}
				if err := protojson.Unmarshal([]byte(outer.Request), req); err != nil {
					t.Fatal(err)
				}
				service := req.Operation.GetRecoverService()
				if service.GetPawn().GetEntityId() != "pawn" || service.GetTarget().GetEntityId() != "thing" || service.GetMethod() != op.ServiceMethod_SERVICE_METHOD_REPAIR {
					t.Fatal("request correlation lost", req)
				}
				return pbResult(reply), nil
			}}
			client := testClient(t, s, time.Second)
			_, raw, err := client.PreviewRecoveryService(context.Background(), pbIdentity(), "pawn", "pawn-token", "thing", "thing-token", RecoveryServiceRepair)
			if test.ok {
				if err != nil || len(raw.Envelope) == 0 {
					t.Fatal("expected accepted preview", err)
				}
				return
			}
			if !errors.Is(err, ErrContract) && !errors.Is(err, ErrRefused) {
				t.Fatal("expected rejection", err)
			}
		})
	}
}

func TestPreviewRecoveryServiceInvalidInputsNeverDispatch(t *testing.T) {
	s := &testServer{schema: protoSchema}
	client := testClient(t, s, time.Second)
	for _, args := range []struct {
		pawn, pawnToken, thing, thingToken string
		method                             RecoveryServiceMethod
	}{
		{"", "pawn-token", "thing", "thing-token", RecoveryServiceRepair},
		{"pawn", "", "thing", "thing-token", RecoveryServiceRepair},
		{"pawn", "pawn-token", "", "thing-token", RecoveryServiceRepair},
		{"pawn", "pawn-token", "thing", "", RecoveryServiceRepair},
		{"pawn", "pawn-token", "pawn", "thing-token", RecoveryServiceRepair},
		{"pawn", "pawn-token", "thing", "thing-token", RecoveryServiceUnspecified},
	} {
		if _, _, err := client.PreviewRecoveryService(context.Background(), pbIdentity(), args.pawn, args.pawnToken, args.thing, args.thingToken, args.method); !errors.Is(err, ErrContract) {
			t.Fatal("invalid recovery service command accepted", args, err)
		}
	}
	if len(s.calls) != 0 {
		t.Fatal("invalid input dispatched", s.calls)
	}
}

func TestApplyRecoveryServiceCorrelationAndOwnerMismatch(t *testing.T) {
	s := &testServer{schema: protoSchema, handler: func(_ context.Context, args nativeArgument) (*mcp.CallToolResult, error) {
		if args.Tool != "rimgovernor/operations_execute" {
			t.Fatal(args.Tool)
		}
		var wrapper struct {
			Request string `json:"request"`
		}
		if err := json.Unmarshal(args.Arguments, &wrapper); err != nil {
			t.Fatal(err)
		}
		req := &op.ExecuteRequest{}
		if err := protojson.Unmarshal([]byte(wrapper.Request), req); err != nil {
			t.Fatal(err)
		}
		if !proto.Equal(req.Precondition, recoveryServicePre()) || req.Operation.GetRecoverService().GetPawn().GetEntityId() != "pawn" {
			t.Fatal("request correlation lost", req)
		}
		return pbResult(&op.ExecuteReply{Outcome: &op.ExecuteReply_Receipt{Receipt: recoveryServiceAdmission()}}), nil
	}}
	writer, err := NewRecoveryServiceWriter(testClient(t, s, time.Second))
	if err != nil {
		t.Fatal(err)
	}
	pre := recoveryServicePre()
	owner := recoveryServiceAdmission().AuthorizingOwner
	reply, raw, err := writer.ApplyRecoveryService(context.Background(), pre, owner, "pawn", "pawn-token", "thing", "thing-token", RecoveryServiceRepair)
	if err != nil || reply.GetReceipt() == nil || len(raw.Envelope) == 0 {
		t.Fatal(err)
	}
	mismatched := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
		admission := recoveryServiceAdmission()
		admission.AuthorizingOwner.ControllerSessionId = proto.String("someone-else")
		return pbResult(&op.ExecuteReply{Outcome: &op.ExecuteReply_Receipt{Receipt: admission}}), nil
	}}
	writer, _ = NewRecoveryServiceWriter(testClient(t, mismatched, time.Second))
	if _, _, err = writer.ApplyRecoveryService(context.Background(), pre, owner, "pawn", "pawn-token", "thing", "thing-token", RecoveryServiceRepair); !errors.Is(err, ErrContract) {
		t.Fatal("owner mismatch accepted", err)
	}
}

func TestApplyRecoveryServiceInvalidInputsNeverDispatch(t *testing.T) {
	s := &testServer{schema: protoSchema}
	writer, _ := NewRecoveryServiceWriter(testClient(t, s, time.Second))
	owner := recoveryServiceAdmission().AuthorizingOwner
	for _, change := range []func(*a.WritePrecondition){
		func(v *a.WritePrecondition) { v.ExpectedGeneration = nil },
		func(v *a.WritePrecondition) { v.ExpectedGeneration = proto.Uint64(0) },
		func(v *a.WritePrecondition) { v.LeaseId = nil },
		func(v *a.WritePrecondition) { v.Attempt.AttemptId = nil },
	} {
		pre := recoveryServicePre()
		change(pre)
		if _, _, err := writer.ApplyRecoveryService(context.Background(), pre, owner, "pawn", "pawn-token", "thing", "thing-token", RecoveryServiceRepair); !errors.Is(err, ErrContract) {
			t.Fatal("invalid precondition accepted", err)
		}
	}
	if _, _, err := writer.ApplyRecoveryService(context.Background(), recoveryServicePre(), owner, "pawn", "pawn-token", "pawn", "thing-token", RecoveryServiceRepair); !errors.Is(err, ErrContract) {
		t.Fatal("pawn/thing collision accepted", err)
	}
	if _, _, err := writer.ApplyRecoveryService(context.Background(), recoveryServicePre(), owner, "pawn", "pawn-token", "thing", "thing-token", RecoveryServiceUnspecified); !errors.Is(err, ErrContract) {
		t.Fatal("unspecified method accepted", err)
	}
	if len(s.calls) != 0 {
		t.Fatal("invalid input dispatched", s.calls)
	}
}

func TestLookupAndObserveRecoveryService(t *testing.T) {
	w := recoveryServiceAttemptFixture()
	admission := recoveryServiceAdmission()
	unknown := &r.LookupReply{Outcome: &r.LookupReply_Unknown{Unknown: &r.UnknownAttempt{Context: &c.ObservationContext{Identity: pbIdentity(), Tick: proto.Int64(1), NativeGeneration: proto.Uint64(1)}}}}
	s := &testServer{schema: protoSchema, handler: func(_ context.Context, args nativeArgument) (*mcp.CallToolResult, error) {
		if args.Tool != "rimgovernor/receipts_lookup" {
			t.Fatal(args.Tool)
		}
		return pbResult(unknown), nil
	}}
	client := testClient(t, s, time.Second)
	reply, _, err := client.LookupRecoveryService(context.Background(), w)
	if err != nil || reply.GetUnknown() == nil {
		t.Fatal("unknown attempt lookup failed", err)
	}
	completed := &r.Progress{Attempt: w.Attempt, Context: &c.ObservationContext{Identity: pbIdentity(), Tick: proto.Int64(11), NativeGeneration: proto.Uint64(1)}, CompleteInspection: proto.Bool(true), Effect: &r.Progress_Completed{Completed: &r.CompletedEffect{Evidence: recoveryServiceEffectEvidence()}}}
	s2 := &testServer{schema: protoSchema, handler: func(_ context.Context, args nativeArgument) (*mcp.CallToolResult, error) {
		if args.Tool != "rimgovernor/receipts_observe_progress" {
			t.Fatal(args.Tool)
		}
		return pbResult(&r.ProgressReply{Outcome: &r.ProgressReply_Progress{Progress: completed}}), nil
	}}
	client2 := testClient(t, s2, time.Second)
	if _, _, err = client2.ObserveRecoveryServiceProgress(context.Background(), w, admission); err != nil {
		t.Fatal("completed progress rejected", err)
	}
	for name, change := range map[string]func(*r.Progress){
		"incomplete inspection": func(v *r.Progress) { v.CompleteInspection = proto.Bool(false) },
		"foreign pawn":          func(v *r.Progress) { v.GetCompleted().Evidence.GetJob().PawnId = proto.String("other") },
		"foreign target": func(v *r.Progress) {
			v.GetCompleted().Evidence.GetJob().TargetA = &r.JobTarget{Target: &r.JobTarget_ThingId{ThingId: "other"}}
		},
		"wrong job def":           func(v *r.Progress) { v.GetCompleted().Evidence.GetJob().JobDef = proto.String("Other") },
		"stale tick before admit": func(v *r.Progress) { v.Context.Tick = proto.Int64(1) },
	} {
		t.Run(name, func(t *testing.T) {
			p := proto.Clone(completed).(*r.Progress)
			change(p)
			bad := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
				return pbResult(&r.ProgressReply{Outcome: &r.ProgressReply_Progress{Progress: p}}), nil
			}}
			if _, _, err := testClient(t, bad, time.Second).ObserveRecoveryServiceProgress(context.Background(), w, admission); !errors.Is(err, ErrContract) {
				t.Fatal("invalid completion accepted", err)
			}
		})
	}
	refusal := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
		out := pbResult(&op.ExecuteReply{Outcome: &op.ExecuteReply_Failure{Failure: &c.Failure{Code: c.FailureCode_FAILURE_CODE_AUTHORITY_REQUIRED.Enum()}}})
		out.IsError = true
		return out, nil
	}}
	writer, _ := NewRecoveryServiceWriter(testClient(t, refusal, time.Second))
	if _, raw, err := writer.ApplyRecoveryService(context.Background(), recoveryServicePre(), admission.AuthorizingOwner, "pawn", "pawn-token", "thing", "thing-token", RecoveryServiceRepair); !errors.Is(err, ErrRefused) || len(raw.Envelope) == 0 {
		t.Fatal("typed refusal lost", err)
	}
	lost := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
		return nil, errors.New("lost after potential effect")
	}}
	writer, _ = NewRecoveryServiceWriter(testClient(t, lost, time.Second))
	if reply, _, err := writer.ApplyRecoveryService(context.Background(), recoveryServicePre(), admission.AuthorizingOwner, "pawn", "pawn-token", "thing", "thing-token", RecoveryServiceRepair); err == nil || reply != nil || len(lost.calls) != 1 {
		t.Fatal("lost reply fabricated outcome or retried", err)
	}
}
