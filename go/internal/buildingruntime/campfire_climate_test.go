package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// campfireSearch runs the real placement search for the cooking campfire on
// the sleeping fixture's 5x5 site with a 4x4 shelter standing on the layout
// plan, and returns the chosen cell and the shelter template's campfire slot
// anchors for the plan's climate.
func campfireSearch(t *testing.T, cold bool) (chosen domain.Cell, slots []domain.Cell, interior policy.Rectangle) {
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
	reading, err := planner.reviewer.observeColony(ctx, planner.native, expected, []string{"SleepingSpot", "Campfire"})
	if err != nil {
		t.Fatal(err)
	}
	planner.concern, planner.definition, planner.environment = policy.EnsureCooking, "Campfire", policy.PlacementAnywhere
	interior = policy.Rectangle{X: 0, Z: 0, Width: 4, Height: 4}
	door := domain.Cell{X: 2, Z: 4}
	facts := reading.Projection
	facts.LayoutPlan = domain.Known(policy.LayoutPlan{Cold: cold, Rooms: []policy.PlannedRoom{{Role: policy.PlannedShelter, Interior: interior, Door: door, DoorRot: domain.North}}})
	room := policy.Room{ID: "1", Role: domain.Known(policy.RoomRoleBarracks), Enclosed: domain.Known(true), Cells: rectangleCells(interior)}
	facts.Rooms = domain.Known(policy.RoomObservation{Shapes: testPieceShapes, Rooms: []policy.Room{room}})
	facts.Cells = append([]policy.SiteCell(nil), facts.Cells...)
	for i := range facts.Cells {
		if facts.Cells[i].Cell == door {
			facts.Cells[i].Doorway = domain.Known(true)
		}
	}
	for _, shelter := range standingShelterRooms(facts) {
		piece, _ := shelter.Piece("Campfire")
		piece.Def = "Campfire"
		plan, ok := policy.PlanInterior(shelter, piece)
		if !ok {
			t.Fatal("the shelter template plans no room")
		}
		for _, p := range plan.Pieces {
			if p.Accepts(shelter.Shapes, "Campfire") {
				slots = append(slots, p.Anchor())
			}
		}
	}
	snapshot := session.State().Snapshot
	snapshot.Plan, snapshot.Revision = "cooking-campfire", 1
	selected, _, reason, err := planner.previewSearch(ctx, snapshot, facts, nil, 1, func() error { return nil })
	if err != nil || !reason.IsZero() || len(selected) != 1 {
		t.Fatalf("selected %v %q %v", selected, reason, err)
	}
	b, _ := selected[0].Action.Building()
	return b.Cell(), slots, interior
}

// A cold map's cooking campfire stands indoors on a shelter template slot,
// and the template holds two (#2044).
func TestCookingCampfireTakesTheShelterSlotOnAColdMap(t *testing.T) {
	t.Parallel()
	got, slots, _ := campfireSearch(t, true)
	if len(slots) != 2 {
		t.Fatalf("a cold shelter template holds %d campfire slots, want 2", len(slots))
	}
	if got != slots[0] && got != slots[1] {
		t.Fatalf("campfire at %v, want a shelter slot %v", got, slots)
	}
}

// On a normal map the template holds no campfire slot and the cooking
// campfire stands outside every room, within the core box (#2044).
func TestCookingCampfireStandsOutsideOnANormalMap(t *testing.T) {
	t.Parallel()
	got, slots, interior := campfireSearch(t, false)
	if len(slots) != 0 {
		t.Fatalf("a normal shelter template holds campfire slots %v", slots)
	}
	if got.X >= interior.X && got.X < interior.X+interior.Width && got.Z >= interior.Z && got.Z < interior.Z+interior.Height {
		t.Fatalf("campfire at %v stands inside the shelter %v", got, interior)
	}
}
