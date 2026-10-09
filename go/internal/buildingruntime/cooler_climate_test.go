package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// coolerSearch runs the temperature planner's real passive-cooler selection
// for a 36 C shelter, then the real placement search, on the sleeping
// fixture's 5x5 site with a 4x4 shelter standing on the layout plan. It
// returns the chosen cell and the shelter template's cooler slot anchors.
func coolerSearch(t *testing.T, hot bool) (chosen domain.Cell, slots []domain.Cell) {
	t.Helper()
	ctx := context.Background()
	planner, _, session, _, n := sleepingFixture(t)
	n.putCatalog(buildable("PassiveCooler", 0, 1, 1))
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
	reading, err := planner.reviewer.observeColony(ctx, planner.native, expected, []string{"SleepingSpot", "PassiveCooler"})
	if err != nil {
		t.Fatal(err)
	}
	interior := policy.Rectangle{X: 0, Z: 0, Width: 4, Height: 4}
	door := domain.Cell{X: 2, Z: 4}
	facts := reading.Projection
	facts.LayoutPlan = domain.Known(policy.LayoutPlan{Hot: hot, Rooms: []policy.PlannedRoom{{Role: policy.PlannedShelter, Interior: interior, Door: door, DoorRot: domain.North}}})
	room := policy.Room{ID: "1", Role: domain.Known(policy.RoomRoleBarracks), Enclosed: domain.Known(true), Cells: rectangleCells(interior), Beds: []string{"bed1"}, Temperature: domain.Known(36.0), Contents: domain.Known([]policy.Amount{})}
	observed := policy.RoomObservation{Shapes: testPieceShapes, EligibleBeds: domain.Known(room.Beds), Rooms: []policy.Room{room}}
	facts.Shapes = testPieceShapes
	facts.Rooms = domain.Known(observed)
	facts.Cells = append([]policy.SiteCell(nil), facts.Cells...)
	for i := range facts.Cells {
		if facts.Cells[i].Cell == door {
			facts.Cells[i].Doorway = domain.Known(true)
		}
	}
	proposal, err := policy.SelectTemperatureMethod(facts.Rooms, temperatureCooling(facts), policy.DefaultRoundsPolicy(), policy.RoundsLatches{Hot: true})
	if err != nil || proposal.Method != policy.TemperatureCool {
		t.Fatalf("temperature proposal %+v %v, want the passive cooler", proposal, err)
	}
	planner.temperature, planner.concern, planner.definition, planner.environment = &proposal, policy.EnsureTemperatureSafety, string(proposal.Method), policy.PlacementIndoors
	for _, shelter := range plannedShelterRooms(facts) {
		piece, _ := shelter.Piece("PassiveCooler")
		piece.Def = "PassiveCooler"
		plan, ok := policy.PlanInterior(shelter, piece)
		if !ok {
			t.Fatal("the shelter template plans no room")
		}
		for _, p := range plan.Pieces {
			if p.Accepts(shelter.Shapes, "PassiveCooler") {
				slots = append(slots, p.Anchor())
			}
		}
	}
	snapshot := session.State().Snapshot
	snapshot.Plan, snapshot.Revision = "shelter-cooler", 1
	selected, _, reason, err := planner.previewSearch(ctx, snapshot, facts, nil, 1, func() error { return nil })
	if err != nil || !reason.IsZero() || len(selected) != 1 {
		t.Fatalf("selected %v %q %v", selected, reason, err)
	}
	b, _ := selected[0].Action.Building()
	return b.Cell(), slots
}

// On a hot map the shelter template holds a passive cooler floor slot and the
// temperature planner's cooler stands on it.
func TestPassiveCoolerTakesTheShelterSlotOnAHotMap(t *testing.T) {
	t.Parallel()
	got, slots := coolerSearch(t, true)
	if len(slots) != 1 {
		t.Fatalf("a hot shelter template holds %d cooler slots, want 1", len(slots))
	}
	if got != slots[0] {
		t.Fatalf("passive cooler at %v, want the shelter slot %v", got, slots)
	}
}

// On a mild map the template holds no cooler slot.
func TestShelterHoldsNoCoolerSlotOnAMildMap(t *testing.T) {
	t.Parallel()
	_, slots := coolerSearch(t, false)
	if len(slots) != 0 {
		t.Fatalf("a mild shelter template holds cooler slots %v", slots)
	}
}
