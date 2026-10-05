package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// The research bench stages in the shelter (#2043): a room the game scores a
// Barracks, standing on the layout plan's shelter interior, is planned as the
// shelter, so the bench takes the shelter template's research slot.
func TestResearchBenchTakesTheShelterTemplateSlot(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	planner, _, session, _, n := sleepingFixture(t)
	shape, ok := testPieceShapes.Get("SimpleResearchBench")
	if !ok {
		t.Fatal("no SimpleResearchBench shape in the fixture catalog")
	}
	n.onPreview = func(_ context.Context, p *bridge.BuildingPreview) {
		b, _ := p.Preview.Action.Building()
		p.Preview.Footprint = domain.Known(rectangleCells(policy.OccupiedRect(b.Cell(), shape.Size, b.Rotation())))
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
	lab, err := policy.Facility(policy.RoomRoleLaboratory)
	if err != nil {
		t.Fatal(err)
	}
	planner.definition, planner.stuff, planner.facility = "SimpleResearchBench", "", &lab
	interior := policy.Rectangle{X: 0, Z: 0, Width: 4, Height: 4}
	door := domain.Cell{X: 2, Z: 4}
	facts := reading.Projection
	facts.LayoutPlan = domain.Known(policy.LayoutPlan{Rooms: []policy.PlannedRoom{{Role: policy.PlannedShelter, Interior: interior, Door: door, DoorRot: domain.North}}})
	room := policy.Room{ID: "1", Role: domain.Known(policy.RoomRoleBarracks), Enclosed: domain.Known(true), Cells: rectangleCells(interior)}
	census := policy.RoomObservation{Shapes: testPieceShapes, Rooms: []policy.Room{room}}
	facts.Rooms = domain.Known(census)
	facts.Cells = append([]policy.SiteCell(nil), facts.Cells...)
	doorSeen := false
	for i := range facts.Cells {
		if facts.Cells[i].Cell == door {
			facts.Cells[i].Doorway, doorSeen = domain.Known(true), true
		}
	}
	if !doorSeen {
		t.Fatalf("the fixture map has no cell at the door %v (%d cells)", door, len(facts.Cells))
	}
	rooms := shelterInteriorRooms(policy.InteriorRoomsFor(lab, census, facts.Cells), facts)
	if len(rooms) != 1 || rooms[0].Role != policy.RoomRoleShelter {
		t.Fatalf("the standing room is planned as %v, want the shelter", rooms)
	}
	piece, _ := rooms[0].Piece("SimpleResearchBench")
	piece.Def = "SimpleResearchBench"
	plan, ok := policy.PlanInterior(rooms[0], piece)
	if !ok {
		t.Fatal("the shelter template plans no room")
	}
	var slot *policy.InteriorPiece
	for i, p := range plan.Pieces {
		if p.Slot == "research" {
			slot = &plan.Pieces[i]
		}
	}
	if slot == nil {
		t.Fatalf("no research slot in %v", plan.Pieces)
	}
	snapshot := session.State().Snapshot
	snapshot.Plan, snapshot.Revision = "shelter-research", 1
	selected, _, reason, err := planner.previewSearch(ctx, snapshot, facts, nil, 1, func() error { return nil })
	if err != nil || !reason.IsZero() || len(selected) != 1 {
		t.Fatalf("selected %v %q %v", selected, reason, err)
	}
	b, _ := selected[0].Action.Building()
	if b.Cell() != slot.Anchor() || b.Rotation() != slot.Rot {
		t.Fatalf("bench at %v %s, want the shelter research slot %v %s", b.Cell(), b.Rotation(), slot.Anchor(), slot.Rot)
	}
}
