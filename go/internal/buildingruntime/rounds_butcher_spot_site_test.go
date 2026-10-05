package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// The stand-in butcher spot (#2040) goes in a standing planned butchery, else
// at the nearest open cell outside every room within the planned core's box
// plus a margin, and stays unplaced when the box is full.
func TestButcherSpotSite(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	planner, _, session, _, n := sleepingFixture(t)
	n.putCatalog(buildable("ButcherSpot", 0, 1, 1))
	n.onPreview = func(_ context.Context, p *bridge.BuildingPreview) {
		b, _ := p.Preview.Action.Building()
		p.Preview.Footprint = domain.Known([]domain.Cell{b.Cell()})
	}
	identity, _, err := n.Identity(ctx)
	if err != nil {
		t.Fatal(err)
	}
	expected, err := observation.DecodeIdentity(identity)
	if err != nil {
		t.Fatal(err)
	}
	reading, err := planner.reviewer.observeColony(ctx, planner.native, expected, []string{"ButcherSpot"})
	if err != nil {
		t.Fatal(err)
	}
	planner.concern, planner.definition, planner.stuff = policy.MaintainButcherSpot, "ButcherSpot", ""
	snapshot := session.State().Snapshot
	snapshot.Plan, snapshot.Revision = "butcher-spot-site", 1
	check := func() error { return nil }
	enclosed := func(id string, in policy.Rectangle) policy.Room {
		return policy.Room{ID: id, Role: domain.Known(policy.RoomRoleRoom), Enclosed: domain.Known(true), Cells: rectangleCells(in)}
	}
	place := func(plan policy.LayoutPlan, rooms []policy.Room, spot *RoundsBuildingPlanner) (domain.Cell, bool) {
		t.Helper()
		facts := reading.Projection
		facts.Bounds = policy.Bounds{Width: 100, Height: 100}
		facts.LayoutPlan = domain.Known(plan)
		facts.Rooms = domain.Known(policy.RoomObservation{Shapes: testPieceShapes, Rooms: rooms})
		selected, _, _, err := spot.previewSearch(ctx, snapshot, facts, nil, 1, check)
		if err != nil {
			t.Fatal(err)
		}
		if len(selected) == 0 {
			return domain.Cell{}, false
		}
		b, _ := selected[0].Action.Building()
		return b.Cell(), true
	}
	inside := func(c domain.Cell, r policy.Rectangle) bool {
		return c.X >= r.X && c.X < r.X+r.Width && c.Z >= r.Z && c.Z < r.Z+r.Height
	}

	storage := policy.Rectangle{X: 1, Z: 1, Width: 3, Height: 3}
	plan := policy.LayoutPlan{Rooms: []policy.PlannedRoom{{Role: policy.PlannedStorage, Interior: storage}}}
	bedroom := policy.Rectangle{X: 0, Z: 4, Width: 5, Height: 1}
	cell, ok := place(plan, []policy.Room{enclosed("bed", bedroom)}, planner)
	if !ok || inside(cell, storage) || inside(cell, bedroom) || cell.X < 0 || cell.X > 4 || cell.Z < 0 || cell.Z > 3 {
		t.Fatalf("spot at %v ok=%v, want a ring cell outside the storage and the bedroom", cell, ok)
	}

	// A standing planned butchery takes the spot inside it.
	butchery := policy.Rectangle{X: 1, Z: 1, Width: 3, Height: 3}
	plan = policy.LayoutPlan{Rooms: []policy.PlannedRoom{{Role: policy.PlannedButchery, Interior: butchery}}}
	rooms := []policy.Room{enclosed("butchery", butchery)}
	facts := reading.Projection
	facts.LayoutPlan = domain.Known(plan)
	facts.Rooms = domain.Known(policy.RoomObservation{Shapes: testPieceShapes, Rooms: rooms})
	room := *planner
	room.cells = plannedRoomCells(facts, policy.PlannedButchery)
	if room.cells == nil {
		t.Fatal("the planned butchery does not stand")
	}
	if cell, ok := place(plan, rooms, &room); !ok || !inside(cell, butchery) {
		t.Fatalf("spot at %v ok=%v, want inside the butchery", cell, ok)
	}

	// Every open cell lies past the core box plus the margin: unplaced.
	far := policy.LayoutPlan{Rooms: []policy.PlannedRoom{{Role: policy.PlannedStorage, Interior: policy.Rectangle{X: 50, Z: 50, Width: 1, Height: 1}}}}
	if cell, ok := place(far, nil, planner); ok {
		t.Fatalf("spot at %v, want none outside the box", cell)
	}
}
