package bridge

import (
	"context"
	"encoding/json"
	"errors"
	"testing"

	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"github.com/modelcontextprotocol/go-sdk/mcp"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
)

func gearReplacePre() *a.WritePrecondition {
	return &a.WritePrecondition{Identity: pbIdentity(), ExpectedGeneration: proto.Uint64(1), Attempt: &c.AttemptKey{ControllerSessionId: proto.String("controller"), ActionId: proto.String("action"), AttemptId: proto.Uint64(1)}}
}
func gearReplaceEffectEvidence() *r.EffectEvidence {
	return &r.EffectEvidence{Effect: &r.EffectEvidence_Job{Job: &r.JobEffect{
		PawnId:   proto.String("pawn"),
		JobDef:   proto.String(gearReplaceJobDef),
		TargetA:  &r.JobTarget{Target: &r.JobTarget_ThingId{ThingId: "thing"}},
		CanTry:   proto.Bool(true),
		Issued:   proto.Bool(true),
		Verified: proto.Bool(false),
	}}}
}
func gearReplaceAdmission() *r.Receipt {
	return &r.Receipt{Attempt: gearReplacePre().Attempt, AdmittedContext: &c.ObservationContext{Identity: pbIdentity(), Tick: proto.Int64(10), NativeGeneration: proto.Uint64(1)}, Outcome: &r.Receipt_Applied{Applied: &r.Applied{Observed: gearReplaceEffectEvidence()}}}
}
func gearReplaceAttemptFixture() GearReplaceAttempt {
	return GearReplaceAttempt{Identity: pbIdentity(), Attempt: gearReplacePre().Attempt, Generation: 1, Pawn: "pawn", Thing: "thing", PawnToken: "pawn-token", ThingToken: "thing-token", LoadoutToken: "loadout-token"}
}

func TestPreviewGearReplaceAcceptedAndRejections(t *testing.T) {
	valid := &op.PreviewReply{Outcome: &op.PreviewReply_Evaluated{Evaluated: &op.PreviewEvaluation{
		Context:   pbContext(),
		Accepted:  proto.Bool(true),
		Projected: &r.EffectEvidence{Effect: &r.EffectEvidence_Job{Job: &r.JobEffect{PawnId: proto.String("pawn"), JobDef: proto.String(gearReplaceJobDef), TargetA: &r.JobTarget{Target: &r.JobTarget_ThingId{ThingId: "thing"}}, CanTry: proto.Bool(true), Issued: proto.Bool(false), Verified: proto.Bool(false)}}},
	}}}
	for _, test := range []struct {
		name   string
		change func(*op.PreviewReply)
		ok     bool
	}{
		{"valid", func(*op.PreviewReply) {}, true},
		{"not accepted", func(v *op.PreviewReply) { v.GetEvaluated().Accepted = proto.Bool(false) }, false},
		{"missing accepted", func(v *op.PreviewReply) { v.GetEvaluated().Accepted = nil }, false},
		{"unexpected preparation", func(v *op.PreviewReply) {
			v.GetEvaluated().Preparation = &op.PreviewEvaluation_Trade{Trade: &op.TradePreparation{}}
		}, false},
		{"missing job projection", func(v *op.PreviewReply) { v.GetEvaluated().Projected = &r.EffectEvidence{} }, false},
		{"projection mismatch", func(v *op.PreviewReply) { v.GetEvaluated().Projected.GetJob().JobDef = proto.String("Other") }, false},
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
				improve := req.Operation.GetImproveGear()
				if improve.GetPawn().GetEntityId() != "pawn" || improve.GetTarget().GetEntityId() != "thing" || improve.GetExpectedLoadoutToken() != "loadout-token" {
					t.Fatal("request correlation lost", req)
				}
				return pbResult(reply), nil
			}}
			client := testClient(t, s, testBudget)
			_, raw, err := client.PreviewGearReplace(context.Background(), pbIdentity(), "pawn", "pawn-token", "thing", "thing-token", "loadout-token")
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

func TestPreviewGearReplaceInvalidInputsNeverDispatch(t *testing.T) {
	s := &testServer{schema: protoSchema}
	client := testClient(t, s, testBudget)
	for _, args := range [][5]string{
		{"", "pawn-token", "thing", "thing-token", "loadout-token"},
		{"pawn", "", "thing", "thing-token", "loadout-token"},
		{"pawn", "pawn-token", "", "thing-token", "loadout-token"},
		{"pawn", "pawn-token", "thing", "", "loadout-token"},
		{"pawn", "pawn-token", "thing", "thing-token", ""},
		{"pawn", "pawn-token", "pawn", "thing-token", "loadout-token"},
	} {
		if _, _, err := client.PreviewGearReplace(context.Background(), pbIdentity(), args[0], args[1], args[2], args[3], args[4]); !errors.Is(err, ErrContract) {
			t.Fatal("invalid gear replace command accepted", args, err)
		}
	}
	if len(s.calls) != 0 {
		t.Fatal("invalid input dispatched", s.calls)
	}
}

func TestApplyGearReplaceCorrelationAndOwnerMismatch(t *testing.T) {
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
		if !proto.Equal(req.Precondition, gearReplacePre()) || req.Operation.GetImproveGear().GetPawn().GetEntityId() != "pawn" {
			t.Fatal("request correlation lost", req)
		}
		return pbResult(&op.ExecuteReply{Outcome: &op.ExecuteReply_Receipt{Receipt: gearReplaceAdmission()}}), nil
	}}
	writer, err := NewGearReplaceWriter(testClient(t, s, testBudget))
	if err != nil {
		t.Fatal(err)
	}
	pre := gearReplacePre()
	reply, raw, err := writer.ApplyGearReplace(context.Background(), pre, "pawn", "pawn-token", "thing", "thing-token", "loadout-token")
	if err != nil || reply.GetReceipt() == nil || len(raw.Envelope) == 0 {
		t.Fatal(err)
	}
	mismatched := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
		admission := gearReplaceAdmission()
		admission.Attempt.AttemptId = proto.Uint64(999)
		return pbResult(&op.ExecuteReply{Outcome: &op.ExecuteReply_Receipt{Receipt: admission}}), nil
	}}
	writer, _ = NewGearReplaceWriter(testClient(t, mismatched, testBudget))
	if _, _, err = writer.ApplyGearReplace(context.Background(), pre, "pawn", "pawn-token", "thing", "thing-token", "loadout-token"); !errors.Is(err, ErrContract) {
		t.Fatal("owner mismatch accepted", err)
	}
}

func TestApplyGearReplaceInvalidInputsNeverDispatch(t *testing.T) {
	s := &testServer{schema: protoSchema}
	writer, _ := NewGearReplaceWriter(testClient(t, s, testBudget))
	for _, change := range []func(*a.WritePrecondition){
		func(v *a.WritePrecondition) { v.ExpectedGeneration = nil },
		func(v *a.WritePrecondition) { v.ExpectedGeneration = proto.Uint64(0) },
		func(v *a.WritePrecondition) { v.Attempt.AttemptId = nil },
	} {
		pre := gearReplacePre()
		change(pre)
		if _, _, err := writer.ApplyGearReplace(context.Background(), pre, "pawn", "pawn-token", "thing", "thing-token", "loadout-token"); !errors.Is(err, ErrContract) {
			t.Fatal("invalid precondition accepted", err)
		}
	}
	if _, _, err := writer.ApplyGearReplace(context.Background(), gearReplacePre(), "pawn", "pawn-token", "pawn", "thing-token", "loadout-token"); !errors.Is(err, ErrContract) {
		t.Fatal("pawn/thing collision accepted", err)
	}
	if len(s.calls) != 0 {
		t.Fatal("invalid input dispatched", s.calls)
	}
}

func TestLookupAndObserveGearReplace(t *testing.T) {
	w := gearReplaceAttemptFixture()
	admission := gearReplaceAdmission()
	unknown := &r.LookupReply{Outcome: &r.LookupReply_Unknown{Unknown: &r.UnknownAttempt{Context: &c.ObservationContext{Identity: pbIdentity(), Tick: proto.Int64(1), NativeGeneration: proto.Uint64(1)}}}}
	s := &testServer{schema: protoSchema, handler: func(_ context.Context, args nativeArgument) (*mcp.CallToolResult, error) {
		if args.Tool != "rimgovernor/receipts_lookup" {
			t.Fatal(args.Tool)
		}
		return pbResult(unknown), nil
	}}
	client := testClient(t, s, testBudget)
	reply, _, err := client.LookupGearReplace(context.Background(), w)
	if err != nil || reply.GetUnknown() == nil {
		t.Fatal("unknown attempt lookup failed", err)
	}
	completed := &r.Progress{Attempt: w.Attempt, Context: &c.ObservationContext{Identity: pbIdentity(), Tick: proto.Int64(11), NativeGeneration: proto.Uint64(1)}, CompleteInspection: proto.Bool(true), Effect: &r.Progress_Completed{Completed: &r.CompletedEffect{Evidence: gearReplaceEffectEvidence()}}}
	s2 := &testServer{schema: protoSchema, handler: func(_ context.Context, args nativeArgument) (*mcp.CallToolResult, error) {
		if args.Tool != "rimgovernor/receipts_observe_progress" {
			t.Fatal(args.Tool)
		}
		return pbResult(&r.ProgressReply{Outcome: &r.ProgressReply_Progress{Progress: completed}}), nil
	}}
	client2 := testClient(t, s2, testBudget)
	if _, _, err = client2.ObserveGearReplaceProgress(context.Background(), w, admission); err != nil {
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
			if _, _, err := testClient(t, bad, testBudget).ObserveGearReplaceProgress(context.Background(), w, admission); !errors.Is(err, ErrContract) {
				t.Fatal("invalid completion accepted", err)
			}
		})
	}
	refusal := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
		out := pbResult(&op.ExecuteReply{Outcome: &op.ExecuteReply_Failure{Failure: &c.Failure{Code: c.FailureCode_FAILURE_CODE_AUTHORITY_REQUIRED.Enum()}}})
		out.IsError = true
		return out, nil
	}}
	writer, _ := NewGearReplaceWriter(testClient(t, refusal, testBudget))
	if _, raw, err := writer.ApplyGearReplace(context.Background(), gearReplacePre(), "pawn", "pawn-token", "thing", "thing-token", "loadout-token"); !errors.Is(err, ErrRefused) || len(raw.Envelope) == 0 {
		t.Fatal("typed refusal lost", err)
	}
	lost := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
		return nil, errors.New("lost after potential effect")
	}}
	writer, _ = NewGearReplaceWriter(testClient(t, lost, testBudget))
	if reply, _, err := writer.ApplyGearReplace(context.Background(), gearReplacePre(), "pawn", "pawn-token", "thing", "thing-token", "loadout-token"); err == nil || reply != nil || len(lost.calls) != 1 {
		t.Fatal("lost reply fabricated outcome or retried", err)
	}
}
