package bridge

import (
	"context"
	"encoding/json"
	"errors"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"github.com/modelcontextprotocol/go-sdk/mcp"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
)

func billFoodTarget(t *testing.T) domain.ProductionBill {
	t.Helper()
	b, err := domain.NewProductionBill("stove", "CookMealSimple", "before-token", domain.FoodTarget, 10)
	if err != nil {
		t.Fatal(err)
	}
	return b
}
func billPre() *a.WritePrecondition {
	return &a.WritePrecondition{Identity: pbIdentity(), ExpectedGeneration: proto.Uint64(1), Attempt: &c.AttemptKey{ControllerSessionId: proto.String("controller"), ActionId: proto.String("action"), AttemptId: proto.Uint64(1)}}
}
func billEffectEvidence() *r.EffectEvidence {
	return &r.EffectEvidence{Effect: &r.EffectEvidence_Bill{Bill: &r.BillEffect{
		Stack:                &r.SnapshotEvidence{EntityId: proto.String("stove"), BeforeToken: proto.String("before-token"), AfterToken: proto.String("after-token")},
		BillId:               proto.String("bill1"),
		RecipeDef:            proto.String("CookMealSimple"),
		Present:              proto.Bool(true),
		Index:                proto.Int32(0),
		OrderedBillIds:       []string{"bill1"},
		ConfigurationMatches: proto.Bool(true),
		Iterations:           proto.Uint32(1),
		OutputComplete:       proto.Bool(true),
		OutputObserved:       proto.Bool(true),
		Outputs:              []*r.ProductionOutput{{ThingId: proto.String("meal1"), DefName: proto.String("MealSimple"), Units: proto.Int32(1)}},
	}}}
}
func billAdmission() *r.Receipt {
	return &r.Receipt{Attempt: billPre().Attempt, AdmittedContext: &c.ObservationContext{Identity: pbIdentity(), Tick: proto.Int64(10), NativeGeneration: proto.Uint64(1)}, Outcome: &r.Receipt_Applied{Applied: &r.Applied{Observed: billEffectEvidence()}}}
}
func billAttempt(t *testing.T) BillAttempt {
	return BillAttempt{Identity: pbIdentity(), Attempt: billPre().Attempt, Generation: 1, Bill: billFoodTarget(t)}
}

func TestBillOperationSettingsByMode(t *testing.T) {
	target, err := domain.NewProductionBill("stove", "CookMealSimple", "before-token", domain.FoodTarget, 10)
	if err != nil {
		t.Fatal(err)
	}
	ops := BillOperation(target)
	add := ops.GetAddBill()
	if add.GetBench().GetEntityId() != "stove" || add.GetBench().GetExpectedSnapshotToken() != "before-token" || add.GetRecipeDef() != "CookMealSimple" {
		t.Fatal("lost bench/recipe identity", add)
	}
	if add.Settings.GetRepeatMode() != op.RepeatMode_REPEAT_MODE_TARGET || add.Settings.GetTargetCount() != 10 || add.Settings.GetUnpauseThreshold() != 5 || !add.Settings.GetPauseWhenSatisfied() {
		t.Fatal("unexpected food-target settings", add.Settings)
	}
	if add.Settings.GetSuspended() || add.Settings.GetStore().GetMode() != op.StoreMode_STORE_MODE_DROP_ON_FLOOR {
		t.Fatal("unexpected shared settings", add.Settings)
	}
	forever, err := domain.NewProductionBill("butcher-table", "ButcherCorpseFlesh", "before-token", domain.ButcherForever, 0)
	if err != nil {
		t.Fatal(err)
	}
	fAdd := BillOperation(forever).GetAddBill()
	if fAdd.Settings.GetRepeatMode() != op.RepeatMode_REPEAT_MODE_FOREVER || fAdd.Settings.TargetCount != nil {
		t.Fatal("unexpected butcher-forever settings", fAdd.Settings)
	}
	// StockTarget (GearProduce, MaintainResource-* and MaintainMedicalReserves'
	// shared "keep at least Target in stock" mode) reuses FoodTarget's
	// pause-when-satisfied settings shape.
	stock, err := domain.NewProductionBill("tailor", "MakeParka", "before-token", domain.StockTarget, 1)
	if err != nil {
		t.Fatal(err)
	}
	sAdd := BillOperation(stock).GetAddBill()
	if sAdd.Settings.GetRepeatMode() != op.RepeatMode_REPEAT_MODE_TARGET || sAdd.Settings.GetTargetCount() != 1 || sAdd.Settings.GetUnpauseThreshold() != 1 || !sAdd.Settings.GetPauseWhenSatisfied() {
		t.Fatal("unexpected stock-target settings", sAdd.Settings)
	}
	if _, err := domain.NewProductionBill("tailor", "MakeParka", "before-token", domain.StockTarget, 0); err == nil {
		t.Fatal("expected zero stock target to be rejected")
	}
}

func TestPreviewBillAcceptedAndRejections(t *testing.T) {
	target := billFoodTarget(t)
	valid := &op.PreviewReply{Outcome: &op.PreviewReply_Evaluated{Evaluated: &op.PreviewEvaluation{Context: pbContext(), Accepted: proto.Bool(true)}}}
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
		{"unexpected projection", func(v *op.PreviewReply) { v.GetEvaluated().Projected = billEffectEvidence() }, false},
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
				if req.Operation.GetAddBill().GetBench().GetEntityId() != "stove" || req.Operation.GetAddBill().GetRecipeDef() != "CookMealSimple" {
					t.Fatal("request correlation lost", req)
				}
				return pbResult(reply), nil
			}}
			client := testClient(t, s, time.Second)
			_, raw, err := client.PreviewBill(context.Background(), pbIdentity(), target)
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

func TestAddBillReceiptCorrelationAndOwnerMismatch(t *testing.T) {
	target := billFoodTarget(t)
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
		if !proto.Equal(req.Precondition, billPre()) || req.Operation.GetAddBill().GetRecipeDef() != "CookMealSimple" {
			t.Fatal("request correlation lost", req)
		}
		return pbResult(&op.ExecuteReply{Outcome: &op.ExecuteReply_Receipt{Receipt: billAdmission()}}), nil
	}}
	control, err := NewBillControl(testClient(t, s, time.Second))
	if err != nil {
		t.Fatal(err)
	}
	reply, raw, err := control.AddBill(context.Background(), billPre(), target)
	if err != nil || reply.GetReceipt() == nil || len(raw.Envelope) == 0 {
		t.Fatal(err)
	}
	mismatched := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
		admission := billAdmission()
		admission.Attempt.AttemptId = proto.Uint64(999)
		return pbResult(&op.ExecuteReply{Outcome: &op.ExecuteReply_Receipt{Receipt: admission}}), nil
	}}
	control, _ = NewBillControl(testClient(t, mismatched, time.Second))
	if _, _, err = control.AddBill(context.Background(), billPre(), target); !errors.Is(err, ErrContract) {
		t.Fatal("owner mismatch accepted", err)
	}
}

func TestAddBillInvalidInputsNeverDispatch(t *testing.T) {
	s := &testServer{schema: protoSchema}
	control, _ := NewBillControl(testClient(t, s, time.Second))
	target := billFoodTarget(t)
	for _, change := range []func(*a.WritePrecondition){
		func(v *a.WritePrecondition) { v.ExpectedGeneration = nil },
		func(v *a.WritePrecondition) { v.ExpectedGeneration = proto.Uint64(0) },
		func(v *a.WritePrecondition) { v.Attempt.AttemptId = nil },
	} {
		pre := billPre()
		change(pre)
		if _, _, err := control.AddBill(context.Background(), pre, target); !errors.Is(err, ErrContract) {
			t.Fatal("invalid precondition accepted", err)
		}
	}
	bad, err := domain.NewProductionBill("", "CookMealSimple", "before-token", domain.FoodTarget, 10)
	if err == nil {
		if _, _, err := control.AddBill(context.Background(), billPre(), bad); !errors.Is(err, ErrContract) {
			t.Fatal("invalid bill accepted", err)
		}
	}
	if len(s.calls) != 0 {
		t.Fatal("invalid input dispatched", s.calls)
	}
}

func TestValidateBillEffectMutations(t *testing.T) {
	target := billFoodTarget(t)
	if err := ValidateBillEffect(billEffectEvidence(), target); err != nil {
		t.Fatal("valid completed effect rejected", err)
	}
	for name, change := range map[string]func(*r.EffectEvidence){
		"foreign bench":        func(v *r.EffectEvidence) { v.GetBill().Stack.EntityId = proto.String("other") },
		"foreign before token": func(v *r.EffectEvidence) { v.GetBill().Stack.BeforeToken = proto.String("other") },
		"foreign recipe":       func(v *r.EffectEvidence) { v.GetBill().RecipeDef = proto.String("CookMealFine") },
		"missing bill id":      func(v *r.EffectEvidence) { v.GetBill().BillId = nil },
		"missing present":      func(v *r.EffectEvidence) { v.GetBill().Present = nil },
		"index out of range":   func(v *r.EffectEvidence) { v.GetBill().Index = proto.Int32(5) },
		"index/id mismatch":    func(v *r.EffectEvidence) { v.GetBill().OrderedBillIds = []string{"other"} },
		"invalid after token":  func(v *r.EffectEvidence) { v.GetBill().Stack.AfterToken = proto.String("") },
		"iteration overflow":   func(v *r.EffectEvidence) { v.GetBill().Iterations = proto.Uint32(2) },
		"too many order ids": func(v *r.EffectEvidence) {
			ids := make([]string, 16)
			for i := range ids {
				ids[i] = "id"
			}
			v.GetBill().OrderedBillIds = ids
		},
		"duplicate order id": func(v *r.EffectEvidence) { v.GetBill().OrderedBillIds = []string{"bill1", "bill1"} },
		"output missing id":  func(v *r.EffectEvidence) { v.GetBill().Outputs[0].ThingId = nil },
		"output zero units":  func(v *r.EffectEvidence) { v.GetBill().Outputs[0].Units = proto.Int32(0) },
		"duplicate output": func(v *r.EffectEvidence) {
			v.GetBill().Outputs = append(v.GetBill().Outputs, proto.Clone(v.GetBill().Outputs[0]).(*r.ProductionOutput))
		},
		"observed without complete": func(v *r.EffectEvidence) { v.GetBill().OutputComplete = proto.Bool(false) },
		"observed without outputs":  func(v *r.EffectEvidence) { v.GetBill().Outputs = nil },
		"outputs without iteration": func(v *r.EffectEvidence) { v.GetBill().Iterations = proto.Uint32(0) },
	} {
		t.Run(name, func(t *testing.T) {
			v := proto.Clone(billEffectEvidence()).(*r.EffectEvidence)
			change(v)
			if err := ValidateBillEffect(v, target); !errors.Is(err, ErrContract) {
				t.Fatal("invalid production evidence accepted", err)
			}
		})
	}
	absent := &r.EffectEvidence{Effect: &r.EffectEvidence_Bill{Bill: &r.BillEffect{
		Stack: &r.SnapshotEvidence{EntityId: proto.String("stove"), BeforeToken: proto.String("before-token")}, BillId: proto.String("bill1"), RecipeDef: proto.String("CookMealSimple"),
		Present: proto.Bool(false), Index: proto.Int32(-1), OrderedBillIds: nil, ConfigurationMatches: proto.Bool(false), Iterations: proto.Uint32(0), OutputComplete: proto.Bool(false), OutputObserved: proto.Bool(false),
	}}}
	if err := ValidateBillEffect(absent, target); err != nil {
		t.Fatal("valid absent effect rejected", err)
	}
	contradiction := proto.Clone(absent).(*r.EffectEvidence)
	contradiction.GetBill().ConfigurationMatches = proto.Bool(true)
	if err := ValidateBillEffect(contradiction, target); !errors.Is(err, ErrContract) {
		t.Fatal("absent bill with matching configuration accepted", err)
	}
	pending := &r.EffectEvidence{Effect: &r.EffectEvidence_Bill{Bill: &r.BillEffect{
		Stack: &r.SnapshotEvidence{EntityId: proto.String("stove"), BeforeToken: proto.String("before-token"), AfterToken: proto.String("after-token")}, BillId: proto.String("bill1"), RecipeDef: proto.String("CookMealSimple"),
		Present: proto.Bool(true), Index: proto.Int32(0), OrderedBillIds: []string{"bill1"}, ConfigurationMatches: proto.Bool(true), Iterations: proto.Uint32(0), OutputComplete: proto.Bool(false), OutputObserved: proto.Bool(false),
	}}}
	if err := ValidateBillEffect(pending, target); err != nil {
		t.Fatal("valid pending effect rejected", err)
	}
}

func TestLookupAndObserveBill(t *testing.T) {
	w := billAttempt(t)
	admission := billAdmission()
	unknown := &r.LookupReply{Outcome: &r.LookupReply_Unknown{Unknown: &r.UnknownAttempt{Context: &c.ObservationContext{Identity: pbIdentity(), Tick: proto.Int64(1), NativeGeneration: proto.Uint64(1)}}}}
	s := &testServer{schema: protoSchema, handler: func(_ context.Context, args nativeArgument) (*mcp.CallToolResult, error) {
		if args.Tool != "rimgovernor/receipts_lookup" {
			t.Fatal(args.Tool)
		}
		return pbResult(unknown), nil
	}}
	client := testClient(t, s, time.Second)
	reply, _, err := client.LookupBill(context.Background(), w)
	if err != nil || reply.GetUnknown() == nil {
		t.Fatal("unknown attempt lookup failed", err)
	}
	completed := &r.Progress{Attempt: w.Attempt, Context: &c.ObservationContext{Identity: pbIdentity(), Tick: proto.Int64(11), NativeGeneration: proto.Uint64(1)}, CompleteInspection: proto.Bool(true), Effect: &r.Progress_Completed{Completed: &r.CompletedEffect{Evidence: billEffectEvidence()}}}
	s2 := &testServer{schema: protoSchema, handler: func(_ context.Context, args nativeArgument) (*mcp.CallToolResult, error) {
		if args.Tool != "rimgovernor/receipts_observe_progress" {
			t.Fatal(args.Tool)
		}
		return pbResult(&r.ProgressReply{Outcome: &r.ProgressReply_Progress{Progress: completed}}), nil
	}}
	client2 := testClient(t, s2, time.Second)
	if _, _, err = client2.ObserveBill(context.Background(), w, admission); err != nil {
		t.Fatal("completed progress rejected", err)
	}
	for name, change := range map[string]func(*r.Progress){
		"incomplete inspection": func(v *r.Progress) { v.CompleteInspection = proto.Bool(false) },
		"configuration drifted": func(v *r.Progress) { v.GetCompleted().Evidence.GetBill().ConfigurationMatches = proto.Bool(false) },
		"no iterations": func(v *r.Progress) {
			v.GetCompleted().Evidence.GetBill().Iterations = proto.Uint32(0)
			v.GetCompleted().Evidence.GetBill().Outputs = nil
		},
		"output not observed":     func(v *r.Progress) { v.GetCompleted().Evidence.GetBill().OutputObserved = proto.Bool(false) },
		"stale tick before admit": func(v *r.Progress) { v.Context.Tick = proto.Int64(1) },
	} {
		t.Run(name, func(t *testing.T) {
			p := proto.Clone(completed).(*r.Progress)
			change(p)
			bad := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
				return pbResult(&r.ProgressReply{Outcome: &r.ProgressReply_Progress{Progress: p}}), nil
			}}
			if _, _, err := testClient(t, bad, time.Second).ObserveBill(context.Background(), w, admission); !errors.Is(err, ErrContract) {
				t.Fatal("invalid completion accepted", err)
			}
		})
	}
	refusal := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
		out := pbResult(&op.ExecuteReply{Outcome: &op.ExecuteReply_Failure{Failure: &c.Failure{Code: c.FailureCode_FAILURE_CODE_AUTHORITY_REQUIRED.Enum()}}})
		out.IsError = true
		return out, nil
	}}
	control, _ := NewBillControl(testClient(t, refusal, time.Second))
	if _, raw, err := control.AddBill(context.Background(), billPre(), billFoodTarget(t)); !errors.Is(err, ErrRefused) || len(raw.Envelope) == 0 {
		t.Fatal("typed refusal lost", err)
	}
	lost := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
		return nil, errors.New("lost after potential effect")
	}}
	control, _ = NewBillControl(testClient(t, lost, time.Second))
	if reply, _, err := control.AddBill(context.Background(), billPre(), billFoodTarget(t)); err == nil || reply != nil || len(lost.calls) != 1 {
		t.Fatal("lost reply fabricated outcome or retried", err)
	}
}

func TestReadBillTarget(t *testing.T) {
	fixture := productionFixture(t)
	fixture.Planning = &o.PlanningSection{Outcome: &o.PlanningSection_Unavailable{Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_REQUESTED.Enum()}}}
	fixture.Cooking[0].Bench.Snapshot = &o.SnapshotRef{Context: proto.Clone(fixture.Context).(*c.ObservationContext), EntityId: proto.String("stove"), Token: proto.String("stove-token")}
	stacks := &o.BillsReply{Outcome: &o.BillsReply_Observed{Observed: &o.BillsSnapshot{Context: proto.Clone(fixture.Context).(*c.ObservationContext),
		Benches:      []*o.BillStack{{Snapshot: &o.SnapshotRef{Context: proto.Clone(fixture.Context).(*c.ObservationContext), EntityId: proto.String("spot"), Token: proto.String("spot-token")}, Bench: &o.EntityRef{Id: proto.String("spot")}}},
		Completeness: &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}}}}}
	recipes := &o.RecipesReply{Outcome: &o.RecipesReply_Observed{Observed: &o.RecipesSnapshot{Context: proto.Clone(fixture.Context).(*c.ObservationContext), Snapshot: &o.SnapshotRef{EntityId: proto.String("spot")}, Completeness: &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}}}}}
	s := &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
		switch arg.Tool {
		case "rimgovernor/observations_read_bills":
			return pbResult(stacks), nil
		case "rimgovernor/observations_read_recipes":
			return pbResult(recipes), nil
		}
		return pbResult(&o.ColonyFactsReply{Outcome: &o.ColonyFactsReply_Observed{Observed: fixture}}), nil
	}}
	client := testClient(t, s, time.Second)
	read, _, err := client.ReadBillTarget(context.Background(), fixture.Context.Identity, "stove")
	if err != nil || read.Token != "stove-token" {
		t.Fatal("bench token lost", read, err)
	}
	// A bench outside the cooking/butchering rows resolves through the generic stack census.
	if read, _, err = client.ReadBillTarget(context.Background(), fixture.Context.Identity, "spot"); err != nil || read.Token != "spot-token" {
		t.Fatal("generic bench token lost", read, err)
	}
	if _, _, err = client.ReadBillTarget(context.Background(), fixture.Context.Identity, "missing"); !errors.Is(err, ErrUnavailable) {
		t.Fatal("unknown bench treated as found", err)
	}
	duplicate := proto.Clone(fixture).(*o.ColonyFactsSnapshot)
	duplicate.Butchering = []*o.ButcheringFacts{{Bench: &o.EntityRef{Id: proto.String("stove"), DefName: proto.String("TableButcher"), MapId: proto.Int32(0), Position: &c.Cell{X: proto.Int32(2), Z: proto.Int32(2)}}, Usable: proto.Bool(true)}}
	sDup := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
		return pbResult(&o.ColonyFactsReply{Outcome: &o.ColonyFactsReply_Observed{Observed: duplicate}}), nil
	}}
	if _, _, err = testClient(t, sDup, time.Second).ReadBillTarget(context.Background(), fixture.Context.Identity, "stove"); !errors.Is(err, ErrContract) {
		t.Fatal("duplicate bench id across cooking/butchering accepted", err)
	}
}
