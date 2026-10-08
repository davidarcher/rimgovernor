package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// rockOpen is sitingOpen with the listed cells natural rock and the others
// (kept) neither walkable nor rock; fogged cells are unlisted.
func rockOpen(rock, kept, fogged []domain.Cell) stockpileOpen {
	open := sitingOpen()
	for _, c := range rock {
		open.cells[c] = SiteCell{Cell: c, Walkable: domain.Known(false), Things: RockThings(true), Zone: domain.Known(false)}
	}
	for _, c := range kept {
		open.cells[c] = SiteCell{Cell: c, Walkable: domain.Known(false), Things: OccupantThings(true), Zone: domain.Known(false)}
	}
	for _, c := range fogged {
		delete(open.cells, c)
	}
	return open
}

var storeInterior = Rectangle{X: 4, Z: 4, Width: 5, Height: 3}

func TestHalfDugEnclosedCavityYieldsNoZone(t *testing.T) {
	t.Parallel()
	site := StoreSite{Role: domain.GeneralRole, Interior: storeInterior}
	open := rockOpen([]domain.Cell{{X: 8, Z: 4}, {X: 8, Z: 5}}, nil, nil)
	if cells := site.Cells(open); cells != nil {
		t.Fatalf("half-dug room zoned %v", cells)
	}
	if cells := site.Cells(rockOpen(nil, nil, []domain.Cell{{X: 6, Z: 5}})); cells != nil {
		t.Fatalf("room with an unseen cell zoned %v", cells)
	}
	rectangle := StoreSite{Role: "food", Interior: storeInterior, Width: 2, Height: 2, Anchor: domain.Cell{X: 5, Z: 5}}
	if cells := rectangle.Cells(open); cells != nil {
		t.Fatalf("half-dug room zoned a rectangle %v", cells)
	}
}

func TestFullyOpenRoomYieldsOneWholeRoomZone(t *testing.T) {
	t.Parallel()
	site := StoreSite{Role: domain.GeneralRole, Interior: storeInterior}
	if cells := site.Cells(rockOpen(nil, nil, nil)); len(cells) != 15 {
		t.Fatalf("whole room zone %d cells: %v", len(cells), cells)
	}
}

func TestKeptCellStaysInTheRoomAndOutOfTheZone(t *testing.T) {
	t.Parallel()
	site := StoreSite{Role: domain.GeneralRole, Interior: storeInterior}
	kept := domain.Cell{X: 5, Z: 5}
	reading := InteriorOf(storeInterior, nil)
	if reading.Ready() {
		t.Fatal("an unlisted interior is unseen, not ready")
	}
	cells := site.Cells(rockOpen(nil, []domain.Cell{kept}, nil))
	if len(cells) != 14 || cellSet(cells)[kept] {
		t.Fatalf("zone %v must leave the kept cell out", cells)
	}
}

func TestKeptCellsThatSplitTheStoreRetainBothParts(t *testing.T) {
	t.Parallel()
	site := StoreSite{Role: domain.GeneralRole, Interior: storeInterior}
	// A kept wall column divides the room into unequal components.
	var wall []domain.Cell
	for z := int32(4); z < 7; z++ {
		wall = append(wall, domain.Cell{X: 5, Z: z})
	}
	open := rockOpen(nil, wall, nil)
	cells := site.Cells(open)
	if len(cells) != 12 || !cellSet(cells)[domain.Cell{X: 8, Z: 6}] || !cellSet(cells)[domain.Cell{X: 4, Z: 4}] {
		t.Fatalf("stockpile ground %v must keep both components", cells)
	}
	if rects := site.Rectangles(open); len(rects) != 1 || rects[0] != storeInterior {
		t.Fatalf("drag must retain the whole room: %v", rects)
	}
}

func TestDugStoreRoomDigsAheadOfOtherDugRoomsAndStone(t *testing.T) {
	t.Parallel()
	plan := LayoutPlan{Rooms: []PlannedRoom{
		{Role: PlannedKitchen, Interior: Rectangle{X: 10, Z: 10, Width: 5, Height: 5}, Dug: true},
		{Role: PlannedStorage, Interior: Rectangle{X: 30, Z: 10, Width: 15, Height: 7}, Dug: true},
		{Role: PlannedStorage, Interior: Rectangle{X: 60, Z: 10, Width: 15, Height: 7}},
	}}
	store, other, open, stone := domain.Cell{X: 31, Z: 11}, domain.Cell{X: 11, Z: 11}, domain.Cell{X: 61, Z: 11}, domain.Cell{X: 0, Z: 0}
	if plan.MineTier(store) >= plan.MineTier(other) || plan.MineTier(other) >= plan.MineTier(stone) {
		t.Fatalf("tiers store %d, dug room %d, stone %d", plan.MineTier(store), plan.MineTier(other), plan.MineTier(stone))
	}
	if plan.MineTier(open) != MineTierStone {
		t.Fatalf("an open-ground store room digs nothing: tier %d", plan.MineTier(open))
	}
	if MineTierOre >= MineTierStore {
		t.Fatal("ore stays first")
	}
}
