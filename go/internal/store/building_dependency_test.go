package store

import (
	"context"
	"errors"
	"fmt"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// reviewCensus files a disabled routine review whose construction census
// holds the given intents built and blueprints standing.
func reviewCensus(t *testing.T, s *Store, current domain.GenerationSnapshot, tick domain.Tick, built []domain.ActionID, blueprints ...domain.ActionID) {
	t.Helper()
	ctx := context.Background()
	previous, err := s.LoadRoutineReview(ctx)
	if err != nil {
		t.Fatal(err)
	}
	census := policy.CurrentConstruction{Colony: true, Buildings: []policy.CurrentBuilding{}}
	for i, a := range built {
		census.Buildings = append(census.Buildings, policy.CurrentBuilding{ID: fmt.Sprintf("Thing%d", i), IntentKey: string(a) + "/1"})
	}
	for _, a := range blueprints {
		census.Intents = append(census.Intents, policy.ConstructionIntent{Key: string(a) + "/1", Stage: "blueprint"})
	}
	r := RoutineReviewRequest{Revision: previous.Revision, Current: current, Tick: tick, Policy: policy.DefaultRoutinePolicy(), Facts: policy.RoutineFacts{CurrentConstruction: domain.Known(census)}}
	if _, err = s.ReviewRoutine(ctx, r); err != nil {
		t.Fatal(err)
	}
}

// An accepted building intent completes at its blueprint; its dependent
// waits until the census reports the building built (#937).
func TestBuildingDependencyWaitsForCensusBuilt(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, memoryPath(t))
	base := plan(t, "p", "floor", "bed")
	p, err := domain.NewPlan(base.ID(), base.Revision(), base.Actions(), domain.ActionDependency{Action: "bed", Requires: "floor"})
	if err != nil {
		t.Fatal(err)
	}
	if err = s.CreatePlan(ctx, p); err != nil {
		t.Fatal(err)
	}
	prepare(t, s, "floor")
	if _, err = s.Dispatch(ctx, "p", "floor", scope(), 10); err != nil {
		t.Fatal(err)
	}
	if _, err = s.RecordReceipt(ctx, "p", "floor", 1, domain.ReceiptAccepted); err != nil {
		t.Fatal(err)
	}
	if _, err = s.Prepare(ctx, "p", "bed", scope(), 11); !errors.Is(err, domain.ErrDependency) {
		t.Fatal("dependent admitted before any census", err)
	}
	reviewCensus(t, s, scope(), 11, nil, "floor")
	if _, err = s.Prepare(ctx, "p", "bed", scope(), 11); !errors.Is(err, domain.ErrDependency) {
		t.Fatal("dependent admitted onto a blueprint", err)
	}
	other := scope()
	other.Load = "other"
	reviewCensus(t, s, other, 12, []domain.ActionID{"floor"})
	if _, err = s.Prepare(ctx, "p", "bed", scope(), 12); !errors.Is(err, domain.ErrDependency) {
		t.Fatal("another world's census released the dependent", err)
	}
	reviewCensus(t, s, scope(), 13, []domain.ActionID{"floor"})
	if _, err = s.Prepare(ctx, "p", "bed", scope(), 13); err != nil {
		t.Fatal(err)
	}
	// An incomplete census keeps the last built set.
	previous, err := s.LoadRoutineReview(ctx)
	if err != nil {
		t.Fatal(err)
	}
	if _, err = s.ReviewRoutine(ctx, RoutineReviewRequest{Revision: previous.Revision, Current: scope(), Tick: 14, Policy: policy.DefaultRoutinePolicy()}); err != nil {
		t.Fatal(err)
	}
	if _, err = s.Dispatch(ctx, "p", "bed", scope(), 14); err != nil {
		t.Fatal(err)
	}
}

// reviewWalls files a disabled routine review whose complete construction
// census holds a colony Wall on each given cell.
func reviewWalls(t *testing.T, s *Store, tick domain.Tick, walls ...domain.Cell) {
	t.Helper()
	ctx := context.Background()
	previous, err := s.LoadRoutineReview(ctx)
	if err != nil {
		t.Fatal(err)
	}
	census := policy.CurrentConstruction{Colony: true, Buildings: []policy.CurrentBuilding{}}
	for i, c := range walls {
		b, err := domain.NewBuilding("Wall", c, domain.North, "BlocksGranite")
		if err != nil {
			t.Fatal(err)
		}
		census.Buildings = append(census.Buildings, policy.CurrentBuilding{ID: fmt.Sprintf("Wall%d", i), Building: b, Cells: []domain.Cell{c}})
	}
	r := RoutineReviewRequest{Revision: previous.Revision, Current: scope(), Tick: tick, Policy: policy.DefaultRoutinePolicy(), Facts: policy.RoutineFacts{CurrentConstruction: domain.Known(census)}}
	if _, err = s.ReviewRoutine(ctx, r); err != nil {
		t.Fatal(err)
	}
}

// An applied wall removal is only designated; the replacement wall on the
// same cell waits until the census shows no wall left there (#989).
func TestWallReplacementWaitsForCensusRemoval(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, memoryPath(t))
	cell := domain.Cell{X: 3, Z: 4}
	removal, err := domain.NewWallRemoval("Wall_original", "", cell)
	if err != nil {
		t.Fatal(err)
	}
	demolish, err := domain.NewWallRemovalAction("demolish", removal)
	if err != nil {
		t.Fatal(err)
	}
	wall, err := domain.NewBuilding("Wall", cell, domain.North, "BlocksGranite")
	if err != nil {
		t.Fatal(err)
	}
	replace, err := domain.NewBuildingAction("replace", wall)
	if err != nil {
		t.Fatal(err)
	}
	p, err := domain.NewPlan("p", domain.PlanRevision(^uint64(0)), []domain.Action{demolish, replace}, domain.ActionDependency{Action: "replace", Requires: "demolish"})
	if err != nil {
		t.Fatal(err)
	}
	if err = s.CreatePlan(ctx, p); err != nil {
		t.Fatal(err)
	}
	prepare(t, s, "demolish")
	if _, err = s.Dispatch(ctx, "p", "demolish", scope(), 10); err != nil {
		t.Fatal(err)
	}
	if _, err = s.RecordReceipt(ctx, "p", "demolish", 1, domain.ReceiptAccepted); err != nil {
		t.Fatal(err)
	}
	if _, err = s.Prepare(ctx, "p", "replace", scope(), 11); !errors.Is(err, domain.ErrDependency) {
		t.Fatal("replacement admitted before any census", err)
	}
	reviewWalls(t, s, 11, cell, domain.Cell{X: 9, Z: 9})
	if _, err = s.Prepare(ctx, "p", "replace", scope(), 11); !errors.Is(err, domain.ErrDependency) {
		t.Fatal("replacement admitted onto the standing wall", err)
	}
	reviewWalls(t, s, 12, domain.Cell{X: 9, Z: 9})
	review, err := s.LoadRoutineReview(ctx)
	if err != nil {
		t.Fatal(err)
	}
	if len(review.WallCells) != 1 || review.WallCells[0] != (WallCell{Cell: cell}) {
		t.Fatalf("census records only the named cell: %#v", review.WallCells)
	}
	if _, err = s.Prepare(ctx, "p", "replace", scope(), 12); err != nil {
		t.Fatal(err)
	}
}

// A removal whose wall still stands a stall bound after dispatch is given
// up with its replacement, so the goal can re-plan (#1001).
func TestStuckWallRemovalWithdrawsReplacement(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, memoryPath(t))
	cell := domain.Cell{X: 3, Z: 4}
	removal, err := domain.NewWallRemoval("Wall_original", "", cell)
	if err != nil {
		t.Fatal(err)
	}
	demolish, err := domain.NewWallRemovalAction("demolish", removal)
	if err != nil {
		t.Fatal(err)
	}
	wall, err := domain.NewBuilding("Wall", cell, domain.North, "BlocksGranite")
	if err != nil {
		t.Fatal(err)
	}
	replace, err := domain.NewBuildingAction("replace", wall)
	if err != nil {
		t.Fatal(err)
	}
	p, err := domain.NewPlan("p", domain.PlanRevision(^uint64(0)), []domain.Action{demolish, replace}, domain.ActionDependency{Action: "replace", Requires: "demolish"})
	if err != nil {
		t.Fatal(err)
	}
	if err = s.CreatePlan(ctx, p); err != nil {
		t.Fatal(err)
	}
	prepare(t, s, "demolish")
	if _, err = s.Dispatch(ctx, "p", "demolish", scope(), 10); err != nil {
		t.Fatal(err)
	}
	if _, err = s.RecordReceipt(ctx, "p", "demolish", 1, domain.ReceiptAccepted); err != nil {
		t.Fatal(err)
	}
	stages := func() map[domain.ActionID]domain.Stage {
		state, err := s.LoadPlan(ctx, "p")
		if err != nil {
			t.Fatal(err)
		}
		out := map[domain.ActionID]domain.Stage{}
		for _, p := range state.Progress {
			out[p.View().Action] = p.View().Stage
		}
		return out
	}
	reviewWalls(t, s, 10+policy.DevelopmentStallTicks, cell)
	if got := stages(); got["demolish"] != domain.Completed || got["replace"] != domain.Pending {
		t.Fatalf("gave up inside the stall bound: %v", got)
	}
	reviewWalls(t, s, 11+policy.DevelopmentStallTicks, cell)
	if got := stages(); got["demolish"] != domain.Cancelled || got["replace"] != domain.Cancelled {
		t.Fatalf("stuck removal not withdrawn: %v", got)
	}
}
