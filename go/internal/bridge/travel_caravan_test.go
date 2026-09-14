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

func travelCaravanPre() *a.WritePrecondition {
	return &a.WritePrecondition{Identity: pbIdentity(), ExpectedGeneration: proto.Uint64(1), LeaseId: proto.String("lease"), Attempt: &c.AttemptKey{ControllerSessionId: proto.String("controller"), ActionId: proto.String("action"), AttemptId: proto.Uint64(1)}}
}
func travelCaravanEffectEvidence() *r.EffectEvidence {
	return &r.EffectEvidence{Effect: &r.EffectEvidence_Caravan{Caravan: &r.CaravanEffect{
		CaravanId:       proto.String("caravan-1"),
		PathStarted:     proto.Bool(true),
		DestinationTile: proto.Int32(42),
	}}}
}
func travelCaravanStopEffectEvidence() *r.EffectEvidence {
	return &r.EffectEvidence{Effect: &r.EffectEvidence_Caravan{Caravan: &r.CaravanEffect{
		CaravanId: proto.String("caravan-1"),
		Stopped:   proto.Bool(true),
	}}}
}
func travelCaravanAdmission() *r.Receipt {
	return &r.Receipt{Attempt: travelCaravanPre().Attempt, AdmittedContext: &c.ObservationContext{Identity: pbIdentity(), Tick: proto.Int64(10), NativeGeneration: proto.Uint64(1)}, AuthorizingOwner: &a.Owner{ControllerSessionId: proto.String("controller"), PlayerDirection: proto.Uint64(1)}, Outcome: &r.Receipt_Applied{Applied: &r.Applied{Observed: travelCaravanEffectEvidence()}}}
}
func travelCaravanAttemptFixture() TravelCaravanAttempt {
	admission := travelCaravanAdmission()
	return TravelCaravanAttempt{Identity: pbIdentity(), Attempt: travelCaravanPre().Attempt, Owner: admission.AuthorizingOwner, Generation: 1, Caravan: "caravan-1", CaravanToken: "caravan-token", Kind: op.TravelKind_TRAVEL_KIND_MOVE, DestinationTile: 42}
}

func TestPreviewTravelCaravanAcceptedAndRejections(t *testing.T) {
	valid := &op.PreviewReply{Outcome: &op.PreviewReply_Evaluated{Evaluated: &op.PreviewEvaluation{
		Context:     pbContext(),
		Accepted:    proto.Bool(true),
		Preparation: &op.PreviewEvaluation_Caravan{Caravan: &op.CaravanPreparation{}},
		Projected:   &r.EffectEvidence{Effect: &r.EffectEvidence_Caravan{Caravan: &r.CaravanEffect{DestinationTile: proto.Int32(42), PathStarted: proto.Bool(true)}}},
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
				travel := req.Operation.GetTravelCaravan()
				if travel.GetCaravan().GetEntityId() != "caravan-1" || travel.GetCaravan().GetExpectedSnapshotToken() != "caravan-token" || travel.GetDestinationTile() != 42 {
					t.Fatal("request correlation lost", req)
				}
				return pbResult(reply), nil
			}}
			client := testClient(t, s, time.Second)
			_, raw, err := client.PreviewTravelCaravan(context.Background(), pbIdentity(), "caravan-1", "caravan-token", op.TravelKind_TRAVEL_KIND_MOVE, 42)
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

func TestPreviewTravelCaravanInvalidInputsNeverDispatch(t *testing.T) {
	s := &testServer{schema: protoSchema}
	client := testClient(t, s, time.Second)
	for _, test := range []struct {
		caravan string
		token   string
		kind    op.TravelKind
		tile    int32
	}{
		{"", "caravan-token", op.TravelKind_TRAVEL_KIND_MOVE, 42},
		{"caravan-1", "", op.TravelKind_TRAVEL_KIND_MOVE, 42},
		{"caravan-1", "caravan-token", op.TravelKind_TRAVEL_KIND_UNSPECIFIED, 42},
		{"caravan-1", "caravan-token", op.TravelKind_TRAVEL_KIND_MOVE, -1},
		{"caravan-1", "caravan-token", op.TravelKind_TRAVEL_KIND_STOP, 0},
		{"caravan-1", "caravan-token", op.TravelKind_TRAVEL_KIND_RETURN_HOME, 5},
	} {
		if _, _, err := client.PreviewTravelCaravan(context.Background(), pbIdentity(), test.caravan, test.token, test.kind, test.tile); !errors.Is(err, ErrContract) {
			t.Fatal("invalid travel caravan command accepted", test, err)
		}
	}
	if len(s.calls) != 0 {
		t.Fatal("invalid input dispatched", s.calls)
	}
}

func TestApplyTravelCaravanCorrelationAndOwnerMismatch(t *testing.T) {
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
		if !proto.Equal(req.Precondition, travelCaravanPre()) || req.Operation.GetTravelCaravan().GetCaravan().GetEntityId() != "caravan-1" {
			t.Fatal("request correlation lost", req)
		}
		return pbResult(&op.ExecuteReply{Outcome: &op.ExecuteReply_Receipt{Receipt: travelCaravanAdmission()}}), nil
	}}
	writer, err := NewTravelCaravanWriter(testClient(t, s, time.Second))
	if err != nil {
		t.Fatal(err)
	}
	pre := travelCaravanPre()
	owner := travelCaravanAdmission().AuthorizingOwner
	reply, raw, err := writer.ApplyTravelCaravan(context.Background(), pre, owner, "caravan-1", "caravan-token", op.TravelKind_TRAVEL_KIND_MOVE, 42)
	if err != nil || reply.GetReceipt() == nil || len(raw.Envelope) == 0 {
		t.Fatal(err)
	}
	mismatched := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
		admission := travelCaravanAdmission()
		admission.AuthorizingOwner.ControllerSessionId = proto.String("someone-else")
		return pbResult(&op.ExecuteReply{Outcome: &op.ExecuteReply_Receipt{Receipt: admission}}), nil
	}}
	writer, _ = NewTravelCaravanWriter(testClient(t, mismatched, time.Second))
	if _, _, err = writer.ApplyTravelCaravan(context.Background(), pre, owner, "caravan-1", "caravan-token", op.TravelKind_TRAVEL_KIND_MOVE, 42); !errors.Is(err, ErrContract) {
		t.Fatal("owner mismatch accepted", err)
	}
}

func TestApplyTravelCaravanStopCarriesNoDestination(t *testing.T) {
	s := &testServer{schema: protoSchema, handler: func(_ context.Context, args nativeArgument) (*mcp.CallToolResult, error) {
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
		if req.Operation.GetTravelCaravan().DestinationTile != nil {
			t.Fatal("stop carried a destination tile", req)
		}
		admission := travelCaravanAdmission()
		admission.GetApplied().Observed = travelCaravanStopEffectEvidence()
		return pbResult(&op.ExecuteReply{Outcome: &op.ExecuteReply_Receipt{Receipt: admission}}), nil
	}}
	writer, err := NewTravelCaravanWriter(testClient(t, s, time.Second))
	if err != nil {
		t.Fatal(err)
	}
	pre := travelCaravanPre()
	owner := travelCaravanAdmission().AuthorizingOwner
	if _, _, err := writer.ApplyTravelCaravan(context.Background(), pre, owner, "caravan-1", "caravan-token", op.TravelKind_TRAVEL_KIND_STOP, -1); err != nil {
		t.Fatal(err)
	}
	if _, _, err := writer.ApplyTravelCaravan(context.Background(), pre, owner, "caravan-1", "caravan-token", op.TravelKind_TRAVEL_KIND_STOP, 0); !errors.Is(err, ErrContract) {
		t.Fatal("stop with a destination accepted", err)
	}
}

func TestApplyTravelCaravanInvalidInputsNeverDispatch(t *testing.T) {
	s := &testServer{schema: protoSchema}
	writer, _ := NewTravelCaravanWriter(testClient(t, s, time.Second))
	owner := travelCaravanAdmission().AuthorizingOwner
	for _, change := range []func(*a.WritePrecondition){
		func(v *a.WritePrecondition) { v.ExpectedGeneration = nil },
		func(v *a.WritePrecondition) { v.ExpectedGeneration = proto.Uint64(0) },
		func(v *a.WritePrecondition) { v.LeaseId = nil },
		func(v *a.WritePrecondition) { v.Attempt.AttemptId = nil },
	} {
		pre := travelCaravanPre()
		change(pre)
		if _, _, err := writer.ApplyTravelCaravan(context.Background(), pre, owner, "caravan-1", "caravan-token", op.TravelKind_TRAVEL_KIND_MOVE, 42); !errors.Is(err, ErrContract) {
			t.Fatal("invalid precondition accepted", err)
		}
	}
	if _, _, err := writer.ApplyTravelCaravan(context.Background(), travelCaravanPre(), owner, "", "caravan-token", op.TravelKind_TRAVEL_KIND_MOVE, 42); !errors.Is(err, ErrContract) {
		t.Fatal("missing caravan id accepted", err)
	}
	if len(s.calls) != 0 {
		t.Fatal("invalid input dispatched", s.calls)
	}
}

func TestLookupAndObserveTravelCaravan(t *testing.T) {
	w := travelCaravanAttemptFixture()
	admission := travelCaravanAdmission()
	unknown := &r.LookupReply{Outcome: &r.LookupReply_Unknown{Unknown: &r.UnknownAttempt{Context: &c.ObservationContext{Identity: pbIdentity(), Tick: proto.Int64(1), NativeGeneration: proto.Uint64(1)}}}}
	s := &testServer{schema: protoSchema, handler: func(_ context.Context, args nativeArgument) (*mcp.CallToolResult, error) {
		if args.Tool != "rimgovernor/receipts_lookup" {
			t.Fatal(args.Tool)
		}
		return pbResult(unknown), nil
	}}
	client := testClient(t, s, time.Second)
	reply, _, err := client.LookupTravelCaravan(context.Background(), w)
	if err != nil || reply.GetUnknown() == nil {
		t.Fatal("unknown attempt lookup failed", err)
	}
	completed := &r.Progress{Attempt: w.Attempt, Context: &c.ObservationContext{Identity: pbIdentity(), Tick: proto.Int64(11), NativeGeneration: proto.Uint64(1)}, CompleteInspection: proto.Bool(true), Effect: &r.Progress_Completed{Completed: &r.CompletedEffect{Evidence: travelCaravanEffectEvidence()}}}
	s2 := &testServer{schema: protoSchema, handler: func(_ context.Context, args nativeArgument) (*mcp.CallToolResult, error) {
		if args.Tool != "rimgovernor/receipts_observe_progress" {
			t.Fatal(args.Tool)
		}
		return pbResult(&r.ProgressReply{Outcome: &r.ProgressReply_Progress{Progress: completed}}), nil
	}}
	client2 := testClient(t, s2, time.Second)
	if _, _, err = client2.ObserveTravelCaravanProgress(context.Background(), w, admission); err != nil {
		t.Fatal("completed progress rejected", err)
	}
	for name, change := range map[string]func(*r.Progress){
		"incomplete inspection":   func(v *r.Progress) { v.CompleteInspection = proto.Bool(false) },
		"foreign caravan":         func(v *r.Progress) { v.GetCompleted().Evidence.GetCaravan().CaravanId = proto.String("other") },
		"destination mismatch":    func(v *r.Progress) { v.GetCompleted().Evidence.GetCaravan().DestinationTile = proto.Int32(7) },
		"missing path started":    func(v *r.Progress) { v.GetCompleted().Evidence.GetCaravan().PathStarted = nil },
		"stale tick before admit": func(v *r.Progress) { v.Context.Tick = proto.Int64(1) },
	} {
		t.Run(name, func(t *testing.T) {
			p := proto.Clone(completed).(*r.Progress)
			change(p)
			bad := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
				return pbResult(&r.ProgressReply{Outcome: &r.ProgressReply_Progress{Progress: p}}), nil
			}}
			if _, _, err := testClient(t, bad, time.Second).ObserveTravelCaravanProgress(context.Background(), w, admission); !errors.Is(err, ErrContract) {
				t.Fatal("invalid completion accepted", err)
			}
		})
	}
	refusal := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
		out := pbResult(&op.ExecuteReply{Outcome: &op.ExecuteReply_Failure{Failure: &c.Failure{Code: c.FailureCode_FAILURE_CODE_AUTHORITY_REQUIRED.Enum()}}})
		out.IsError = true
		return out, nil
	}}
	writer, _ := NewTravelCaravanWriter(testClient(t, refusal, time.Second))
	if _, raw, err := writer.ApplyTravelCaravan(context.Background(), travelCaravanPre(), admission.AuthorizingOwner, "caravan-1", "caravan-token", op.TravelKind_TRAVEL_KIND_MOVE, 42); !errors.Is(err, ErrRefused) || len(raw.Envelope) == 0 {
		t.Fatal("typed refusal lost", err)
	}
	lost := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
		return nil, errors.New("lost after potential effect")
	}}
	writer, _ = NewTravelCaravanWriter(testClient(t, lost, time.Second))
	if reply, _, err := writer.ApplyTravelCaravan(context.Background(), travelCaravanPre(), admission.AuthorizingOwner, "caravan-1", "caravan-token", op.TravelKind_TRAVEL_KIND_MOVE, 42); err == nil || reply != nil || len(lost.calls) != 1 {
		t.Fatal("lost reply fabricated outcome or retried", err)
	}
}

func TestTravelCaravanTokenMatchesInternal(t *testing.T) {
	if TravelCaravanToken("caravan-1", 42, true) != travelCaravanToken("caravan-1", 42, true) {
		t.Fatal("exported token diverged from internal computation")
	}
}
