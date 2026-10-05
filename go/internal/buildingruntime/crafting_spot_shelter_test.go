package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// craftingSpotSearch runs the real placement search for the crafting spot on
// the sleeping fixture's 5x5 site with a 4x4 room on it, and returns the
// chosen cell and the shelter template's craft slot for that room.
func craftingSpotSearch(t *testing.T, shelterPlanned bool) (chosen, slot domain.Cell) {
	t.Helper()
	ctx := context.Background()
	planner, _, session, _, n := sleepingFixture(t)
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
	reading, err := planner.reviewer.observeColony(ctx, planner.native, expected, []string{"SleepingSpot"})
	if err != nil {
		t.Fatal(err)
	}
	planner.concern, planner.definition, planner.environment = policy.EnsureBasicDefense, craftingSpotDefinition, policy.PlacementAnywhere
	interior := policy.Rectangle{X: 0, Z: 0, Width: 4, Height: 4}
	door := domain.Cell{X: 2, Z: 4}
	facts := reading.Projection
	if shelterPlanned {
		facts.LayoutPlan = domain.Known(policy.LayoutPlan{Rooms: []policy.LayoutRoom{{Role: policy.ModuleShelter, Interior: interior, Door: door, DoorRot: domain.North}}})
	}
	room := policy.Room{ID: "1", Role: domain.Known(policy.RoomRoleBarracks), Enclosed: domain.Known(true), Cells: rectangleCells(interior)}
	facts.Rooms = domain.Known(policy.RoomObservation{Shapes: testPieceShapes, Rooms: []policy.Room{room}})
	facts.Cells = append([]policy.SiteCell(nil), facts.Cells...)
	for i := range facts.Cells {
		if facts.Cells[i].Cell == door {
			facts.Cells[i].Doorway = domain.Known(true)
		}
	}
	interiorRoom, ok := policy.InteriorRoomFromCensus(room, policy.RoomRoleShelter, []domain.Cell{door}, testPieceShapes)
	if !ok {
		t.Fatal("the room has no interior")
	}
	piece, _ := interiorRoom.Piece(craftingSpotDefinition)
	piece.Def = craftingSpotDefinition
	plan, ok := policy.PlanInterior(interiorRoom, piece)
	if !ok {
		t.Fatal("the shelter template plans no room")
	}
	found := false
	for _, p := range plan.Pieces {
		if p.Slot == "craft" {
			slot, found = p.Anchor(), true
		}
	}
	if !found {
		t.Fatalf("no craft slot in %v", plan.Pieces)
	}
	snapshot := session.State().Snapshot
	snapshot.Plan, snapshot.Revision = "crafting-spot", 1
	selected, _, reason, err := planner.previewSearch(ctx, snapshot, facts, nil, 1, func() error { return nil })
	if err != nil || !reason.IsZero() || len(selected) != 1 {
		t.Fatalf("selected %v %q %v", selected, reason, err)
	}
	b, _ := selected[0].Action.Building()
	return b.Cell(), slot
}

// With a shelter standing the crafting spot lands on the template's crafting
// slot (#2074); without one the placement is the scored search's.
func TestCraftingSpotTakesTheShelterTemplateSlot(t *testing.T) {
	t.Parallel()
	got, slot := craftingSpotSearch(t, true)
	if got != slot {
		t.Fatalf("crafting spot at %v, want the shelter craft slot %v", got, slot)
	}
	old, _ := craftingSpotSearch(t, false)
	if old == slot {
		t.Fatalf("without a shelter the spot still lands on the slot %v; the test does not tell the paths apart", slot)
	}
}
