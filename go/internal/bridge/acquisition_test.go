package bridge

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"github.com/modelcontextprotocol/go-sdk/mcp"
	"google.golang.org/protobuf/proto"
	"testing"
	"time"
)

func acquisitionTestTarget() AcquisitionTarget {
	v, _ := domain.NewAcquisition("plant", "WoodLog", domain.Cell{X: 1, Z: 2})
	return AcquisitionTarget{v, "cas"}
}
func acquisitionTestEffect() *r.EffectEvidence {
	return &r.EffectEvidence{Effect: &r.EffectEvidence_Acquisition{Acquisition: &r.AcquisitionEffect{SourceId: proto.String("plant"), ResourceDef: proto.String("WoodLog"), Cell: &c.Cell{X: proto.Int32(1), Z: proto.Int32(2)}, Designated: proto.Bool(true), LaborFinished: proto.Bool(false), ProducedUnits: proto.Int32(0), OutputComplete: proto.Bool(true), OutputObserved: proto.Bool(false)}}}
}
func TestAcquisitionFixedWriteAndExactAdmission(t *testing.T) {
	receipt := draftTestReceipt()
	receipt.Outcome = &r.Receipt_Applied{Applied: &r.Applied{Observed: acquisitionTestEffect()}}
	client := testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
		if arg.Tool != "rimgovernor/operations_execute" {
			t.Fatal(arg.Tool)
		}
		draftTestRequest(t, arg, &op.ExecuteRequest{Precondition: buildingPre(), Operation: acquisitionOperation(acquisitionTestTarget())})
		return pbResult(&op.ExecuteReply{Outcome: &op.ExecuteReply_Receipt{Receipt: receipt}}), nil
	}}, time.Second)
	writer, _ := NewAcquisitionControl(client)
	if _, _, err := writer.Acquire(context.Background(), buildingPre(), acquisitionTestTarget()); err != nil {
		t.Fatal(err)
	}
	want := AcquisitionAttempt{pbIdentity(), buildingPre().Attempt, 1, acquisitionTestTarget().Acquisition}
	for _, edit := range []func(*r.Receipt){
		func(v *r.Receipt) { v.Attempt.AttemptId = proto.Uint64(2) },
		func(v *r.Receipt) { v.GetApplied().Observed.GetAcquisition().SourceId = proto.String("other") },
		func(v *r.Receipt) { v.GetApplied().Observed.GetAcquisition().Cell.Z = proto.Int32(3) },
		func(v *r.Receipt) { v.GetApplied().Observed.GetAcquisition().ProducedUnits = proto.Int32(10) },
	} {
		v := proto.Clone(receipt).(*r.Receipt)
		edit(v)
		if acquisitionReceipt(v, want) == nil {
			t.Fatal("foreign or fabricated output admitted", v)
		}
	}
}

func TestAcquisitionOutputAccountingRejectsDuplicateAndMissingStacks(t *testing.T) {
	base := acquisitionTestEffect()
	d := base.GetAcquisition()
	d.LaborFinished = proto.Bool(true)
	d.Designated = proto.Bool(false)
	d.OutputObserved = proto.Bool(true)
	d.ProducedUnits = proto.Int32(10)
	d.Outputs = []*r.AcquisitionOutput{{ThingId: proto.String("stack"), Units: proto.Int32(10)}}
	if err := ValidateAcquisitionEffect(base, acquisitionTestTarget().Acquisition); err != nil {
		t.Fatal(err)
	}
	for _, edit := range []func(*r.AcquisitionEffect){
		func(v *r.AcquisitionEffect) { v.Outputs = nil },
		func(v *r.AcquisitionEffect) {
			v.Outputs = append(v.Outputs, v.Outputs[0])
			v.ProducedUnits = proto.Int32(20)
		},
		func(v *r.AcquisitionEffect) { v.Outputs[0].Units = nil },
		func(v *r.AcquisitionEffect) { v.OutputComplete = proto.Bool(false) },
	} {
		v := proto.Clone(base).(*r.EffectEvidence)
		edit(v.GetAcquisition())
		if ValidateAcquisitionEffect(v, acquisitionTestTarget().Acquisition) == nil {
			t.Fatal("invalid placement accounting", v)
		}
	}
}
