package cutplant

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

type cutPlantBoundaryFixture struct {
	*boundary.Fixture
	target    bridge.CutPlantTarget
	read      bridge.CutPlantRead
	projected *r.EffectEvidence
	refused   bool
}

func (f *cutPlantBoundaryFixture) ReadBlightedPlants(context.Context, *c.Identity) (bridge.CutPlantRead, bridge.Result, error) {
	return f.read, bridge.Result{}, nil
}
func (f *cutPlantBoundaryFixture) PreviewCutPlant(context.Context, *c.Identity, bridge.CutPlantTarget) (*op.PreviewReply, bridge.Result, error) {
	return &op.PreviewReply{Outcome: &op.PreviewReply_Evaluated{Evaluated: &op.PreviewEvaluation{Context: proto.Clone(f.Receipt.AdmittedContext).(*c.ObservationContext), Accepted: proto.Bool(!f.refused), Projected: f.projected}}}, bridge.Result{}, nil
}
func (f *cutPlantBoundaryFixture) DesignateCutPlant(_ context.Context, pre *a.WritePrecondition, target bridge.CutPlantTarget) (*op.ExecuteReply, bridge.Result, error) {
	f.Places++
	f.LastPre = pre
	return &op.ExecuteReply{Outcome: &op.ExecuteReply_Receipt{Receipt: f.Receipt}}, bridge.Result{}, nil
}
func (f *cutPlantBoundaryFixture) LookupCutPlant(context.Context, bridge.CutPlantAttempt) (*r.LookupReply, bridge.Result, error) {
	f.Lookups++
	return &r.LookupReply{Outcome: &r.LookupReply_Receipt{Receipt: f.Receipt}}, bridge.Result{}, nil
}
func (f *cutPlantBoundaryFixture) ObserveCutPlant(context.Context, bridge.CutPlantAttempt, *r.Receipt) (*r.ProgressReply, bridge.Result, error) {
	f.Observes++
	return &r.ProgressReply{Outcome: &r.ProgressReply_Progress{Progress: f.Progress}}, bridge.Result{}, nil
}
func newCutPlantBoundaryFixture(t *testing.T) (*CutPlantBoundary, *cutPlantBoundaryFixture) {
	b, base := boundary.NewFixture(t)
	plant, _ := domain.NewCutPlant("Plant_Rice1", "Plant_Rice", domain.Cell{X: 1, Z: 2})
	action, _ := domain.NewCutPlantAction("action", plant)
	base.Placement.Action = action
	effect := &r.EffectEvidence{Effect: &r.EffectEvidence_Designation{Designation: &r.DesignationEffect{ThingId: proto.String("Plant_Rice1"), ResourceDef: proto.String("Plant_Rice"), DesignationDef: proto.String("CutPlant"), Present: proto.Bool(true), Cell: &c.Cell{X: proto.Int32(1), Z: proto.Int32(2)}}}}
	base.Receipt.Outcome = &r.Receipt_Applied{Applied: &r.Applied{Observed: proto.Clone(effect).(*r.EffectEvidence)}}
	base.Progress.Effect = &r.Progress_Completed{Completed: &r.CompletedEffect{Evidence: proto.Clone(effect).(*r.EffectEvidence)}}
	base.Emergency.Context.Tick = proto.Int64(10)
	f := &cutPlantBoundaryFixture{Fixture: base, target: bridge.CutPlantTarget{Plant: plant, Token: "token"}, projected: effect}
	f.read = bridge.CutPlantRead{Context: proto.Clone(base.Receipt.AdmittedContext).(*c.ObservationContext), Targets: []bridge.CutPlantTarget{f.target}}
	return &CutPlantBoundary{Boundary: b, cutPlant: CutPlantCapabilities{Native: f, Writer: f}}, f
}
func TestCutPlantBoundaryLeaseFreeReadbackAndExactPrecondition(t *testing.T) {
	t.Parallel()
	b, f := newCutPlantBoundaryFixture(t)
	ctx := context.Background()
	p := f.Placement
	inspection, err := b.InspectCutPlant(ctx, executor.Target{Action: p.Action, Snapshot: p.Snapshot})
	if err != nil || !inspection.Accepted || f.Leases != 0 {
		t.Fatal(inspection, err)
	}
	receipt, err := b.DesignateCutPlant(ctx, executor.CutPlantDispatch{Attempt: p, SnapshotToken: inspection.SnapshotToken})
	if err != nil || receipt.Kind != domain.ReceiptAccepted || f.Leases != 1 || f.Places != 1 || !proto.Equal(f.LastPre.Attempt, f.Receipt.Attempt) {
		t.Fatal(receipt, err)
	}
	evidence, err := b.ObserveCutPlant(ctx, p, p.Snapshot)
	if err != nil || evidence.Observation.Effect != domain.EffectCompleted || f.Leases != 1 || f.Places != 1 {
		t.Fatal(evidence, err)
	}
}

// A fresh census without the exact undesignated plant is the absent
// sentinel, so the executor can settle the proposal instead of holding it; a
// preview refusal on a present plant still only holds.
func TestCutPlantBoundaryReportsAbsentTarget(t *testing.T) {
	t.Parallel()
	b, f := newCutPlantBoundaryFixture(t)
	p := f.Placement
	f.read.Targets = nil
	if _, err := b.InspectCutPlant(context.Background(), executor.Target{Action: p.Action, Snapshot: p.Snapshot}); !errors.Is(err, executor.ErrCutPlantAbsent) {
		t.Fatal(err)
	}
	f.read.Targets, f.refused = []bridge.CutPlantTarget{f.target}, true
	if _, err := b.InspectCutPlant(context.Background(), executor.Target{Action: p.Action, Snapshot: p.Snapshot}); !errors.Is(err, executor.ErrHeld) || errors.Is(err, executor.ErrCutPlantAbsent) {
		t.Fatal(err)
	}
}

// The clock may run between the census, the preview and the emergency
// read: the inspection accepts ticks in that order (the token binds the
// plant), tolerates a cached emergency read within its family's tick
// tolerance (#243), and holds when the preview predates the census or the
// emergency read is older than that.
func TestCutPlantBoundaryAcceptsAdvancingTicksAcrossItsReads(t *testing.T) {
	t.Parallel()
	b, f := newCutPlantBoundaryFixture(t)
	p := f.Placement
	target := executor.Target{Action: p.Action, Snapshot: p.Snapshot}
	previewTick := f.Receipt.AdmittedContext.GetTick() + 2*bridge.FactEmergency.TickTolerance()
	f.Receipt.AdmittedContext.Tick = proto.Int64(previewTick)
	f.read.Context.Tick = proto.Int64(previewTick - 5)
	f.Emergency.Context.Tick = proto.Int64(previewTick + 7)
	inspection, err := b.InspectCutPlant(context.Background(), target)
	if err != nil || !inspection.Accepted || inspection.Tick != domain.Tick(previewTick) || inspection.SnapshotToken != "token" {
		t.Fatal(inspection, err)
	}
	f.read.Context.Tick = proto.Int64(previewTick + 1)
	if _, err := b.InspectCutPlant(context.Background(), target); !errors.Is(err, executor.ErrHeld) {
		t.Fatal(err)
	}
	f.read.Context.Tick = proto.Int64(previewTick)
	f.Emergency.Context.Tick = proto.Int64(previewTick - bridge.FactEmergency.TickTolerance())
	if inspection, err := b.InspectCutPlant(context.Background(), target); err != nil || !inspection.Accepted {
		t.Fatal(inspection, err)
	}
	f.Emergency.Context.Tick = proto.Int64(previewTick - bridge.FactEmergency.TickTolerance() - 1)
	if _, err := b.InspectCutPlant(context.Background(), target); !errors.Is(err, executor.ErrHeld) {
		t.Fatal(err)
	}
}
func TestCutPlantBoundaryRejectsForeignAndIncompleteEvidence(t *testing.T) {
	t.Parallel()
	for _, kind := range []string{"item", "cell", "incomplete", "reason", "world"} {
		t.Run(kind, func(t *testing.T) {
			b, f := newCutPlantBoundaryFixture(t)
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
			if _, err := b.ObserveCutPlant(context.Background(), f.Placement, f.Placement.Snapshot); err == nil {
				t.Fatal("accepted mismatching outcome")
			}
			if f.Leases != 0 || f.Places != 0 {
				t.Fatal("observation wrote")
			}
		})
	}
}

// A standing designated plant is pending: the attempt stays open with the
// designation confirmed, and the executor observes again later.
func TestCutPlantBoundaryReportsPendingWhileThePlantStands(t *testing.T) {
	t.Parallel()
	b, f := newCutPlantBoundaryFixture(t)
	f.Progress.Effect = &r.Progress_Pending{Pending: &r.PendingEffect{Evidence: proto.Clone(f.projected).(*r.EffectEvidence)}}
	evidence, err := b.ObserveCutPlant(context.Background(), f.Placement, f.Placement.Snapshot)
	if designated, known := evidence.Designated.Value(); err != nil || evidence.Observation.Effect != domain.EffectPending || !known || !designated {
		t.Fatal(evidence, err)
	}
}
