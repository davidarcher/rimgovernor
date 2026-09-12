package buildingruntime

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
	"testing"
)

type supplyBoundaryFixture struct {
	*boundaryFixture
	target    bridge.SupplyTarget
	read      bridge.SupplyRead
	projected *r.EffectEvidence
}

func (f *supplyBoundaryFixture) ReadAllowSupplies(context.Context, *c.Identity, domain.Cell) (bridge.SupplyRead, bridge.Result, error) {
	return f.read, bridge.Result{}, nil
}
func (f *supplyBoundaryFixture) PreviewSupplyAllow(context.Context, *c.Identity, bridge.SupplyTarget) (*op.PreviewReply, bridge.Result, error) {
	return &op.PreviewReply{Outcome: &op.PreviewReply_Evaluated{Evaluated: &op.PreviewEvaluation{Context: proto.Clone(f.receipt.AdmittedContext).(*c.ObservationContext), Accepted: proto.Bool(true), Projected: f.projected}}}, bridge.Result{}, nil
}
func (f *supplyBoundaryFixture) AllowSupply(_ context.Context, pre *a.WritePrecondition, target bridge.SupplyTarget) (*op.ExecuteReply, bridge.Result, error) {
	f.places++
	f.lastPre = pre
	return &op.ExecuteReply{Outcome: &op.ExecuteReply_Receipt{Receipt: f.receipt}}, bridge.Result{}, nil
}
func (f *supplyBoundaryFixture) LookupSupplyAllow(context.Context, bridge.SupplyAttempt) (*r.LookupReply, bridge.Result, error) {
	f.lookups++
	return &r.LookupReply{Outcome: &r.LookupReply_Receipt{Receipt: f.receipt}}, bridge.Result{}, nil
}
func (f *supplyBoundaryFixture) ObserveSupplyAllow(context.Context, bridge.SupplyAttempt, *r.Receipt) (*r.ProgressReply, bridge.Result, error) {
	f.observes++
	return &r.ProgressReply{Outcome: &r.ProgressReply_Progress{Progress: f.progress}}, bridge.Result{}, nil
}
func newSupplyBoundaryFixture(t *testing.T) (*supplyBoundary, *supplyBoundaryFixture) {
	b, base := newBoundaryFixture(t)
	supply, _ := domain.NewSupplyAllow("steel", "Steel", domain.Cell{X: 1, Z: 2})
	action, _ := domain.NewSupplyAllowAction("action", supply)
	base.placement.Action = action
	effect := &r.EffectEvidence{Effect: &r.EffectEvidence_Designation{Designation: &r.DesignationEffect{ThingId: proto.String("steel"), ResourceDef: proto.String("Steel"), DesignationDef: proto.String("Allow"), Present: proto.Bool(true), Cell: &c.Cell{X: proto.Int32(1), Z: proto.Int32(2)}}}}
	base.receipt.Outcome = &r.Receipt_Applied{Applied: &r.Applied{Observed: proto.Clone(effect).(*r.EffectEvidence)}}
	base.progress.Effect = &r.Progress_Completed{Completed: &r.CompletedEffect{Evidence: proto.Clone(effect).(*r.EffectEvidence)}}
	base.emergency.Context.Tick = proto.Int64(10)
	f := &supplyBoundaryFixture{boundaryFixture: base, target: bridge.SupplyTarget{Supply: supply, Token: "token"}, projected: effect}
	f.read = bridge.SupplyRead{Context: proto.Clone(base.receipt.AdmittedContext).(*c.ObservationContext), Targets: []bridge.SupplyTarget{f.target}}
	return &supplyBoundary{Boundary: b, supply: SupplyCapabilities{Native: f, Writer: f}}, f
}
func TestSupplyBoundaryLeaseFreeReadbackAndExactPrecondition(t *testing.T) {
	b, f := newSupplyBoundaryFixture(t)
	ctx := context.Background()
	p := f.placement
	inspection, err := b.InspectSupply(ctx, executor.Target{Action: p.Action, Snapshot: p.Snapshot})
	if err != nil || !inspection.Accepted || f.leases != 0 {
		t.Fatal(inspection, err)
	}
	receipt, err := b.AllowSupply(ctx, executor.SupplyDispatch{Attempt: p, SnapshotToken: inspection.SnapshotToken})
	if err != nil || receipt.Kind != domain.ReceiptAccepted || f.leases != 1 || f.places != 1 || !proto.Equal(f.lastPre.Attempt, f.receipt.Attempt) {
		t.Fatal(receipt, err)
	}
	evidence, err := b.ObserveSupply(ctx, p, p.Snapshot)
	if err != nil || evidence.Observation.Effect != domain.EffectCompleted || f.leases != 1 || f.places != 1 {
		t.Fatal(evidence, err)
	}
}
func TestSupplyBoundaryRejectsForeignAndIncompleteEvidence(t *testing.T) {
	for _, kind := range []string{"item", "cell", "incomplete", "reason", "world"} {
		t.Run(kind, func(t *testing.T) {
			b, f := newSupplyBoundaryFixture(t)
			switch kind {
			case "item":
				f.progress.GetCompleted().Evidence.GetDesignation().ThingId = proto.String("other")
			case "cell":
				f.progress.GetCompleted().Evidence.GetDesignation().Cell.X = nil
			case "incomplete":
				f.progress.CompleteInspection = proto.Bool(false)
			case "world":
				f.progress.Context.Identity.LoadToken = proto.String("other")
			case "reason":
				f.progress.Effect = &r.Progress_Unsuccessful{Unsuccessful: &r.UnsuccessfulEffect{}}
			}
			if _, err := b.ObserveSupply(context.Background(), f.placement, f.placement.Snapshot); err == nil {
				t.Fatal("accepted mismatching outcome")
			}
			if f.leases != 0 || f.places != 0 {
				t.Fatal("observation wrote")
			}
		})
	}
}
