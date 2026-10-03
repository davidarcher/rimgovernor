package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// A bed for a generic 4x4 room with a door in its north wall takes the
// bedroom template's slot: head against the south wall on the centre line,
// facing away from the door. With the slot occupied the search falls back
// to a snap cell of the room (#800).
func TestFacilityBedTakesTheInteriorTemplateSlot(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	planner, _, session, _, n := sleepingFixture(t)
	n.onPreview = func(_ context.Context, p *bridge.BuildingPreview) {
		b, _ := p.Preview.Action.Building()
		p.Preview.Footprint = domain.Known(rectangleCells(policy.OccupiedRect(b.Cell(), domain.Cell{X: 1, Z: 2}, b.Rotation())))
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
	bedroom, err := policy.Facility(policy.RoomRoleBedroom)
	if err != nil {
		t.Fatal(err)
	}
	planner.definition, planner.stuff, planner.facility = "Bed", "", &bedroom
	room := policy.Room{ID: "1", Role: domain.Known(policy.RoomRoleRoom), Cells: rectangleCells(policy.Rectangle{X: 0, Z: 0, Width: 4, Height: 4})}
	snapshot := session.State().Snapshot
	snapshot.Plan, snapshot.Revision = "interior-test", 1
	check := func() error { return nil }
	search := func(occupied domain.Cell) policy.Preview {
		t.Helper()
		facts := reading.Projection
		facts.Rooms = domain.Known(policy.RoomObservation{Shapes: testPieceShapes, Rooms: []policy.Room{room}})
		facts.Cells = append([]policy.SiteCell(nil), facts.Cells...)
		for i := range facts.Cells {
			switch facts.Cells[i].Cell {
			case domain.Cell{X: 2, Z: 4}:
				facts.Cells[i].Doorway = domain.Known(true)
			case occupied:
				facts.Cells[i].Occupied = domain.Known(true)
			}
		}
		selected, _, reason, err := planner.previewSearch(ctx, snapshot, facts, nil, 1, check)
		if err != nil || !reason.IsZero() || len(selected) != 1 {
			t.Fatalf("selected %v %q %v", selected, reason, err)
		}
		return selected[0]
	}
	b, _ := search(domain.Cell{X: -1, Z: -1}).Action.Building()
	if b.Cell() != (domain.Cell{X: 2, Z: 0}) || b.Rotation() != domain.North {
		t.Fatalf("bed at %v %s, want the template slot 2,0 north (head on the far wall)", b.Cell(), b.Rotation())
	}
	b, _ = search(domain.Cell{X: 2, Z: 0}).Action.Building()
	snap := map[domain.Cell]bool{}
	interior := policy.InteriorRoom{Interior: policy.Rectangle{X: 0, Z: 0, Width: 4, Height: 4}}
	for _, c := range policy.InteriorSnapAnchors(interior, []domain.Cell{{X: 2, Z: 0}}) {
		snap[c] = true
	}
	if !snap[b.Cell()] {
		t.Fatalf("fallback bed at %v, off every snap cell", b.Cell())
	}
}

func rectangleCells(r policy.Rectangle) []domain.Cell {
	var cells []domain.Cell
	for x := r.X; x < r.X+r.Width; x++ {
		for z := r.Z; z < r.Z+r.Height; z++ {
			cells = append(cells, domain.Cell{X: x, Z: z})
		}
	}
	return cells
}
