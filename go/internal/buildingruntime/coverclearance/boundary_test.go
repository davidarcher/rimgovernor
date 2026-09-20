package coverclearance

import (
	"context"
	"errors"
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
	"testing"
)

type coverClearanceBoundaryFixture struct {
	*boundary.Fixture
	target    bridge.CoverClearanceTarget
	read      bridge.DefenseSite
	projected *r.EffectEvidence
	refused   bool
}

func (f *coverClearanceBoundaryFixture) ReadDefenseSite(context.Context, *c.Identity, bridge.CellRect) (bridge.DefenseSite, bridge.Result, error) {
	return f.read, bridge.Result{}, nil
}
func (f *coverClearanceBoundaryFixture) PreviewCoverClearance(context.Context, *c.Identity, bridge.CoverClearanceTarget) (*op.PreviewReply, bridge.Result, error) {
	return &op.PreviewReply{Outcome: &op.PreviewReply_Evaluated{Evaluated: &op.PreviewEvaluation{Context: proto.Clone(f.Receipt.AdmittedContext).(*c.ObservationContext), Accepted: proto.Bool(!f.refused), Projected: f.projected}}}, bridge.Result{}, nil
}
func (f *coverClearanceBoundaryFixture) DesignateCoverClearance(_ context.Context, pre *a.WritePrecondition, target bridge.CoverClearanceTarget) (*op.ExecuteReply, bridge.Result, error) {
	f.Places++
	f.LastPre = pre
	return &op.ExecuteReply{Outcome: &op.ExecuteReply_Receipt{Receipt: f.Receipt}}, bridge.Result{}, nil
}
func (f *coverClearanceBoundaryFixture) LookupCoverClearance(context.Context, bridge.CoverClearanceAttempt) (*r.LookupReply, bridge.Result, error) {
	f.Lookups++
	return &r.LookupReply{Outcome: &r.LookupReply_Receipt{Receipt: f.Receipt}}, bridge.Result{}, nil
}
func (f *coverClearanceBoundaryFixture) ObserveCoverClearance(context.Context, bridge.CoverClearanceAttempt, *r.Receipt) (*r.ProgressReply, bridge.Result, error) {
	f.Observes++
	return &r.ProgressReply{Outcome: &r.ProgressReply_Progress{Progress: f.Progress}}, bridge.Result{}, nil
}
func newCoverClearanceBoundaryFixture(t *testing.T) (*CoverClearanceBoundary, *coverClearanceBoundaryFixture) {
	b, base := boundary.NewFixture(t)
	plant, _ := domain.NewCoverClearance("Plant_TreeOak1", "Plant_TreeOak", domain.CoverClearanceCutPlant, domain.Cell{X: 1, Z: 2})
	action, _ := domain.NewCoverClearanceAction("action", plant)
	base.Placement.Action = action
	effect := &r.EffectEvidence{Effect: &r.EffectEvidence_Designation{Designation: &r.DesignationEffect{ThingId: proto.String("Plant_TreeOak1"), ResourceDef: proto.String("Plant_TreeOak"), DesignationDef: proto.String("CutPlant"), Present: proto.Bool(true), Cell: &c.Cell{X: proto.Int32(1), Z: proto.Int32(2)}}}}
	base.Receipt.Outcome = &r.Receipt_Applied{Applied: &r.Applied{Observed: proto.Clone(effect).(*r.EffectEvidence)}}
	base.Progress.Effect = &r.Progress_Completed{Completed: &r.CompletedEffect{Evidence: proto.Clone(effect).(*r.EffectEvidence)}}
	base.Emergency.Context.Tick = proto.Int64(10)
	f := &coverClearanceBoundaryFixture{Fixture: base, target: bridge.CoverClearanceTarget{Clearance: plant, Token: "token"}, projected: effect}
	f.read = bridge.DefenseSite{Context: proto.Clone(base.Receipt.AdmittedContext).(*c.ObservationContext), Cells: coverCells(f.target)}
	return &CoverClearanceBoundary{Boundary: b, coverClearance: CoverClearanceCapabilities{Native: f, Writer: f}}, f
}
func TestCoverClearanceBoundaryLeaseFreeReadbackAndExactPrecondition(t *testing.T) {
	t.Parallel()
	b, f := newCoverClearanceBoundaryFixture(t)
	ctx := context.Background()
	p := f.Placement
	inspection, err := b.InspectCoverClearance(ctx, executor.Target{Action: p.Action, Snapshot: p.Snapshot})
	if err != nil || !inspection.Accepted || f.Leases != 0 {
		t.Fatal(inspection, err)
	}
	receipt, err := b.DesignateCoverClearance(ctx, executor.CoverClearanceDispatch{Attempt: p, SnapshotToken: inspection.SnapshotToken})
	if err != nil || receipt.Kind != domain.ReceiptAccepted || f.Leases != 1 || f.Places != 1 || !proto.Equal(f.LastPre.Attempt, f.Receipt.Attempt) {
		t.Fatal(receipt, err)
	}
	evidence, err := b.ObserveCoverClearance(ctx, p, p.Snapshot)
	if err != nil || evidence.Observation.Effect != domain.EffectCompleted || f.Leases != 1 || f.Places != 1 {
		t.Fatal(evidence, err)
	}
}

// A fresh census without the exact undesignated thing is the absent
// sentinel, so the executor can settle the proposal instead of holding it; a
// preview refusal on a present thing still only holds.
func TestCoverClearanceBoundaryReportsAbsentTarget(t *testing.T) {
	t.Parallel()
	b, f := newCoverClearanceBoundaryFixture(t)
	p := f.Placement
	f.read.Cells = nil
	if _, err := b.InspectCoverClearance(context.Background(), executor.Target{Action: p.Action, Snapshot: p.Snapshot}); !errors.Is(err, executor.ErrCoverClearanceAbsent) {
		t.Fatal(err)
	}
	f.read.Cells, f.refused = coverCells(f.target), true
	if _, err := b.InspectCoverClearance(context.Background(), executor.Target{Action: p.Action, Snapshot: p.Snapshot}); !errors.Is(err, executor.ErrHeld) || errors.Is(err, executor.ErrCoverClearanceAbsent) {
		t.Fatal(err)
	}
}

// The clock may run between the census, the preview and the emergency
// read: the inspection accepts ticks in that order (the token binds the
// plant), tolerates a cached emergency read within its family's tick
// tolerance (#243), and holds when the preview predates the census or the
// emergency read is older than that.
func TestCoverClearanceBoundaryAcceptsAdvancingTicksAcrossItsReads(t *testing.T) {
	t.Parallel()
	b, f := newCoverClearanceBoundaryFixture(t)
	p := f.Placement
	target := executor.Target{Action: p.Action, Snapshot: p.Snapshot}
	previewTick := f.Receipt.AdmittedContext.GetTick() + 2*bridge.FactEmergency.TickTolerance()
	f.Receipt.AdmittedContext.Tick = proto.Int64(previewTick)
	f.read.Context.Tick = proto.Int64(previewTick - 5)
	f.Emergency.Context.Tick = proto.Int64(previewTick + 7)
	inspection, err := b.InspectCoverClearance(context.Background(), target)
	if err != nil || !inspection.Accepted || inspection.Tick != domain.Tick(previewTick) || inspection.SnapshotToken != "token" {
		t.Fatal(inspection, err)
	}
	f.read.Context.Tick = proto.Int64(previewTick + 1)
	if _, err := b.InspectCoverClearance(context.Background(), target); !errors.Is(err, executor.ErrHeld) {
		t.Fatal(err)
	}
	f.read.Context.Tick = proto.Int64(previewTick)
	f.Emergency.Context.Tick = proto.Int64(previewTick - bridge.FactEmergency.TickTolerance())
	if inspection, err := b.InspectCoverClearance(context.Background(), target); err != nil || !inspection.Accepted {
		t.Fatal(inspection, err)
	}
	f.Emergency.Context.Tick = proto.Int64(previewTick - bridge.FactEmergency.TickTolerance() - 1)
	if _, err := b.InspectCoverClearance(context.Background(), target); !errors.Is(err, executor.ErrHeld) {
		t.Fatal(err)
	}
}
func TestCoverClearanceBoundaryRejectsForeignAndIncompleteEvidence(t *testing.T) {
	t.Parallel()
	for _, kind := range []string{"item", "cell", "incomplete", "reason", "world"} {
		t.Run(kind, func(t *testing.T) {
			b, f := newCoverClearanceBoundaryFixture(t)
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
			if _, err := b.ObserveCoverClearance(context.Background(), f.Placement, f.Placement.Snapshot); err == nil {
				t.Fatal("accepted mismatching outcome")
			}
			if f.Leases != 0 || f.Places != 0 {
				t.Fatal("observation wrote")
			}
		})
	}
}

// A standing designated thing is pending: the attempt stays open with the
// designation confirmed, and the executor observes again later.
func TestCoverClearanceBoundaryReportsPendingWhileTheThingStands(t *testing.T) {
	t.Parallel()
	b, f := newCoverClearanceBoundaryFixture(t)
	f.Progress.Effect = &r.Progress_Pending{Pending: &r.PendingEffect{Evidence: proto.Clone(f.projected).(*r.EffectEvidence)}}
	evidence, err := b.ObserveCoverClearance(context.Background(), f.Placement, f.Placement.Snapshot)
	if designated, known := evidence.Designated.Value(); err != nil || evidence.Observation.Effect != domain.EffectPending || !known || !designated {
		t.Fatal(evidence, err)
	}
}

func coverCells(target bridge.CoverClearanceTarget) []bridge.DefenseCell {
	cl := target.Clearance
	return []bridge.DefenseCell{{Cell: cl.Cell(), Walkable: true, Passable: true, CoverFill: 0.25, Cover: &bridge.DefenseCover{ThingID: cl.Thing(), DefName: cl.Definition(), Kind: o.CoverKind_COVER_KIND_PLANT, Token: target.Token}}}
}

// A designated or re-identified cover thing is absent for the executor's
// purposes: the exact undesignated target is gone.
func TestCoverClearanceBoundaryTreatsDesignatedCoverAsAbsent(t *testing.T) {
	t.Parallel()
	b, f := newCoverClearanceBoundaryFixture(t)
	p := f.Placement
	f.read.Cells[0].Cover.Designated = true
	if _, err := b.InspectCoverClearance(context.Background(), executor.Target{Action: p.Action, Snapshot: p.Snapshot}); !errors.Is(err, executor.ErrCoverClearanceAbsent) {
		t.Fatal(err)
	}
	f.read.Cells[0].Cover.Designated, f.read.Cells[0].Cover.Kind = false, o.CoverKind_COVER_KIND_CHUNK
	if _, err := b.InspectCoverClearance(context.Background(), executor.Target{Action: p.Action, Snapshot: p.Snapshot}); !errors.Is(err, executor.ErrCoverClearanceAbsent) {
		t.Fatal(err)
	}
}
