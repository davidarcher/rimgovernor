package bridge

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"github.com/modelcontextprotocol/go-sdk/mcp"
	"google.golang.org/protobuf/proto"
	"testing"
	"time"
)

func supplyTestTarget() SupplyTarget {
	s, _ := domain.NewSupplyAllow("steel", "Steel", domain.Cell{X: 1, Z: 2})
	return SupplyTarget{s, "snapshot"}
}
func supplyTestAttempt() SupplyAttempt {
	return SupplyAttempt{pbIdentity(), buildingPre().Attempt, 1, supplyTestTarget().Supply}
}
func supplyTestEffect() *r.EffectEvidence {
	return &r.EffectEvidence{Effect: &r.EffectEvidence_Designation{Designation: &r.DesignationEffect{ThingId: proto.String("steel"), ResourceDef: proto.String("Steel"), DesignationDef: proto.String("Allow"), Present: proto.Bool(true), Cell: &c.Cell{X: proto.Int32(1), Z: proto.Int32(2)}}}}
}
func supplyTestReceipt() *r.Receipt {
	v := draftTestReceipt()
	v.Outcome = &r.Receipt_Applied{Applied: &r.Applied{Observed: supplyTestEffect()}}
	return v
}
func supplyTestRead() *o.ListSuppliesReply {
	ctx := buildingAdmission().AdmittedContext
	item := &o.EntityRef{Id: proto.String("steel"), DefName: proto.String("Steel"), MapId: pbIdentity().MapId, Position: &c.Cell{X: proto.Int32(1), Z: proto.Int32(2)}, Snapshot: &o.SnapshotRef{Context: proto.Clone(ctx).(*c.ObservationContext), EntityId: proto.String("steel"), Token: proto.String("snapshot")}}
	return &o.ListSuppliesReply{Outcome: &o.ListSuppliesReply_Observed{Observed: &o.SuppliesSnapshot{Context: ctx, Completeness: emergencyCounts(1), Stocks: []*o.ResourceStock{{Definition: &o.DefinitionRef{DefName: proto.String("Steel")}, Units: proto.Int64(20), Forbidden: proto.Int64(20), Items: []*o.EntityRef{item}, ItemsCompleteness: emergencyCounts(1)}}}}}
}
func TestSupplyCensusRequiresCompleteExactScopedItems(t *testing.T) {
	for name, edit := range map[string]func(*o.SuppliesSnapshot){
		"page":       func(v *o.SuppliesSnapshot) { v.Completeness.Page.Complete = proto.Bool(false) },
		"unreadable": func(v *o.SuppliesSnapshot) { v.Stocks[0].ItemsCompleteness.Unreadable = proto.Uint64(1) },
		"allowed":    func(v *o.SuppliesSnapshot) { v.Stocks[0].Forbidden = proto.Int64(19) },
		"duplicate": func(v *o.SuppliesSnapshot) {
			v.Stocks[0].Items = append(v.Stocks[0].Items, proto.Clone(v.Stocks[0].Items[0]).(*o.EntityRef))
			v.Stocks[0].ItemsCompleteness = emergencyCounts(2)
		},
		"cell":     func(v *o.SuppliesSnapshot) { v.Stocks[0].Items[0].Position.X = proto.Int32(3) },
		"snapshot": func(v *o.SuppliesSnapshot) { v.Stocks[0].Items[0].Snapshot.EntityId = proto.String("foreign") },
		"snapshot world": func(v *o.SuppliesSnapshot) {
			v.Stocks[0].Items[0].Snapshot.Context.Identity.LoadToken = proto.String("other")
		},
		"unknown fields": func(v *o.SuppliesSnapshot) { v.ProtoReflect().SetUnknown([]byte{0x78, 1}) },
	} {
		t.Run(name, func(t *testing.T) {
			v := supplyTestRead()
			edit(v.GetObserved())
			if _, err := decodeAllowSupplies(v, pbIdentity(), supplyTestTarget().Supply.Cell()); err == nil {
				t.Fatal("accepted incomplete or foreign census")
			}
		})
	}
	v := supplyTestRead()
	got, err := decodeAllowSupplies(v, pbIdentity(), supplyTestTarget().Supply.Cell())
	if err != nil || len(got.Targets) != 1 || got.Targets[0] != supplyTestTarget() {
		t.Fatal(got, err)
	}
	v.GetObserved().Stocks[0].Items[0].Snapshot = nil
	got, err = decodeAllowSupplies(v, pbIdentity(), supplyTestTarget().Supply.Cell())
	if err != nil || len(got.Targets) != 0 {
		t.Fatal("ineligible item became target", got, err)
	}
}
func TestSupplyFixedCapabilityAndReceiptCorrelation(t *testing.T) {
	calls := 0
	client := testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
		calls++
		if arg.Tool != "rimgovernor/operations_execute" {
			t.Fatal(arg.Tool)
		}
		draftTestRequest(t, arg, &op.ExecuteRequest{Precondition: buildingPre(), Operation: supplyOperation(supplyTestTarget())})
		return pbResult(&op.ExecuteReply{Outcome: &op.ExecuteReply_Receipt{Receipt: supplyTestReceipt()}}), nil
	}}, time.Second)
	writer, err := NewSupplyControl(client)
	if err != nil {
		t.Fatal(err)
	}
	if _, _, err = writer.AllowSupply(context.Background(), buildingPre(), supplyTestTarget()); err != nil || calls != 1 {
		t.Fatal(calls, err)
	}
	for name, edit := range map[string]func(*r.Receipt){
		"world":     func(v *r.Receipt) { v.AdmittedContext.Identity.LoadToken = proto.String("other") },
		"attempt":   func(v *r.Receipt) { v.Attempt.AttemptId = proto.Uint64(2) },
		"item":      func(v *r.Receipt) { v.GetApplied().Observed.GetDesignation().ThingId = proto.String("other") },
		"forbidden": func(v *r.Receipt) { v.GetApplied().Observed.GetDesignation().Present = proto.Bool(false) },
		"cell":      func(v *r.Receipt) { v.GetApplied().Observed.GetDesignation().Cell = nil },
	} {
		t.Run(name, func(t *testing.T) {
			v := supplyTestReceipt()
			edit(v)
			if err := supplyReceipt(v, supplyTestAttempt()); err == nil {
				t.Fatal("accepted mismatching receipt")
			}
		})
	}
}
func TestSupplyProgressRequiresObservedOutcome(t *testing.T) {
	for _, kind := range []string{"completed", "incomplete", "forbidden", "foreign", "unknown"} {
		t.Run(kind, func(t *testing.T) {
			evidence := supplyTestEffect()
			v := &r.Progress{Attempt: buildingPre().Attempt, Context: buildingAdmission().AdmittedContext, CompleteInspection: proto.Bool(kind != "incomplete"), Effect: &r.Progress_Completed{Completed: &r.CompletedEffect{Evidence: evidence}}}
			if kind == "foreign" {
				evidence.GetDesignation().ThingId = proto.String("foreign")
			}
			if kind == "forbidden" {
				evidence.GetDesignation().Present = proto.Bool(false)
				v.Effect = &r.Progress_Unsuccessful{Unsuccessful: &r.UnsuccessfulEffect{Reason: r.UnsuccessfulReason_UNSUCCESSFUL_REASON_OUTCOME_NOT_ACHIEVED.Enum(), Evidence: evidence}}
			}
			if kind == "unknown" {
				v.Effect = &r.Progress_Unknown{Unknown: &r.UnknownEffect{}}
			}
			client := testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
				if arg.Tool != "rimgovernor/receipts_observe_progress" {
					t.Fatal(arg.Tool)
				}
				return pbResult(&r.ProgressReply{Outcome: &r.ProgressReply_Progress{Progress: v}}), nil
			}}, time.Second)
			_, _, err := client.ObserveSupplyAllow(context.Background(), supplyTestAttempt(), supplyTestReceipt())
			if (err != nil) != (kind == "foreign" || kind == "incomplete") {
				t.Fatal(kind, err)
			}
		})
	}
}
