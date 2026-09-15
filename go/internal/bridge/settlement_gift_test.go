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

func settlementGiftPre() *a.WritePrecondition {
	return &a.WritePrecondition{Identity: pbIdentity(), ExpectedGeneration: proto.Uint64(1), Attempt: &c.AttemptKey{ControllerSessionId: proto.String("controller"), ActionId: proto.String("action"), AttemptId: proto.Uint64(1)}}
}
func settlementGiftEffectEvidence() *r.EffectEvidence {
	return &r.EffectEvidence{Effect: &r.EffectEvidence_Trade{Trade: &r.TradeEffect{FactionId: proto.String("faction-1"), Executed: proto.Bool(true), ActuallyTraded: proto.Bool(true)}}}
}
func settlementGiftAdmission() *r.Receipt {
	return &r.Receipt{Attempt: settlementGiftPre().Attempt, AdmittedContext: &c.ObservationContext{Identity: pbIdentity(), Tick: proto.Int64(10), NativeGeneration: proto.Uint64(1)}, Outcome: &r.Receipt_Applied{Applied: &r.Applied{Observed: settlementGiftEffectEvidence()}}}
}
func settlementGiftAttemptFixture() SettlementGiftAttempt {
	return SettlementGiftAttempt{
		Identity: pbIdentity(), Attempt: settlementGiftPre().Attempt, Generation: 1,
		Caravan: "caravan-1", CaravanToken: "caravan-cas", Faction: "faction-1", FactionToken: "faction-cas",
		ExpectedPawnIDs: []string{"pawn-1", "pawn-2"}, Silver: 500,
	}
}

func TestPreviewSettlementGiftAcceptedAndRejections(t *testing.T) {
	valid := &op.PreviewReply{Outcome: &op.PreviewReply_Evaluated{Evaluated: &op.PreviewEvaluation{
		Context:   pbContext(),
		Accepted:  proto.Bool(true),
		Projected: &r.EffectEvidence{Effect: &r.EffectEvidence_Trade{Trade: &r.TradeEffect{FactionId: proto.String("faction-1")}}},
	}}}
	for _, test := range []struct {
		name   string
		change func(*op.PreviewReply)
		ok     bool
	}{
		{"valid", func(*op.PreviewReply) {}, true},
		{"not accepted", func(v *op.PreviewReply) { v.GetEvaluated().Accepted = proto.Bool(false) }, true},
		{"missing accepted", func(v *op.PreviewReply) { v.GetEvaluated().Accepted = nil }, false},
		{"unexpected preparation", func(v *op.PreviewReply) {
			v.GetEvaluated().Preparation = &op.PreviewEvaluation_Trade{Trade: &op.TradePreparation{}}
		}, false},
		{"missing trade projection", func(v *op.PreviewReply) { v.GetEvaluated().Projected = &r.EffectEvidence{} }, false},
		{"projection mismatch", func(v *op.PreviewReply) { v.GetEvaluated().Projected.GetTrade().FactionId = proto.String("other") }, false},
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
				gift := req.Operation.GetGiftCaravanSilver()
				if gift.GetCaravan().GetEntityId() != "caravan-1" || gift.GetCaravan().GetExpectedSnapshotToken() != "caravan-cas" ||
					gift.GetFaction().GetEntityId() != "faction-1" || gift.GetFaction().GetExpectedSnapshotToken() != "faction-cas" ||
					len(gift.GetExpectedPawnIds()) != 2 || gift.GetSilver() != 500 {
					t.Fatal("request correlation lost", req)
				}
				return pbResult(reply), nil
			}}
			client := testClient(t, s, time.Second)
			_, raw, err := client.PreviewSettlementGift(context.Background(), pbIdentity(), "caravan-1", "caravan-cas", "faction-1", "faction-cas", []string{"pawn-1", "pawn-2"}, 500)
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

func TestPreviewSettlementGiftInvalidInputsNeverDispatch(t *testing.T) {
	s := &testServer{schema: protoSchema}
	client := testClient(t, s, time.Second)
	for _, args := range []struct {
		caravan, caravanToken, faction, factionToken string
		pawns                                        []string
		silver                                       int32
	}{
		{"", "caravan-cas", "faction-1", "faction-cas", []string{"pawn-1"}, 500},
		{"caravan-1", "", "faction-1", "faction-cas", []string{"pawn-1"}, 500},
		{"caravan-1", "caravan-cas", "caravan-1", "faction-cas", []string{"pawn-1"}, 500},
		{"caravan-1", "caravan-cas", "faction-1", "faction-cas", nil, 500},
		{"caravan-1", "caravan-cas", "faction-1", "faction-cas", []string{"pawn-1", "pawn-1"}, 500},
		{"caravan-1", "caravan-cas", "faction-1", "faction-cas", []string{"pawn-1"}, 0},
		{"caravan-1", "caravan-cas", "faction-1", "faction-cas", []string{"pawn-1"}, -1},
	} {
		if _, _, err := client.PreviewSettlementGift(context.Background(), pbIdentity(), args.caravan, args.caravanToken, args.faction, args.factionToken, args.pawns, args.silver); !errors.Is(err, ErrContract) {
			t.Fatal("invalid settlement gift command accepted", args, err)
		}
	}
	if len(s.calls) != 0 {
		t.Fatal("invalid input dispatched", s.calls)
	}
}

func TestApplySettlementGiftCorrelationAndOwnerMismatch(t *testing.T) {
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
		if !proto.Equal(req.Precondition, settlementGiftPre()) || req.Operation.GetGiftCaravanSilver().GetCaravan().GetEntityId() != "caravan-1" {
			t.Fatal("request correlation lost", req)
		}
		return pbResult(&op.ExecuteReply{Outcome: &op.ExecuteReply_Receipt{Receipt: settlementGiftAdmission()}}), nil
	}}
	writer, err := NewSettlementGiftWriter(testClient(t, s, time.Second))
	if err != nil {
		t.Fatal(err)
	}
	pre := settlementGiftPre()
	reply, raw, err := writer.ApplySettlementGift(context.Background(), pre, "caravan-1", "caravan-cas", "faction-1", "faction-cas", []string{"pawn-1", "pawn-2"}, 500)
	if err != nil || reply.GetReceipt() == nil || len(raw.Envelope) == 0 {
		t.Fatal(err)
	}
	mismatched := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
		admission := settlementGiftAdmission()
		admission.Attempt.AttemptId = proto.Uint64(999)
		return pbResult(&op.ExecuteReply{Outcome: &op.ExecuteReply_Receipt{Receipt: admission}}), nil
	}}
	writer, _ = NewSettlementGiftWriter(testClient(t, mismatched, time.Second))
	if _, _, err = writer.ApplySettlementGift(context.Background(), pre, "caravan-1", "caravan-cas", "faction-1", "faction-cas", []string{"pawn-1", "pawn-2"}, 500); !errors.Is(err, ErrContract) {
		t.Fatal("owner mismatch accepted", err)
	}
}

func TestApplySettlementGiftInvalidInputsNeverDispatch(t *testing.T) {
	s := &testServer{schema: protoSchema}
	writer, _ := NewSettlementGiftWriter(testClient(t, s, time.Second))
	for _, change := range []func(*a.WritePrecondition){
		func(v *a.WritePrecondition) { v.ExpectedGeneration = nil },
		func(v *a.WritePrecondition) { v.ExpectedGeneration = proto.Uint64(0) },
		func(v *a.WritePrecondition) { v.Attempt.AttemptId = nil },
	} {
		pre := settlementGiftPre()
		change(pre)
		if _, _, err := writer.ApplySettlementGift(context.Background(), pre, "caravan-1", "caravan-cas", "faction-1", "faction-cas", []string{"pawn-1", "pawn-2"}, 500); !errors.Is(err, ErrContract) {
			t.Fatal("invalid precondition accepted", err)
		}
	}
	if _, _, err := writer.ApplySettlementGift(context.Background(), settlementGiftPre(), "caravan-1", "caravan-cas", "faction-1", "faction-cas", []string{"pawn-1", "pawn-2"}, 0); !errors.Is(err, ErrContract) {
		t.Fatal("invalid silver accepted", err)
	}
	if len(s.calls) != 0 {
		t.Fatal("invalid input dispatched", s.calls)
	}
}

func TestLookupAndObserveSettlementGift(t *testing.T) {
	w := settlementGiftAttemptFixture()
	admission := settlementGiftAdmission()
	unknown := &r.LookupReply{Outcome: &r.LookupReply_Unknown{Unknown: &r.UnknownAttempt{Context: &c.ObservationContext{Identity: pbIdentity(), Tick: proto.Int64(1), NativeGeneration: proto.Uint64(1)}}}}
	s := &testServer{schema: protoSchema, handler: func(_ context.Context, args nativeArgument) (*mcp.CallToolResult, error) {
		if args.Tool != "rimgovernor/receipts_lookup" {
			t.Fatal(args.Tool)
		}
		return pbResult(unknown), nil
	}}
	client := testClient(t, s, time.Second)
	reply, _, err := client.LookupSettlementGift(context.Background(), w)
	if err != nil || reply.GetUnknown() == nil {
		t.Fatal("unknown attempt lookup failed", err)
	}
	completed := &r.Progress{Attempt: w.Attempt, Context: &c.ObservationContext{Identity: pbIdentity(), Tick: proto.Int64(11), NativeGeneration: proto.Uint64(1)}, CompleteInspection: proto.Bool(true), Effect: &r.Progress_Completed{Completed: &r.CompletedEffect{Evidence: settlementGiftEffectEvidence()}}}
	s2 := &testServer{schema: protoSchema, handler: func(_ context.Context, args nativeArgument) (*mcp.CallToolResult, error) {
		if args.Tool != "rimgovernor/receipts_observe_progress" {
			t.Fatal(args.Tool)
		}
		return pbResult(&r.ProgressReply{Outcome: &r.ProgressReply_Progress{Progress: completed}}), nil
	}}
	client2 := testClient(t, s2, time.Second)
	if _, _, err = client2.ObserveSettlementGiftProgress(context.Background(), w, admission); err != nil {
		t.Fatal("completed progress rejected", err)
	}
	for name, change := range map[string]func(*r.Progress){
		"incomplete inspection":   func(v *r.Progress) { v.CompleteInspection = proto.Bool(false) },
		"foreign faction":         func(v *r.Progress) { v.GetCompleted().Evidence.GetTrade().FactionId = proto.String("other") },
		"stale tick before admit": func(v *r.Progress) { v.Context.Tick = proto.Int64(1) },
	} {
		t.Run(name, func(t *testing.T) {
			p := proto.Clone(completed).(*r.Progress)
			change(p)
			bad := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
				return pbResult(&r.ProgressReply{Outcome: &r.ProgressReply_Progress{Progress: p}}), nil
			}}
			if _, _, err := testClient(t, bad, time.Second).ObserveSettlementGiftProgress(context.Background(), w, admission); !errors.Is(err, ErrContract) {
				t.Fatal("invalid completion accepted", err)
			}
		})
	}
	refusal := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
		out := pbResult(&op.ExecuteReply{Outcome: &op.ExecuteReply_Failure{Failure: &c.Failure{Code: c.FailureCode_FAILURE_CODE_AUTHORITY_REQUIRED.Enum()}}})
		out.IsError = true
		return out, nil
	}}
	writer, _ := NewSettlementGiftWriter(testClient(t, refusal, time.Second))
	if _, raw, err := writer.ApplySettlementGift(context.Background(), settlementGiftPre(), "caravan-1", "caravan-cas", "faction-1", "faction-cas", []string{"pawn-1", "pawn-2"}, 500); !errors.Is(err, ErrRefused) || len(raw.Envelope) == 0 {
		t.Fatal("typed refusal lost", err)
	}
	lost := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
		return nil, errors.New("lost after potential effect")
	}}
	writer, _ = NewSettlementGiftWriter(testClient(t, lost, time.Second))
	if reply, _, err := writer.ApplySettlementGift(context.Background(), settlementGiftPre(), "caravan-1", "caravan-cas", "faction-1", "faction-cas", []string{"pawn-1", "pawn-2"}, 500); err == nil || reply != nil || len(lost.calls) != 1 {
		t.Fatal("lost reply fabricated outcome or retried", err)
	}
}
