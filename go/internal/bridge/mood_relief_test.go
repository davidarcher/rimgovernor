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

func moodReliefJob() MoodReliefExpectedJob { id := int32(7); return MoodReliefExpectedJob{JobID: &id} }
func moodReliefPre() *a.WritePrecondition {
	return &a.WritePrecondition{Identity: pbIdentity(), ExpectedGeneration: proto.Uint64(1), Attempt: &c.AttemptKey{ControllerSessionId: proto.String("controller"), ActionId: proto.String("action"), AttemptId: proto.Uint64(1)}}
}
func moodReliefEffectEvidence() *r.EffectEvidence {
	return &r.EffectEvidence{Effect: &r.EffectEvidence_Job{Job: &r.JobEffect{
		PawnId:   proto.String("pawn"),
		JobDef:   proto.String("Ingest"),
		TargetA:  &r.JobTarget{Target: &r.JobTarget_ThingId{ThingId: "pawn"}},
		CanTry:   proto.Bool(true),
		Issued:   proto.Bool(true),
		Verified: proto.Bool(true),
	}}}
}
func moodReliefAdmission() *r.Receipt {
	return &r.Receipt{Attempt: moodReliefPre().Attempt, AdmittedContext: &c.ObservationContext{Identity: pbIdentity(), Tick: proto.Int64(10), NativeGeneration: proto.Uint64(1)}, Outcome: &r.Receipt_Applied{Applied: &r.Applied{Observed: moodReliefEffectEvidence()}}}
}
func moodReliefAttemptFixture() MoodReliefAttempt {
	return MoodReliefAttempt{Identity: pbIdentity(), Attempt: moodReliefPre().Attempt, Generation: 1, Pawn: "pawn", PawnToken: "pawn-token", Need: MoodReliefFood, ExpectedJob: moodReliefJob(), ExpectedScheduleDef: "Anything"}
}

func TestPreviewMoodReliefAcceptedAndRejections(t *testing.T) {
	valid := &op.PreviewReply{Outcome: &op.PreviewReply_Evaluated{Evaluated: &op.PreviewEvaluation{
		Context:  pbContext(),
		Accepted: proto.Bool(true),
		Reason:   proto.String("Exact native need priority and player timetable allow recovery now."),
		Projected: &r.EffectEvidence{Effect: &r.EffectEvidence_Job{Job: &r.JobEffect{
			PawnId: proto.String("pawn"), TargetA: &r.JobTarget{Target: &r.JobTarget_ThingId{ThingId: "pawn"}},
			CanTry: proto.Bool(true), Issued: proto.Bool(false), Verified: proto.Bool(false),
		}}},
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
		{"already issued", func(v *op.PreviewReply) { v.GetEvaluated().Projected.GetJob().Issued = proto.Bool(true) }, false},
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
				relief := req.Operation.GetRelieveNeed()
				if relief.GetPawn().GetEntityId() != "pawn" || relief.GetNeed() != op.Need_NEED_FOOD || relief.GetExpectedJob().GetJobId() != 7 || relief.GetExpectedScheduleDef() != "Anything" {
					t.Fatal("request correlation lost", req)
				}
				return pbResult(reply), nil
			}}
			client := testClient(t, s, time.Second)
			_, raw, err := client.PreviewMoodRelief(context.Background(), pbIdentity(), "pawn", "pawn-token", MoodReliefFood, moodReliefJob(), "Anything")
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

func TestPreviewMoodReliefInvalidInputsNeverDispatch(t *testing.T) {
	s := &testServer{schema: protoSchema}
	client := testClient(t, s, time.Second)
	for _, args := range []struct {
		pawn, pawnToken, schedule string
		need                      MoodReliefNeed
		job                       MoodReliefExpectedJob
	}{
		{"", "pawn-token", "Anything", MoodReliefFood, moodReliefJob()},
		{"pawn", "", "Anything", MoodReliefFood, moodReliefJob()},
		{"pawn", "pawn-token", "", MoodReliefFood, moodReliefJob()},
		{"pawn", "pawn-token", "Anything", MoodReliefNeedUnspecified, moodReliefJob()},
		{"pawn", "pawn-token", "Anything", MoodReliefFood, MoodReliefExpectedJob{}},
		{"pawn", "pawn-token", "Anything", MoodReliefFood, MoodReliefExpectedJob{Idle: true, JobID: func() *int32 { v := int32(1); return &v }()}},
	} {
		if _, _, err := client.PreviewMoodRelief(context.Background(), pbIdentity(), args.pawn, args.pawnToken, args.need, args.job, args.schedule); !errors.Is(err, ErrContract) {
			t.Fatal("invalid mood relief command accepted", args, err)
		}
	}
	if len(s.calls) != 0 {
		t.Fatal("invalid input dispatched", s.calls)
	}
}

func TestApplyMoodReliefCorrelationAndOwnerMismatch(t *testing.T) {
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
		if !proto.Equal(req.Precondition, moodReliefPre()) || req.Operation.GetRelieveNeed().GetPawn().GetEntityId() != "pawn" {
			t.Fatal("request correlation lost", req)
		}
		return pbResult(&op.ExecuteReply{Outcome: &op.ExecuteReply_Receipt{Receipt: moodReliefAdmission()}}), nil
	}}
	writer, err := NewMoodReliefWriter(testClient(t, s, time.Second))
	if err != nil {
		t.Fatal(err)
	}
	pre := moodReliefPre()
	reply, raw, err := writer.ApplyMoodRelief(context.Background(), pre, "pawn", "pawn-token", MoodReliefFood, moodReliefJob(), "Anything")
	if err != nil || reply.GetReceipt() == nil || len(raw.Envelope) == 0 {
		t.Fatal(err)
	}
	mismatched := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
		admission := moodReliefAdmission()
		admission.Attempt.AttemptId = proto.Uint64(999)
		return pbResult(&op.ExecuteReply{Outcome: &op.ExecuteReply_Receipt{Receipt: admission}}), nil
	}}
	writer, _ = NewMoodReliefWriter(testClient(t, mismatched, time.Second))
	if _, _, err = writer.ApplyMoodRelief(context.Background(), pre, "pawn", "pawn-token", MoodReliefFood, moodReliefJob(), "Anything"); !errors.Is(err, ErrContract) {
		t.Fatal("owner mismatch accepted", err)
	}
}

func TestApplyMoodReliefInvalidInputsNeverDispatch(t *testing.T) {
	s := &testServer{schema: protoSchema}
	writer, _ := NewMoodReliefWriter(testClient(t, s, time.Second))
	for _, change := range []func(*a.WritePrecondition){
		func(v *a.WritePrecondition) { v.ExpectedGeneration = nil },
		func(v *a.WritePrecondition) { v.ExpectedGeneration = proto.Uint64(0) },
		func(v *a.WritePrecondition) { v.Attempt.AttemptId = nil },
	} {
		pre := moodReliefPre()
		change(pre)
		if _, _, err := writer.ApplyMoodRelief(context.Background(), pre, "pawn", "pawn-token", MoodReliefFood, moodReliefJob(), "Anything"); !errors.Is(err, ErrContract) {
			t.Fatal("invalid precondition accepted", err)
		}
	}
	if _, _, err := writer.ApplyMoodRelief(context.Background(), moodReliefPre(), "pawn", "pawn-token", MoodReliefNeedUnspecified, moodReliefJob(), "Anything"); !errors.Is(err, ErrContract) {
		t.Fatal("unspecified need accepted", err)
	}
	if len(s.calls) != 0 {
		t.Fatal("invalid input dispatched", s.calls)
	}
}

func TestLookupAndObserveMoodRelief(t *testing.T) {
	w := moodReliefAttemptFixture()
	admission := moodReliefAdmission()
	unknown := &r.LookupReply{Outcome: &r.LookupReply_Unknown{Unknown: &r.UnknownAttempt{Context: &c.ObservationContext{Identity: pbIdentity(), Tick: proto.Int64(1), NativeGeneration: proto.Uint64(1)}}}}
	s := &testServer{schema: protoSchema, handler: func(_ context.Context, args nativeArgument) (*mcp.CallToolResult, error) {
		if args.Tool != "rimgovernor/receipts_lookup" {
			t.Fatal(args.Tool)
		}
		return pbResult(unknown), nil
	}}
	client := testClient(t, s, time.Second)
	reply, _, err := client.LookupMoodRelief(context.Background(), w)
	if err != nil || reply.GetUnknown() == nil {
		t.Fatal("unknown attempt lookup failed", err)
	}
	completed := &r.Progress{Attempt: w.Attempt, Context: &c.ObservationContext{Identity: pbIdentity(), Tick: proto.Int64(11), NativeGeneration: proto.Uint64(1)}, CompleteInspection: proto.Bool(true), Effect: &r.Progress_Completed{Completed: &r.CompletedEffect{Evidence: moodReliefEffectEvidence()}}}
	s2 := &testServer{schema: protoSchema, handler: func(_ context.Context, args nativeArgument) (*mcp.CallToolResult, error) {
		if args.Tool != "rimgovernor/receipts_observe_progress" {
			t.Fatal(args.Tool)
		}
		return pbResult(&r.ProgressReply{Outcome: &r.ProgressReply_Progress{Progress: completed}}), nil
	}}
	client2 := testClient(t, s2, time.Second)
	if _, _, err = client2.ObserveMoodReliefProgress(context.Background(), w, admission); err != nil {
		t.Fatal("completed progress rejected", err)
	}
	for name, change := range map[string]func(*r.Progress){
		"incomplete inspection":   func(v *r.Progress) { v.CompleteInspection = proto.Bool(false) },
		"foreign pawn":            func(v *r.Progress) { v.GetCompleted().Evidence.GetJob().PawnId = proto.String("other") },
		"stale tick before admit": func(v *r.Progress) { v.Context.Tick = proto.Int64(1) },
	} {
		t.Run(name, func(t *testing.T) {
			p := proto.Clone(completed).(*r.Progress)
			change(p)
			bad := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
				return pbResult(&r.ProgressReply{Outcome: &r.ProgressReply_Progress{Progress: p}}), nil
			}}
			if _, _, err := testClient(t, bad, time.Second).ObserveMoodReliefProgress(context.Background(), w, admission); !errors.Is(err, ErrContract) {
				t.Fatal("invalid completion accepted", err)
			}
		})
	}
	refusal := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
		out := pbResult(&op.ExecuteReply{Outcome: &op.ExecuteReply_Failure{Failure: &c.Failure{Code: c.FailureCode_FAILURE_CODE_AUTHORITY_REQUIRED.Enum()}}})
		out.IsError = true
		return out, nil
	}}
	writer, _ := NewMoodReliefWriter(testClient(t, refusal, time.Second))
	if _, raw, err := writer.ApplyMoodRelief(context.Background(), moodReliefPre(), "pawn", "pawn-token", MoodReliefFood, moodReliefJob(), "Anything"); !errors.Is(err, ErrRefused) || len(raw.Envelope) == 0 {
		t.Fatal("typed refusal lost", err)
	}
	lost := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
		return nil, errors.New("lost after potential effect")
	}}
	writer, _ = NewMoodReliefWriter(testClient(t, lost, time.Second))
	if reply, _, err := writer.ApplyMoodRelief(context.Background(), moodReliefPre(), "pawn", "pawn-token", MoodReliefFood, moodReliefJob(), "Anything"); err == nil || reply != nil || len(lost.calls) != 1 {
		t.Fatal("lost reply fabricated outcome or retried", err)
	}
}
