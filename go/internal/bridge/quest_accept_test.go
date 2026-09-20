package bridge

import (
	"context"
	"encoding/json"
	"errors"
	"testing"

	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"github.com/modelcontextprotocol/go-sdk/mcp"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
)

func questAcceptPre() *a.WritePrecondition {
	return &a.WritePrecondition{Identity: pbIdentity(), ExpectedGeneration: proto.Uint64(1), Attempt: &c.AttemptKey{ControllerSessionId: proto.String("controller"), ActionId: proto.String("action"), AttemptId: proto.Uint64(1)}}
}
func questAcceptEffectEvidence() *r.EffectEvidence {
	return &r.EffectEvidence{Effect: &r.EffectEvidence_Quest{Quest: &r.QuestEffect{QuestId: proto.String("quest-1"), Accepted: proto.Bool(true), State: proto.String("Ongoing")}}}
}
func questAcceptAdmission() *r.Receipt {
	return &r.Receipt{Attempt: questAcceptPre().Attempt, AdmittedContext: &c.ObservationContext{Identity: pbIdentity(), Tick: proto.Int64(10), NativeGeneration: proto.Uint64(1)}, Outcome: &r.Receipt_Applied{Applied: &r.Applied{Observed: questAcceptEffectEvidence()}}}
}
func questAcceptAttemptFixture() QuestAcceptAttempt {
	return QuestAcceptAttempt{Identity: pbIdentity(), Attempt: questAcceptPre().Attempt, Generation: 1, Quest: "quest-1", QuestToken: "quest-cas", AccepterPawn: "pawn-1", RewardChoice: 0}
}

func TestPreviewQuestAcceptAcceptedAndRejections(t *testing.T) {
	valid := &op.PreviewReply{Outcome: &op.PreviewReply_Evaluated{Evaluated: &op.PreviewEvaluation{
		Context:   pbContext(),
		Accepted:  proto.Bool(true),
		Projected: &r.EffectEvidence{Effect: &r.EffectEvidence_Quest{Quest: &r.QuestEffect{QuestId: proto.String("quest-1")}}},
	}}}
	for _, test := range []struct {
		name   string
		change func(*op.PreviewReply)
		ok     bool
	}{
		{"valid", func(*op.PreviewReply) {}, true},
		// Accepted=false is a legitimate native verdict (native declines this
		// exact quest/reward/accepter tuple), not a contract violation: the
		// boundary layer folds it into policy.QuestAcceptFacts.NativeCanTry
		// and EvaluateQuestAccept refuses on it, the same as any other known
		// fact. Only a missing Accepted field is a contract violation.
		{"not accepted", func(v *op.PreviewReply) { v.GetEvaluated().Accepted = proto.Bool(false) }, true},
		{"missing accepted", func(v *op.PreviewReply) { v.GetEvaluated().Accepted = nil }, false},
		{"unexpected preparation", func(v *op.PreviewReply) {
			v.GetEvaluated().Preparation = &op.PreviewEvaluation_Trade{Trade: &op.TradePreparation{}}
		}, false},
		{"missing quest projection", func(v *op.PreviewReply) { v.GetEvaluated().Projected = &r.EffectEvidence{} }, false},
		{"projection mismatch", func(v *op.PreviewReply) { v.GetEvaluated().Projected.GetQuest().QuestId = proto.String("other") }, false},
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
				accept := req.Operation.GetAcceptQuest()
				if accept.GetQuest().GetEntityId() != "quest-1" || accept.GetQuest().GetExpectedSnapshotToken() != "quest-cas" || accept.GetAccepterPawnId() != "pawn-1" || accept.GetRewardChoice() != 0 {
					t.Fatal("request correlation lost", req)
				}
				return pbResult(reply), nil
			}}
			client := testClient(t, s, testBudget)
			_, raw, err := client.PreviewQuestAccept(context.Background(), pbIdentity(), "quest-1", "quest-cas", "pawn-1", 0)
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

func TestPreviewQuestAcceptInvalidInputsNeverDispatch(t *testing.T) {
	s := &testServer{schema: protoSchema}
	client := testClient(t, s, testBudget)
	for _, args := range []struct {
		quest, token, accepter string
		choice                 int32
	}{
		{"", "quest-cas", "pawn-1", 0},
		{"quest-1", "", "pawn-1", 0},
		{"quest-1", "quest-cas", "x\x00y", 0},
		{"quest-1", "quest-cas", "pawn-1", -2},
	} {
		if _, _, err := client.PreviewQuestAccept(context.Background(), pbIdentity(), args.quest, args.token, args.accepter, args.choice); !errors.Is(err, ErrContract) {
			t.Fatal("invalid quest accept command accepted", args, err)
		}
	}
	if len(s.calls) != 0 {
		t.Fatal("invalid input dispatched", s.calls)
	}
}

func TestApplyQuestAcceptCorrelationAndOwnerMismatch(t *testing.T) {
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
		if !proto.Equal(req.Precondition, questAcceptPre()) || req.Operation.GetAcceptQuest().GetQuest().GetEntityId() != "quest-1" {
			t.Fatal("request correlation lost", req)
		}
		return pbResult(&op.ExecuteReply{Outcome: &op.ExecuteReply_Receipt{Receipt: questAcceptAdmission()}}), nil
	}}
	writer, err := NewQuestAcceptWriter(testClient(t, s, testBudget))
	if err != nil {
		t.Fatal(err)
	}
	pre := questAcceptPre()
	reply, raw, err := writer.ApplyQuestAccept(context.Background(), pre, "quest-1", "quest-cas", "pawn-1", 0)
	if err != nil || reply.GetReceipt() == nil || len(raw.Envelope) == 0 {
		t.Fatal(err)
	}
	mismatched := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
		admission := questAcceptAdmission()
		admission.Attempt.AttemptId = proto.Uint64(999)
		return pbResult(&op.ExecuteReply{Outcome: &op.ExecuteReply_Receipt{Receipt: admission}}), nil
	}}
	writer, _ = NewQuestAcceptWriter(testClient(t, mismatched, testBudget))
	if _, _, err = writer.ApplyQuestAccept(context.Background(), pre, "quest-1", "quest-cas", "pawn-1", 0); !errors.Is(err, ErrContract) {
		t.Fatal("owner mismatch accepted", err)
	}
}

func TestApplyQuestAcceptInvalidInputsNeverDispatch(t *testing.T) {
	s := &testServer{schema: protoSchema}
	writer, _ := NewQuestAcceptWriter(testClient(t, s, testBudget))
	for _, change := range []func(*a.WritePrecondition){
		func(v *a.WritePrecondition) { v.ExpectedGeneration = nil },
		func(v *a.WritePrecondition) { v.ExpectedGeneration = proto.Uint64(0) },
		func(v *a.WritePrecondition) { v.Attempt.AttemptId = nil },
	} {
		pre := questAcceptPre()
		change(pre)
		if _, _, err := writer.ApplyQuestAccept(context.Background(), pre, "quest-1", "quest-cas", "pawn-1", 0); !errors.Is(err, ErrContract) {
			t.Fatal("invalid precondition accepted", err)
		}
	}
	if _, _, err := writer.ApplyQuestAccept(context.Background(), questAcceptPre(), "quest-1", "quest-cas", "pawn-1", -2); !errors.Is(err, ErrContract) {
		t.Fatal("invalid reward choice accepted", err)
	}
	if len(s.calls) != 0 {
		t.Fatal("invalid input dispatched", s.calls)
	}
}

func TestLookupAndObserveQuestAccept(t *testing.T) {
	w := questAcceptAttemptFixture()
	admission := questAcceptAdmission()
	unknown := &r.LookupReply{Outcome: &r.LookupReply_Unknown{Unknown: &r.UnknownAttempt{Context: &c.ObservationContext{Identity: pbIdentity(), Tick: proto.Int64(1), NativeGeneration: proto.Uint64(1)}}}}
	s := &testServer{schema: protoSchema, handler: func(_ context.Context, args nativeArgument) (*mcp.CallToolResult, error) {
		if args.Tool != "rimgovernor/receipts_lookup" {
			t.Fatal(args.Tool)
		}
		return pbResult(unknown), nil
	}}
	client := testClient(t, s, testBudget)
	reply, _, err := client.LookupQuestAccept(context.Background(), w)
	if err != nil || reply.GetUnknown() == nil {
		t.Fatal("unknown attempt lookup failed", err)
	}
	completed := &r.Progress{Attempt: w.Attempt, Context: &c.ObservationContext{Identity: pbIdentity(), Tick: proto.Int64(11), NativeGeneration: proto.Uint64(1)}, CompleteInspection: proto.Bool(true), Effect: &r.Progress_Completed{Completed: &r.CompletedEffect{Evidence: questAcceptEffectEvidence()}}}
	s2 := &testServer{schema: protoSchema, handler: func(_ context.Context, args nativeArgument) (*mcp.CallToolResult, error) {
		if args.Tool != "rimgovernor/receipts_observe_progress" {
			t.Fatal(args.Tool)
		}
		return pbResult(&r.ProgressReply{Outcome: &r.ProgressReply_Progress{Progress: completed}}), nil
	}}
	client2 := testClient(t, s2, testBudget)
	if _, _, err = client2.ObserveQuestAcceptProgress(context.Background(), w, admission); err != nil {
		t.Fatal("completed progress rejected", err)
	}
	for name, change := range map[string]func(*r.Progress){
		"incomplete inspection":   func(v *r.Progress) { v.CompleteInspection = proto.Bool(false) },
		"foreign quest":           func(v *r.Progress) { v.GetCompleted().Evidence.GetQuest().QuestId = proto.String("other") },
		"stale tick before admit": func(v *r.Progress) { v.Context.Tick = proto.Int64(1) },
	} {
		t.Run(name, func(t *testing.T) {
			p := proto.Clone(completed).(*r.Progress)
			change(p)
			bad := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
				return pbResult(&r.ProgressReply{Outcome: &r.ProgressReply_Progress{Progress: p}}), nil
			}}
			if _, _, err := testClient(t, bad, testBudget).ObserveQuestAcceptProgress(context.Background(), w, admission); !errors.Is(err, ErrContract) {
				t.Fatal("invalid completion accepted", err)
			}
		})
	}
	refusal := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
		out := pbResult(&op.ExecuteReply{Outcome: &op.ExecuteReply_Failure{Failure: &c.Failure{Code: c.FailureCode_FAILURE_CODE_AUTHORITY_REQUIRED.Enum()}}})
		out.IsError = true
		return out, nil
	}}
	writer, _ := NewQuestAcceptWriter(testClient(t, refusal, testBudget))
	if _, raw, err := writer.ApplyQuestAccept(context.Background(), questAcceptPre(), "quest-1", "quest-cas", "pawn-1", 0); !errors.Is(err, ErrRefused) || len(raw.Envelope) == 0 {
		t.Fatal("typed refusal lost", err)
	}
	lost := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
		return nil, errors.New("lost after potential effect")
	}}
	writer, _ = NewQuestAcceptWriter(testClient(t, lost, testBudget))
	if reply, _, err := writer.ApplyQuestAccept(context.Background(), questAcceptPre(), "quest-1", "quest-cas", "pawn-1", 0); err == nil || reply != nil || len(lost.calls) != 1 {
		t.Fatal("lost reply fabricated outcome or retried", err)
	}
}

func TestReadQuestAcceptTargetSelectsAndValidates(t *testing.T) {
	snapshot := worldProgressionFixture()
	server := &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
		if arg.Tool != "rimgovernor/observations_read_world_progression" {
			t.Fatal(arg.Tool)
		}
		return pbResult(&o.WorldProgressionReply{Outcome: &o.WorldProgressionReply_Observed{Observed: snapshot}}), nil
	}}
	client := testClient(t, server, testBudget)
	target, _, err := client.ReadQuestAcceptTarget(context.Background(), pbIdentity(), "quest-1")
	if err != nil || target.Quest != "quest-1" || target.SnapshotToken != "quest-cas" || target.State != "NotYetAccepted" ||
		!target.RequiresAccepter || !target.CanAccept || target.ChoiceCount != 1 || !target.HasTradeRequest || len(target.EligiblePawnIDs) != 1 {
		t.Fatal(target, err)
	}
	missing := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
		return pbResult(&o.WorldProgressionReply{Outcome: &o.WorldProgressionReply_Observed{Observed: worldProgressionFixture()}}), nil
	}}
	if _, _, err := testClient(t, missing, testBudget).ReadQuestAcceptTarget(context.Background(), pbIdentity(), "quest-missing"); !errors.Is(err, ErrUnavailable) {
		t.Fatal("missing quest accepted", err)
	}
	if _, _, err := testClient(t, server, testBudget).ReadQuestAcceptTarget(context.Background(), pbIdentity(), ""); !errors.Is(err, ErrContract) {
		t.Fatal("invalid quest target identity accepted", err)
	}
}
