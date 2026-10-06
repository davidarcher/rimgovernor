package buildingruntime

import (
	"context"
	"slices"
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
func campfireSearch(t *testing.T, cold bool) (chosen domain.Cell, slots []domain.Cell, interior policy.Rectangle, wait Verdict) {
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
	// No wall stands: the planned interior alone holds the slots (#2264).
	facts.Shapes = testPieceShapes
	facts.Rooms = domain.Known(policy.RoomObservation{Shapes: testPieceShapes})
	for _, shelter := range plannedShelterRooms(facts) {
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
	if err != nil {
		t.Fatal(err)
	}
	if len(selected) == 0 {
		return domain.Cell{}, slots, interior, reason
	}
	if !reason.IsZero() || len(selected) != 1 {
		t.Fatalf("selected %v %q", selected, reason)
	}
	b, _ := selected[0].Action.Building()
	return b.Cell(), slots, interior, reason
}

// A cold map's cooking campfire stands indoors on a shelter template slot,
// and the template holds two (#2044).
func TestCookingCampfireTakesTheShelterSlotOnAColdMap(t *testing.T) {
	t.Parallel()
	got, slots, _, _ := campfireSearch(t, true)
	if len(slots) != 2 {
		t.Fatalf("a cold shelter template holds %d campfire slots, want 2", len(slots))
	}
	if got != slots[0] && got != slots[1] {
		t.Fatalf("campfire at %v, want a shelter slot %v", got, slots)
	}
}

// On a normal map the template holds no campfire slot and no kitchen stands:
// the cooking campfire has no outdoor stand-in and waits for the room (#2266).
func TestCookingCampfireWaitsForTheKitchenOnANormalMap(t *testing.T) {
	t.Parallel()
	got, slots, _, wait := campfireSearch(t, false)
	if len(slots) != 0 {
		t.Fatalf("a normal shelter template holds campfire slots %v", slots)
	}
	if wait != BuildingNoLayoutPlan || got != (domain.Cell{}) {
		t.Fatalf("campfire at %v wait %q, want a wait for the kitchen", got, wait)
	}
}

// Loose sleeping spots keep off the shelter's campfire slots: a spot on one
// left a cold map's cooking campfire to fall through to the kitchen (#2138).
func TestShelterFurnitureCellsCoverTheCampfireSlotsNotTheBunks(t *testing.T) {
	t.Parallel()
	facts := observation.ColonyProjection{}
	facts.LayoutPlan = domain.Known(policy.LayoutPlan{Cold: true, Rooms: []policy.PlannedRoom{{Role: policy.PlannedShelter,
		Interior: policy.Rectangle{X: 62, Z: 79, Width: 6, Height: 4}, Door: domain.Cell{X: 65, Z: 78}, DoorRot: domain.South}}})
	facts.Shapes = testPieceShapes
	facts.Rooms = domain.Known(policy.RoomObservation{Shapes: testPieceShapes})
	cells := shelterFurnitureCells(facts)
	for _, want := range []domain.Cell{{X: 62, Z: 82}, {X: 67, Z: 79}} {
		if !slices.Contains(cells, want) {
			t.Fatalf("campfire slot %v is not protected from loose spots: %v", want, cells)
		}
	}
	if slices.Contains(cells, domain.Cell{X: 63, Z: 82}) {
		t.Fatalf("a bunk slot is protected: %v", cells)
	}
}
