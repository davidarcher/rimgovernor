package buildingruntime

import (
	"context"
	"fmt"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func TestShellFootprintRebuildsTheRoom(t *testing.T) {
	shell, err := domain.RectangleFootprint(domain.RoomBounds{X: 10, Z: 20, Width: 9, Height: 7}, domain.South)
	if err != nil {
		t.Fatal(err)
	}
	got, ok := shellFootprint(shell.Placements("Wall", "Door", "WoodLog"))
	if !ok || !domain.SameRoomFootprint(got, shell) {
		t.Fatalf("rebuilt %+v ok=%t, want %+v", got.Bounds(), ok, shell.Bounds())
	}
	// A ring with a gap encloses nothing.
	open := shell.Placements("Wall", "Door", "WoodLog")
	if _, ok := shellFootprint(open[1 : len(open)-1]); ok {
		t.Fatal("an open ring is not a room")
	}
}

func TestReviewColonyGridDerivesOnceAndServesThePersistedGrid(t *testing.T) {
	s, _ := schedulerFixture(t)
	ctx := context.Background()
	r := &RoutineReviewer{player: s.player}
	snapshot := s.player.session.State().Snapshot
	shell, err := domain.RectangleFootprint(domain.RoomBounds{X: 40, Z: 50, Width: 9, Height: 9}, domain.South)
	if err != nil {
		t.Fatal(err)
	}
	perimeter := shell.Placements("Wall", "Door", "WoodLog")
	var actions []domain.Action
	census := policy.CurrentConstruction{Colony: true}
	for i, b := range perimeter {
		action, err := domain.NewBuildingAction(domain.ActionID(fmt.Sprintf("cell-%d", i)), b)
		if err != nil {
			t.Fatal(err)
		}
		actions = append(actions, action)
		census.Buildings = append(census.Buildings, policy.CurrentBuilding{ID: fmt.Sprintf("b-%d", i), Building: b, Cells: []domain.Cell{b.Cell()}})
	}
	plan, err := domain.NewPlan(shellPlanPrefix+"-starter", 1, actions)
	if err != nil {
		t.Fatal(err)
	}
	if err = s.player.journal.CreatePlan(ctx, plan); err != nil {
		t.Fatal(err)
	}
	projection := observation.ColonyProjection{Identity: observation.Identity{Colony: snapshot.Colony, Map: snapshot.Map, Load: snapshot.Load, Tick: 100}, Bounds: policy.Bounds{Width: 100, Height: 100}}
	projection.ColonyGrid = domain.Unknown[policy.ColonyGrid]()
	projection.Facts.CurrentConstruction = domain.Unknown[policy.CurrentConstruction]()
	if err = r.reviewColonyGrid(ctx, snapshot, &projection); err != nil {
		t.Fatal(err)
	}
	if _, known := projection.ColonyGrid.Value(); known {
		t.Fatal("grid derived without a census")
	}
	projection.Facts.CurrentConstruction = domain.Known(census)
	if err = r.reviewColonyGrid(ctx, snapshot, &projection); err != nil {
		t.Fatal(err)
	}
	want := policy.ColonyGrid{Origin: domain.Cell{X: 40, Z: 50}, Pitch: 16, Axes: policy.ColonyGridAxes, Source: policy.ColonyGridFromStarter}
	if got, known := projection.ColonyGrid.Value(); !known || got != want {
		t.Fatalf("grid = %+v known=%t", got, known)
	}
	// A later review with different evidence serves the persisted grid.
	moved := policy.CurrentConstruction{Colony: true}
	for i, c := range []domain.Cell{{X: 5, Z: 5}, {X: 6, Z: 5}, {X: 5, Z: 6}, {X: 6, Z: 6}} {
		b, _ := domain.NewBuilding("Wall", c, domain.North, "WoodLog")
		moved.Buildings = append(moved.Buildings, policy.CurrentBuilding{ID: fmt.Sprintf("m-%d", i), Building: b, Cells: []domain.Cell{c}})
	}
	later := observation.ColonyProjection{Identity: projection.Identity, Bounds: projection.Bounds}
	later.Identity.Tick = 500
	later.Facts.CurrentConstruction = domain.Known(moved)
	if err = r.reviewColonyGrid(ctx, snapshot, &later); err != nil {
		t.Fatal(err)
	}
	if got, known := later.ColonyGrid.Value(); !known || got != want {
		t.Fatalf("grid moved: %+v known=%t", got, known)
	}
	// Without a standing shell plan the largest wall ring fixes the origin.
	other := snapshot
	other.Load = "other-load"
	fresh := observation.ColonyProjection{Identity: observation.Identity{Colony: snapshot.Colony, Map: snapshot.Map, Load: other.Load, Tick: 10}, Bounds: projection.Bounds}
	fresh.Facts.CurrentConstruction = domain.Known(moved)
	if err = r.reviewColonyGrid(ctx, other, &fresh); err != nil {
		t.Fatal(err)
	}
	if got, known := fresh.ColonyGrid.Value(); !known || got.Origin != (domain.Cell{X: 5, Z: 5}) || got.Source != policy.ColonyGridFromRoom {
		t.Fatalf("largest room grid = %+v known=%t", got, known)
	}
}
