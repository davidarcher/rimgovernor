package supply

import (
	"context"
	"errors"
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
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
	*boundary.Fixture
	target    bridge.SupplyTarget
	read      bridge.SupplyRead
	projected *r.EffectEvidence
	refused   bool
}

func (f *supplyBoundaryFixture) ReadAllowSupplies(context.Context, *c.Identity, domain.Cell) (bridge.SupplyRead, bridge.Result, error) {
	return f.read, bridge.Result{}, nil
}
func (f *supplyBoundaryFixture) PreviewSupplyAllow(context.Context, *c.Identity, bridge.SupplyTarget) (*op.PreviewReply, bridge.Result, error) {
	return &op.PreviewReply{Outcome: &op.PreviewReply_Evaluated{Evaluated: &op.PreviewEvaluation{Context: proto.Clone(f.Receipt.AdmittedContext).(*c.ObservationContext), Accepted: proto.Bool(!f.refused), Projected: f.projected}}}, bridge.Result{}, nil
}
func (f *supplyBoundaryFixture) AllowSupply(_ context.Context, pre *a.WritePrecondition, target bridge.SupplyTarget) (*op.ExecuteReply, bridge.Result, error) {
	f.Places++
	f.LastPre = pre
	return &op.ExecuteReply{Outcome: &op.ExecuteReply_Receipt{Receipt: f.Receipt}}, bridge.Result{}, nil
}
func (f *supplyBoundaryFixture) LookupSupplyAllow(context.Context, bridge.SupplyAttempt) (*r.LookupReply, bridge.Result, error) {
	f.Lookups++
	return &r.LookupReply{Outcome: &r.LookupReply_Receipt{Receipt: f.Receipt}}, bridge.Result{}, nil
}
func (f *supplyBoundaryFixture) ObserveSupplyAllow(context.Context, bridge.SupplyAttempt, *r.Receipt) (*r.ProgressReply, bridge.Result, error) {
	f.Observes++
	return &r.ProgressReply{Outcome: &r.ProgressReply_Progress{Progress: f.Progress}}, bridge.Result{}, nil
}
func newSupplyBoundaryFixture(t *testing.T) (*SupplyBoundary, *supplyBoundaryFixture) {
	b, base := boundary.NewFixture(t)
	supply, _ := domain.NewSupplyAllow("steel", "Steel", domain.Cell{X: 1, Z: 2})
	action, _ := domain.NewSupplyAllowAction("action", supply)
	base.Placement.Action = action
	effect := &r.EffectEvidence{Effect: &r.EffectEvidence_Designation{Designation: &r.DesignationEffect{ThingId: proto.String("steel"), ResourceDef: proto.String("Steel"), DesignationDef: proto.String("Allow"), Present: proto.Bool(true), Cell: &c.Cell{X: proto.Int32(1), Z: proto.Int32(2)}}}}
	base.Receipt.Outcome = &r.Receipt_Applied{Applied: &r.Applied{Observed: proto.Clone(effect).(*r.EffectEvidence)}}
	base.Progress.Effect = &r.Progress_Completed{Completed: &r.CompletedEffect{Evidence: proto.Clone(effect).(*r.EffectEvidence)}}
	base.Emergency.Context.Tick = proto.Int64(10)
	f := &supplyBoundaryFixture{Fixture: base, target: bridge.SupplyTarget{Supply: supply, Token: "token"}, projected: effect}
	f.read = bridge.SupplyRead{Context: proto.Clone(base.Receipt.AdmittedContext).(*c.ObservationContext), Targets: []bridge.SupplyTarget{f.target}}
	return &SupplyBoundary{Boundary: b, supply: SupplyCapabilities{Native: f, Writer: f}}, f
}
func TestSupplyBoundaryLeaseFreeReadbackAndExactPrecondition(t *testing.T) {
	t.Parallel()
	b, f := newSupplyBoundaryFixture(t)
	ctx := context.Background()
	p := f.Placement
	inspection, err := b.InspectSupply(ctx, executor.Target{Action: p.Action, Snapshot: p.Snapshot})
	if err != nil || !inspection.Accepted || f.Leases != 0 {
		t.Fatal(inspection, err)
	}
	receipt, err := b.AllowSupply(ctx, executor.SupplyDispatch{Attempt: p, SnapshotToken: inspection.SnapshotToken})
	if err != nil || receipt.Kind != domain.ReceiptAccepted || f.Leases != 1 || f.Places != 1 || !proto.Equal(f.LastPre.Attempt, f.Receipt.Attempt) {
		t.Fatal(receipt, err)
	}
	evidence, err := b.ObserveSupply(ctx, p, p.Snapshot)
	if err != nil || evidence.Observation.Effect != domain.EffectCompleted || f.Leases != 1 || f.Places != 1 {
		t.Fatal(evidence, err)
	}
}

// A fresh cell read without the exact item is the absent sentinel, so the
// executor can settle the proposal instead of holding it (#114); a preview
// refusal on a present item still only holds.
func TestSupplyBoundaryReportsAbsentTarget(t *testing.T) {
	t.Parallel()
	b, f := newSupplyBoundaryFixture(t)
	p := f.Placement
	f.read.Targets = nil
	if _, err := b.InspectSupply(context.Background(), executor.Target{Action: p.Action, Snapshot: p.Snapshot}); !errors.Is(err, executor.ErrSupplyAbsent) {
		t.Fatal(err)
	}
	f.read.Targets, f.refused = []bridge.SupplyTarget{f.target}, true
	if _, err := b.InspectSupply(context.Background(), executor.Target{Action: p.Action, Snapshot: p.Snapshot}); !errors.Is(err, executor.ErrHeld) || errors.Is(err, executor.ErrSupplyAbsent) {
		t.Fatal(err)
	}
}
// The clock may run between the cell read, the preview and the emergency
// read: the inspection accepts ticks in that order (the token binds the
// item) and holds only when one of them predates the read before it (#120).
func TestSupplyBoundaryAcceptsAdvancingTicksAcrossItsReads(t *testing.T) {
	t.Parallel()
	b, f := newSupplyBoundaryFixture(t)
	p := f.Placement
	target := executor.Target{Action: p.Action, Snapshot: p.Snapshot}
	previewTick := f.Receipt.AdmittedContext.GetTick()
	f.read.Context.Tick = proto.Int64(previewTick - 5)
	f.Emergency.Context.Tick = proto.Int64(previewTick + 7)
	inspection, err := b.InspectSupply(context.Background(), target)
	if err != nil || !inspection.Accepted || inspection.Tick != domain.Tick(previewTick) || inspection.SnapshotToken != "token" {
		t.Fatal(inspection, err)
	}
	f.read.Context.Tick = proto.Int64(previewTick + 1)
	if _, err := b.InspectSupply(context.Background(), target); !errors.Is(err, executor.ErrHeld) {
		t.Fatal(err)
	}
	f.read.Context.Tick = proto.Int64(previewTick)
	f.Emergency.Context.Tick = proto.Int64(previewTick - 1)
	if _, err := b.InspectSupply(context.Background(), target); !errors.Is(err, executor.ErrHeld) {
		t.Fatal(err)
	}
}
func TestSupplyBoundaryRejectsForeignAndIncompleteEvidence(t *testing.T) {
	t.Parallel()
	for _, kind := range []string{"item", "cell", "incomplete", "reason", "world"} {
		t.Run(kind, func(t *testing.T) {
			b, f := newSupplyBoundaryFixture(t)
			switch kind {
			case "item":
				f.Progress.GetCompleted().Evidence.GetDesignation().ThingId = proto.String("other")
			case "cell":
				f.Progress.GetCompleted().Evidence.GetDesignation().Cell.X = nil
			case "incomplete":
				f.Progress.CompleteInspection = proto.Bool(false)
			case "world":
				f.Progress.Context.Identity.LoadToken = proto.String("other")
			case "reason":
				f.Progress.Effect = &r.Progress_Unsuccessful{Unsuccessful: &r.UnsuccessfulEffect{}}
			}
			if _, err := b.ObserveSupply(context.Background(), f.Placement, f.Placement.Snapshot); err == nil {
				t.Fatal("accepted mismatching outcome")
			}
			if f.Leases != 0 || f.Places != 0 {
				t.Fatal("observation wrote")
			}
		})
	}
}
