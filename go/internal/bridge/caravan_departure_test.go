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

func caravanDeparturePre() *a.WritePrecondition {
	return &a.WritePrecondition{Identity: pbIdentity(), ExpectedGeneration: proto.Uint64(1), Attempt: &c.AttemptKey{ControllerSessionId: proto.String("controller"), ActionId: proto.String("action"), AttemptId: proto.Uint64(1)}}
}
func caravanDepartureEffectEvidence() *r.EffectEvidence {
	return &r.EffectEvidence{Effect: &r.EffectEvidence_Caravan{Caravan: &r.CaravanEffect{
		CaravanId:       proto.String("caravan-1"),
		AssemblyStarted: proto.Bool(true),
		DestinationTile: proto.Int32(42),
		PawnIds:         []string{"alpha", "beta"},
	}}}
}
func caravanDepartureAdmission() *r.Receipt {
	return &r.Receipt{Attempt: caravanDeparturePre().Attempt, AdmittedContext: &c.ObservationContext{Identity: pbIdentity(), Tick: proto.Int64(10), NativeGeneration: proto.Uint64(1)}, Outcome: &r.Receipt_Applied{Applied: &r.Applied{Observed: caravanDepartureEffectEvidence()}}}
}
func caravanDepartureAttemptFixture() CaravanDepartureAttempt {
	return CaravanDepartureAttempt{Identity: pbIdentity(), Attempt: caravanDeparturePre().Attempt, Generation: 1, CatalogToken: "catalog-token", PawnIDs: []string{"alpha", "beta"}, Cargo: []CaravanCargoSelection{{GroupID: "meals", Count: 10}}, DestinationTile: 42}
}

func TestPreviewCaravanDepartureAcceptedAndRejections(t *testing.T) {
	valid := &op.PreviewReply{Outcome: &op.PreviewReply_Evaluated{Evaluated: &op.PreviewEvaluation{
		Context:     pbContext(),
		Accepted:    proto.Bool(true),
		Preparation: &op.PreviewEvaluation_Caravan{Caravan: &op.CaravanPreparation{}},
		Projected:   &r.EffectEvidence{Effect: &r.EffectEvidence_Caravan{Caravan: &r.CaravanEffect{DestinationTile: proto.Int32(42), PawnIds: []string{"alpha", "beta"}, AssemblyStarted: proto.Bool(true)}}},
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
		{"missing caravan projection", func(v *op.PreviewReply) { v.GetEvaluated().Projected = &r.EffectEvidence{} }, false},
		{"destination mismatch", func(v *op.PreviewReply) { v.GetEvaluated().Projected.GetCaravan().DestinationTile = proto.Int32(7) }, false},
		{"crew mismatch", func(v *op.PreviewReply) { v.GetEvaluated().Projected.GetCaravan().PawnIds = []string{"alpha"} }, false},
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
				form := req.Operation.GetFormCaravan()
				if form.GetExpectedCatalogToken() != "catalog-token" || len(form.GetPawnIds()) != 2 || form.GetDestinationTile() != 42 {
					t.Fatal("request correlation lost", req)
				}
				return pbResult(reply), nil
			}}
			client := testClient(t, s, time.Second)
			_, raw, err := client.PreviewCaravanDeparture(context.Background(), pbIdentity(), "catalog-token", []string{"alpha", "beta"}, []CaravanCargoSelection{{GroupID: "meals", Count: 10}}, 42)
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

func TestPreviewCaravanDepartureInvalidInputsNeverDispatch(t *testing.T) {
	s := &testServer{schema: protoSchema}
	client := testClient(t, s, time.Second)
	for _, test := range []struct {
		token   string
		pawns   []string
		cargo   []CaravanCargoSelection
		tile    int32
		invalid bool
	}{
		{"", []string{"alpha"}, nil, 42, true},
		{"catalog-token", nil, nil, 42, true},
		{"catalog-token", []string{"alpha", "alpha"}, nil, 42, true},
		{"catalog-token", []string{""}, nil, 42, true},
		{"catalog-token", []string{"alpha"}, nil, -1, true},
		{"catalog-token", []string{"alpha"}, []CaravanCargoSelection{{GroupID: "meals", Count: 0}}, 42, true},
		{"catalog-token", []string{"alpha"}, []CaravanCargoSelection{{GroupID: "meals", Count: 1}, {GroupID: "meals", Count: 1}}, 42, true},
	} {
		if _, _, err := client.PreviewCaravanDeparture(context.Background(), pbIdentity(), test.token, test.pawns, test.cargo, test.tile); !errors.Is(err, ErrContract) {
			t.Fatal("invalid caravan departure command accepted", test, err)
		}
	}
	if len(s.calls) != 0 {
		t.Fatal("invalid input dispatched", s.calls)
	}
}

func TestApplyCaravanDepartureCorrelationAndOwnerMismatch(t *testing.T) {
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
		if !proto.Equal(req.Precondition, caravanDeparturePre()) || req.Operation.GetFormCaravan().GetExpectedCatalogToken() != "catalog-token" {
			t.Fatal("request correlation lost", req)
		}
		return pbResult(&op.ExecuteReply{Outcome: &op.ExecuteReply_Receipt{Receipt: caravanDepartureAdmission()}}), nil
	}}
	writer, err := NewCaravanDepartureWriter(testClient(t, s, time.Second))
	if err != nil {
		t.Fatal(err)
	}
	pre := caravanDeparturePre()
	cargo := []CaravanCargoSelection{{GroupID: "meals", Count: 10}}
	reply, raw, err := writer.ApplyCaravanDeparture(context.Background(), pre, "catalog-token", []string{"alpha", "beta"}, cargo, 42)
	if err != nil || reply.GetReceipt() == nil || len(raw.Envelope) == 0 {
		t.Fatal(err)
	}
	mismatched := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
		admission := caravanDepartureAdmission()
		admission.Attempt.AttemptId = proto.Uint64(999)
		return pbResult(&op.ExecuteReply{Outcome: &op.ExecuteReply_Receipt{Receipt: admission}}), nil
	}}
	writer, _ = NewCaravanDepartureWriter(testClient(t, mismatched, time.Second))
	if _, _, err = writer.ApplyCaravanDeparture(context.Background(), pre, "catalog-token", []string{"alpha", "beta"}, cargo, 42); !errors.Is(err, ErrContract) {
		t.Fatal("owner mismatch accepted", err)
	}
}

func TestApplyCaravanDepartureInvalidInputsNeverDispatch(t *testing.T) {
	s := &testServer{schema: protoSchema}
	writer, _ := NewCaravanDepartureWriter(testClient(t, s, time.Second))
	cargo := []CaravanCargoSelection{{GroupID: "meals", Count: 10}}
	for _, change := range []func(*a.WritePrecondition){
		func(v *a.WritePrecondition) { v.ExpectedGeneration = nil },
		func(v *a.WritePrecondition) { v.ExpectedGeneration = proto.Uint64(0) },
		func(v *a.WritePrecondition) { v.Attempt.AttemptId = nil },
	} {
		pre := caravanDeparturePre()
		change(pre)
		if _, _, err := writer.ApplyCaravanDeparture(context.Background(), pre, "catalog-token", []string{"alpha", "beta"}, cargo, 42); !errors.Is(err, ErrContract) {
			t.Fatal("invalid precondition accepted", err)
		}
	}
	if _, _, err := writer.ApplyCaravanDeparture(context.Background(), caravanDeparturePre(), "", []string{"alpha"}, cargo, 42); !errors.Is(err, ErrContract) {
		t.Fatal("missing catalog token accepted", err)
	}
	if len(s.calls) != 0 {
		t.Fatal("invalid input dispatched", s.calls)
	}
}

func TestLookupAndObserveCaravanDeparture(t *testing.T) {
	w := caravanDepartureAttemptFixture()
	admission := caravanDepartureAdmission()
	unknown := &r.LookupReply{Outcome: &r.LookupReply_Unknown{Unknown: &r.UnknownAttempt{Context: &c.ObservationContext{Identity: pbIdentity(), Tick: proto.Int64(1), NativeGeneration: proto.Uint64(1)}}}}
	s := &testServer{schema: protoSchema, handler: func(_ context.Context, args nativeArgument) (*mcp.CallToolResult, error) {
		if args.Tool != "rimgovernor/receipts_lookup" {
			t.Fatal(args.Tool)
		}
		return pbResult(unknown), nil
	}}
	client := testClient(t, s, time.Second)
	reply, _, err := client.LookupCaravanDeparture(context.Background(), w)
	if err != nil || reply.GetUnknown() == nil {
		t.Fatal("unknown attempt lookup failed", err)
	}
	completed := &r.Progress{Attempt: w.Attempt, Context: &c.ObservationContext{Identity: pbIdentity(), Tick: proto.Int64(11), NativeGeneration: proto.Uint64(1)}, CompleteInspection: proto.Bool(true), Effect: &r.Progress_Completed{Completed: &r.CompletedEffect{Evidence: caravanDepartureEffectEvidence()}}}
	s2 := &testServer{schema: protoSchema, handler: func(_ context.Context, args nativeArgument) (*mcp.CallToolResult, error) {
		if args.Tool != "rimgovernor/receipts_observe_progress" {
			t.Fatal(args.Tool)
		}
		return pbResult(&r.ProgressReply{Outcome: &r.ProgressReply_Progress{Progress: completed}}), nil
	}}
	client2 := testClient(t, s2, time.Second)
	if _, _, err = client2.ObserveCaravanDepartureProgress(context.Background(), w, admission); err != nil {
		t.Fatal("completed progress rejected", err)
	}
	for name, change := range map[string]func(*r.Progress){
		"incomplete inspection":   func(v *r.Progress) { v.CompleteInspection = proto.Bool(false) },
		"foreign pawn":            func(v *r.Progress) { v.GetCompleted().Evidence.GetCaravan().PawnIds = []string{"other"} },
		"destination mismatch":    func(v *r.Progress) { v.GetCompleted().Evidence.GetCaravan().DestinationTile = proto.Int32(7) },
		"stale tick before admit": func(v *r.Progress) { v.Context.Tick = proto.Int64(1) },
	} {
		t.Run(name, func(t *testing.T) {
			p := proto.Clone(completed).(*r.Progress)
			change(p)
			bad := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
				return pbResult(&r.ProgressReply{Outcome: &r.ProgressReply_Progress{Progress: p}}), nil
			}}
			if _, _, err := testClient(t, bad, time.Second).ObserveCaravanDepartureProgress(context.Background(), w, admission); !errors.Is(err, ErrContract) {
				t.Fatal("invalid completion accepted", err)
			}
		})
	}
	refusal := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
		out := pbResult(&op.ExecuteReply{Outcome: &op.ExecuteReply_Failure{Failure: &c.Failure{Code: c.FailureCode_FAILURE_CODE_AUTHORITY_REQUIRED.Enum()}}})
		out.IsError = true
		return out, nil
	}}
	cargo := []CaravanCargoSelection{{GroupID: "meals", Count: 10}}
	writer, _ := NewCaravanDepartureWriter(testClient(t, refusal, time.Second))
	if _, raw, err := writer.ApplyCaravanDeparture(context.Background(), caravanDeparturePre(), "catalog-token", []string{"alpha", "beta"}, cargo, 42); !errors.Is(err, ErrRefused) || len(raw.Envelope) == 0 {
		t.Fatal("typed refusal lost", err)
	}
	lost := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
		return nil, errors.New("lost after potential effect")
	}}
	writer, _ = NewCaravanDepartureWriter(testClient(t, lost, time.Second))
	if reply, _, err := writer.ApplyCaravanDeparture(context.Background(), caravanDeparturePre(), "catalog-token", []string{"alpha", "beta"}, cargo, 42); err == nil || reply != nil || len(lost.calls) != 1 {
		t.Fatal("lost reply fabricated outcome or retried", err)
	}
}
