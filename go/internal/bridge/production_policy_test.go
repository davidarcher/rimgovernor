package bridge

import (
	"context"
	"encoding/json"
	"errors"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"github.com/modelcontextprotocol/go-sdk/mcp"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
)

func productionPolicyTargetFixture() ProductionPolicyTarget {
	return ProductionPolicyTarget{
		Floors:                map[policy.Resource]int64{"Steel": 100},
		Commitments:           map[policy.Resource]int64{"WoodLog": 50},
		Stopped:               []policy.Resource{"Silver"},
		Drills:                []ProductionDrillTarget{{BuildingDef: "DeepDrill", ResourceDef: "Steel", X: 1, Z: 2, StockTarget: 300}},
		ExpectedSnapshotToken: "production-policy-abc123",
	}
}
func productionPolicyPre() *a.WritePrecondition {
	return &a.WritePrecondition{Identity: pbIdentity(), ExpectedGeneration: proto.Uint64(1), Attempt: &c.AttemptKey{ControllerSessionId: proto.String("controller"), ActionId: proto.String("action"), AttemptId: proto.Uint64(1)}}
}
func productionPolicyEffectEvidence() *r.EffectEvidence {
	return &r.EffectEvidence{Effect: &r.EffectEvidence_ProductionPolicy{ProductionPolicy: &r.ProductionPolicyEffect{
		Snapshot: &r.SnapshotEvidence{EntityId: proto.String("1"), BeforeToken: proto.String("production-policy-abc123"), AfterToken: proto.String("production-policy-def456")},
		Changed:  proto.Bool(true), InterruptedPawnIds: []string{"pawn-1"},
	}}}
}
func productionPolicyAdmission() *r.Receipt {
	return &r.Receipt{Attempt: productionPolicyPre().Attempt, AdmittedContext: &c.ObservationContext{Identity: pbIdentity(), Tick: proto.Int64(10), NativeGeneration: proto.Uint64(1)}, Outcome: &r.Receipt_Applied{Applied: &r.Applied{Observed: productionPolicyEffectEvidence()}}}
}
func productionPolicyAttemptFixture() ProductionPolicyAttempt {
	return ProductionPolicyAttempt{Identity: pbIdentity(), Attempt: productionPolicyPre().Attempt, Generation: 1, Target: productionPolicyTargetFixture()}
}

func TestPreviewProductionPolicyAcceptedAndRejections(t *testing.T) {
	valid := &op.PreviewReply{Outcome: &op.PreviewReply_Evaluated{Evaluated: &op.PreviewEvaluation{Context: pbContext(), Accepted: proto.Bool(true)}}}
	for _, test := range []struct {
		name   string
		change func(*op.PreviewReply)
		ok     bool
	}{
		{"valid", func(*op.PreviewReply) {}, true},
		// Unlike a job-dispatch preview, SetProductionPolicy's evaluation carries
		// no projected job whose CanTry must agree with Accepted -- acceptance is
		// not authority (see the function comment), so Accepted=false is itself a
		// meaningful, structurally valid reply (e.g. the caller's snapshot token
		// is stale), not a contract violation.
		{"not accepted", func(v *op.PreviewReply) { v.GetEvaluated().Accepted = proto.Bool(false) }, true},
		{"missing accepted", func(v *op.PreviewReply) { v.GetEvaluated().Accepted = nil }, false},
		{"unexpected projection", func(v *op.PreviewReply) {
			v.GetEvaluated().Projected = &r.EffectEvidence{}
		}, false},
		{"unexpected preparation", func(v *op.PreviewReply) {
			v.GetEvaluated().Preparation = &op.PreviewEvaluation_Trade{Trade: &op.TradePreparation{}}
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
				set := req.Operation.GetSetProductionPolicy()
				if set.GetExpectedSnapshotToken() != "production-policy-abc123" || len(set.GetFloors().GetRows()) != 1 || set.GetFloors().GetRows()[0].GetDefName() != "Steel" {
					t.Fatal("request correlation lost", req)
				}
				return pbResult(reply), nil
			}}
			client := testClient(t, s, testBudget)
			_, raw, err := client.PreviewProductionPolicy(context.Background(), pbIdentity(), productionPolicyTargetFixture())
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

func TestPreviewProductionPolicyInvalidInputsNeverDispatch(t *testing.T) {
	s := &testServer{schema: protoSchema}
	client := testClient(t, s, testBudget)
	for name, change := range map[string]func(*ProductionPolicyTarget){
		"empty token":       func(v *ProductionPolicyTarget) { v.ExpectedSnapshotToken = "" },
		"negative floor":    func(v *ProductionPolicyTarget) { v.Floors["Steel"] = -1 },
		"oversized floor":   func(v *ProductionPolicyTarget) { v.Floors["Steel"] = 100001 },
		"invalid def name":  func(v *ProductionPolicyTarget) { v.Floors[""] = 1 },
		"duplicate stopped": func(v *ProductionPolicyTarget) { v.Stopped = []policy.Resource{"Silver", "Silver"} },
		"negative drill target": func(v *ProductionPolicyTarget) {
			v.Drills = []ProductionDrillTarget{{BuildingDef: "DeepDrill", ResourceDef: "Steel", StockTarget: -1}}
		},
		"duplicate drill position": func(v *ProductionPolicyTarget) {
			v.Drills = []ProductionDrillTarget{{BuildingDef: "DeepDrill", ResourceDef: "Steel"}, {BuildingDef: "DeepDrill", ResourceDef: "Steel"}}
		},
	} {
		t.Run(name, func(t *testing.T) {
			target := productionPolicyTargetFixture()
			change(&target)
			if _, _, err := client.PreviewProductionPolicy(context.Background(), pbIdentity(), target); !errors.Is(err, ErrContract) {
				t.Fatal("invalid production policy target accepted", err)
			}
		})
	}
	if len(s.calls) != 0 {
		t.Fatal("invalid input dispatched", s.calls)
	}
}

func TestApplyProductionPolicyCorrelationAndOwnerMismatch(t *testing.T) {
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
		if !proto.Equal(req.Precondition, productionPolicyPre()) || req.Operation.GetSetProductionPolicy().GetExpectedSnapshotToken() != "production-policy-abc123" {
			t.Fatal("request correlation lost", req)
		}
		return pbResult(&op.ExecuteReply{Outcome: &op.ExecuteReply_Receipt{Receipt: productionPolicyAdmission()}}), nil
	}}
	writer, err := NewProductionPolicyWriter(testClient(t, s, testBudget))
	if err != nil {
		t.Fatal(err)
	}
	pre := productionPolicyPre()
	reply, raw, err := writer.ApplyProductionPolicy(context.Background(), pre, productionPolicyTargetFixture())
	if err != nil || reply.GetReceipt() == nil || len(raw.Envelope) == 0 {
		t.Fatal(err)
	}
	mismatched := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
		admission := productionPolicyAdmission()
		admission.Attempt.AttemptId = proto.Uint64(999)
		return pbResult(&op.ExecuteReply{Outcome: &op.ExecuteReply_Receipt{Receipt: admission}}), nil
	}}
	writer, _ = NewProductionPolicyWriter(testClient(t, mismatched, testBudget))
	if _, _, err = writer.ApplyProductionPolicy(context.Background(), pre, productionPolicyTargetFixture()); !errors.Is(err, ErrContract) {
		t.Fatal("owner mismatch accepted", err)
	}
}

func TestApplyProductionPolicyInvalidInputsNeverDispatch(t *testing.T) {
	s := &testServer{schema: protoSchema}
	writer, _ := NewProductionPolicyWriter(testClient(t, s, testBudget))
	for _, change := range []func(*a.WritePrecondition){
		func(v *a.WritePrecondition) { v.ExpectedGeneration = nil },
		func(v *a.WritePrecondition) { v.ExpectedGeneration = proto.Uint64(0) },
		func(v *a.WritePrecondition) { v.Attempt.AttemptId = nil },
	} {
		pre := productionPolicyPre()
		change(pre)
		if _, _, err := writer.ApplyProductionPolicy(context.Background(), pre, productionPolicyTargetFixture()); !errors.Is(err, ErrContract) {
			t.Fatal("invalid precondition accepted", err)
		}
	}
	badTarget := productionPolicyTargetFixture()
	badTarget.ExpectedSnapshotToken = ""
	if _, _, err := writer.ApplyProductionPolicy(context.Background(), productionPolicyPre(), badTarget); !errors.Is(err, ErrContract) {
		t.Fatal("invalid target accepted", err)
	}
	if len(s.calls) != 0 {
		t.Fatal("invalid input dispatched", s.calls)
	}
}

func TestLookupAndObserveProductionPolicy(t *testing.T) {
	w := productionPolicyAttemptFixture()
	admission := productionPolicyAdmission()
	unknown := &r.LookupReply{Outcome: &r.LookupReply_Unknown{Unknown: &r.UnknownAttempt{Context: &c.ObservationContext{Identity: pbIdentity(), Tick: proto.Int64(1), NativeGeneration: proto.Uint64(1)}}}}
	s := &testServer{schema: protoSchema, handler: func(_ context.Context, args nativeArgument) (*mcp.CallToolResult, error) {
		if args.Tool != "rimgovernor/receipts_lookup" {
			t.Fatal(args.Tool)
		}
		return pbResult(unknown), nil
	}}
	client := testClient(t, s, testBudget)
	reply, _, err := client.LookupProductionPolicy(context.Background(), w)
	if err != nil || reply.GetUnknown() == nil {
		t.Fatal("unknown attempt lookup failed", err)
	}
	completed := &r.Progress{Attempt: w.Attempt, Context: &c.ObservationContext{Identity: pbIdentity(), Tick: proto.Int64(11), NativeGeneration: proto.Uint64(1)}, CompleteInspection: proto.Bool(true), Effect: &r.Progress_Completed{Completed: &r.CompletedEffect{Evidence: productionPolicyEffectEvidence()}}}
	s2 := &testServer{schema: protoSchema, handler: func(_ context.Context, args nativeArgument) (*mcp.CallToolResult, error) {
		if args.Tool != "rimgovernor/receipts_observe_progress" {
			t.Fatal(args.Tool)
		}
		return pbResult(&r.ProgressReply{Outcome: &r.ProgressReply_Progress{Progress: completed}}), nil
	}}
	client2 := testClient(t, s2, testBudget)
	if _, _, err = client2.ObserveProductionPolicyProgress(context.Background(), w, admission); err != nil {
		t.Fatal("completed progress rejected", err)
	}
	for name, change := range map[string]func(*r.Progress){
		"incomplete inspection": func(v *r.Progress) { v.CompleteInspection = proto.Bool(false) },
		"before token mismatch": func(v *r.Progress) {
			v.GetCompleted().Evidence.GetProductionPolicy().Snapshot.BeforeToken = proto.String("other")
		},
		"invalid after token": func(v *r.Progress) {
			v.GetCompleted().Evidence.GetProductionPolicy().Snapshot.AfterToken = proto.String("")
		},
		"stale tick before admit": func(v *r.Progress) { v.Context.Tick = proto.Int64(1) },
	} {
		t.Run(name, func(t *testing.T) {
			p := proto.Clone(completed).(*r.Progress)
			change(p)
			bad := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
				return pbResult(&r.ProgressReply{Outcome: &r.ProgressReply_Progress{Progress: p}}), nil
			}}
			if _, _, err := testClient(t, bad, testBudget).ObserveProductionPolicyProgress(context.Background(), w, admission); !errors.Is(err, ErrContract) {
				t.Fatal("invalid completion accepted", err)
			}
		})
	}
	refusal := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
		out := pbResult(&op.ExecuteReply{Outcome: &op.ExecuteReply_Failure{Failure: &c.Failure{Code: c.FailureCode_FAILURE_CODE_AUTHORITY_REQUIRED.Enum()}}})
		out.IsError = true
		return out, nil
	}}
	writer, _ := NewProductionPolicyWriter(testClient(t, refusal, testBudget))
	if _, raw, err := writer.ApplyProductionPolicy(context.Background(), productionPolicyPre(), productionPolicyTargetFixture()); !errors.Is(err, ErrRefused) || len(raw.Envelope) == 0 {
		t.Fatal("typed refusal lost", err)
	}
	lost := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
		return nil, errors.New("lost after potential effect")
	}}
	writer, _ = NewProductionPolicyWriter(testClient(t, lost, testBudget))
	if reply, _, err := writer.ApplyProductionPolicy(context.Background(), productionPolicyPre(), productionPolicyTargetFixture()); err == nil || reply != nil || len(lost.calls) != 1 {
		t.Fatal("lost reply fabricated outcome or retried", err)
	}
}

func productionPolicySnapshotFixture() *o.ProductionPolicySnapshot {
	return &o.ProductionPolicySnapshot{
		Snapshot:          &o.SnapshotRef{Context: pbContext(), EntityId: proto.String("1"), Token: proto.String("production-policy-abc123")},
		Floors:            []*o.Quantity{{DefName: proto.String("Steel"), Units: proto.Int64(100)}},
		Commitments:       []*o.Quantity{{DefName: proto.String("WoodLog"), Units: proto.Int64(50)}},
		StoppedDefs:       []string{"Silver"},
		Drills:            []*o.OwnedDrill{{DefName: proto.String("DeepDrill"), Cell: &c.Cell{X: proto.Int32(1), Z: proto.Int32(2)}, Resource: proto.String("Steel"), StockTarget: proto.Int64(300), Recovered: proto.Int64(10), Missing: proto.Bool(false)}},
		CommitmentsActive: proto.Bool(true),
	}
}

func TestReadProductionPolicyDecodesAndRejectsMalformed(t *testing.T) {
	valid := productionPolicySnapshotFixture()
	s := &testServer{schema: protoSchema, handler: func(_ context.Context, args nativeArgument) (*mcp.CallToolResult, error) {
		if args.Tool != "rimgovernor/observations_read_production_policy" {
			t.Fatal(args.Tool)
		}
		return pbResult(&o.ProductionPolicyReply{Outcome: &o.ProductionPolicyReply_Observed{Observed: valid}}), nil
	}}
	client := testClient(t, s, testBudget)
	read, _, err := client.ReadProductionPolicy(context.Background(), pbIdentity())
	if err != nil {
		t.Fatal(err)
	}
	if read.SnapshotToken != "production-policy-abc123" || read.Floors["Steel"] != 100 || read.Commitments["WoodLog"] != 50 ||
		len(read.Stopped) != 1 || read.Stopped[0] != "Silver" || len(read.Drills) != 1 || read.Drills[0].DefName != "DeepDrill" || !read.CommitmentsActive {
		t.Fatal("production policy decode lost fields", read)
	}
	for name, change := range map[string]func(*o.ProductionPolicySnapshot){
		"missing snapshot":       func(v *o.ProductionPolicySnapshot) { v.Snapshot = nil },
		"invalid token":          func(v *o.ProductionPolicySnapshot) { v.Snapshot.Token = proto.String("") },
		"negative floor":         func(v *o.ProductionPolicySnapshot) { v.Floors[0].Units = proto.Int64(-1) },
		"duplicate floor":        func(v *o.ProductionPolicySnapshot) { v.Floors = append(v.Floors, v.Floors[0]) },
		"invalid stopped":        func(v *o.ProductionPolicySnapshot) { v.StoppedDefs = []string{""} },
		"duplicate drill":        func(v *o.ProductionPolicySnapshot) { v.Drills = append(v.Drills, v.Drills[0]) },
		"foreign world identity": func(v *o.ProductionPolicySnapshot) { v.Snapshot.Context.Identity.LoadToken = proto.String("other") },
	} {
		t.Run(name, func(t *testing.T) {
			bad := proto.Clone(valid).(*o.ProductionPolicySnapshot)
			change(bad)
			s := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
				return pbResult(&o.ProductionPolicyReply{Outcome: &o.ProductionPolicyReply_Observed{Observed: bad}}), nil
			}}
			if _, _, err := testClient(t, s, testBudget).ReadProductionPolicy(context.Background(), pbIdentity()); !errors.Is(err, ErrContract) {
				t.Fatal("malformed production policy snapshot accepted", err)
			}
		})
	}
}
