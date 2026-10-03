package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func bedroomSlots(pieces []InteriorPiece) map[string]InteriorPiece {
	out := map[string]InteriorPiece{}
	for _, p := range pieces {
		out[p.Slot] = p
	}
	return out
}

// headCells are a canonical South bed's sleeping cells: its top row.
func headCells(bed InteriorPiece) []domain.Cell {
	var out []domain.Cell
	for u := bed.Rect.X; u < bed.Rect.X+bed.Rect.Width; u++ {
		out = append(out, domain.Cell{X: u, Z: bed.Rect.Z + bed.Rect.Height - 1})
	}
	return out
}

func TestBedroomFurnitureLinksToTheBed(t *testing.T) {
	for _, size := range [][3]int32{{5, 4, 0}, {5, 4, 1}, {4, 4, 1}, {6, 5, 2}, {7, 6, 1}} {
		plan := assertInteriorRepeatable(t, RoomRoleBedroom, size[0], size[1], size[2])
		s := bedroomSlots(plan.Canonical)
		bed := s["bed"]
		if bed.Rot != domain.South || bed.Rect.Z+bed.Rect.Height != plan.Frame.Depth {
			t.Errorf("%v: bed %+v %s not headed on the back wall", size, bed.Rect, bed.Rot)
		}
		table, ok := s["end_table"]
		if !ok {
			t.Fatalf("%v: no end table in %+v", size, plan.Canonical)
		}
		adjacent := false
		for _, h := range headCells(bed) {
			d := domain.Cell{X: table.Rect.X - h.X, Z: table.Rect.Z - h.Z}
			adjacent = adjacent || (d.Z == 0 && (d.X == 1 || d.X == -1)) || (d.X == 0 && (d.Z == 1 || d.Z == -1))
		}
		if !adjacent {
			t.Errorf("%v: end table %+v not cardinal to the bed head", size, table.Rect)
		}
		dresser, ok := s["dresser"]
		if !ok {
			t.Fatalf("%v: no dresser in %+v", size, plan.Canonical)
		}
		dx := float64(2*dresser.Rect.X+dresser.Rect.Width) - float64(2*bed.Rect.X+bed.Rect.Width)
		dz := float64(2*dresser.Rect.Z+dresser.Rect.Height) - float64(2*bed.Rect.Z+bed.Rect.Height)
		if dx*dx+dz*dz > 4*36 {
			t.Errorf("%v: dresser %+v beyond 6 cells of the bed", size, dresser.Rect)
		}
		// A four-wide back row is full with bed, table and dresser.
		if _, ok := s["lamp"]; !ok && plan.Frame.Width > 4 {
			t.Errorf("%v: no lamp", size)
		}
		// Everything but the back row stays clear floor.
		for _, p := range plan.Canonical {
			if p.Slot != "bed" && p.Rect.Z != plan.Frame.Depth-1 {
				t.Errorf("%v: %s off the back wall", size, p.Slot)
			}
		}
	}
}

func TestBedroomCrampedRoomsKeepTheBed(t *testing.T) {
	plan := assertInteriorRepeatable(t, RoomRoleBedroom, 3, 3, 1)
	s := bedroomSlots(plan.Canonical)
	if _, ok := s["bed"]; !ok {
		t.Fatalf("3x3: no bed")
	}
	if _, ok := s["dresser"]; ok {
		t.Errorf("3x3: a dresser fits beside a centred bed: %+v", plan.Canonical)
	}
	for _, size := range [][3]int32{{1, 3, 0}, {2, 3, 0}} {
		assertInteriorRepeatable(t, RoomRoleBedroom, size[0], size[1], size[2])
	}
}

func TestBedroomDoubleBedLayout(t *testing.T) {
	f := InteriorFrame{Shapes: testShapes, Width: 6, Depth: 5, Entrance: 1, Doors: []domain.Cell{{X: 1, Z: -1}}}
	pieces, ok := planBedroomWith(f, "DoubleBed", domain.Cell{X: 2, Z: 2})
	if !ok {
		t.Fatal("no double-bed plan")
	}
	plan := InteriorPlan{Template: "bedroom", Frame: f, Canonical: pieces}
	assertInteriorRegular(t, plan)
	s := bedroomSlots(pieces)
	if s["bed"].Rect != (Rectangle{X: 2, Z: 3, Width: 2, Height: 2}) || s["end_table"].Rect.X != 1 || s["dresser"].Rect.X != 4 {
		t.Errorf("double bed layout %+v", pieces)
	}
	if _, ok := planBedroomWith(InteriorFrame{Shapes: testShapes, Width: 1, Depth: 5, Doors: []domain.Cell{{X: 0, Z: -1}}}, "DoubleBed", domain.Cell{X: 2, Z: 2}); ok {
		t.Error("a double bed planned in a one-wide room")
	}
}

// A requested or standing double bed is planned at its 2x2 size, the end
// table touching a head cell, and repeatably across door sides.
func TestBedroomPlansTheRequestedBed(t *testing.T) {
	for _, def := range []string{"DoubleBed", "RoyalBed"} {
		for _, room := range interiorRoomsAround(RoomRoleBedroom, 6, 5, 1) {
			plan, ok := PlanInterior(room, testShapes.Defs[def])
			if !ok {
				t.Fatalf("%s %+v: no plan", def, room)
			}
			assertInteriorRegular(t, plan)
			s := bedroomSlots(plan.Canonical)
			bed, table := s["bed"], s["end_table"]
			if bed.Def != def || bed.Rect.Width != 2 || bed.Rect.Height != 2 {
				t.Fatalf("%s: bed %+v", def, bed)
			}
			adjacent := false
			for _, h := range headCells(bed) {
				adjacent = adjacent || (table.Rect.Z == h.Z && (table.Rect.X == h.X-1 || table.Rect.X == h.X+1))
			}
			if !adjacent {
				t.Errorf("%s: end table %+v off the head", def, table.Rect)
			}
			room.Standing = []string{def}
			standing, ok := PlanInterior(room, InteriorPieceDef{})
			if !ok || standing.Canonical[0] != plan.Canonical[0] {
				t.Errorf("%s: standing plan %+v", def, standing.Canonical)
			}
		}
	}
}

func plannedSpace(plan InteriorPlan) float64 {
	blocked := 0
	for _, p := range plan.Canonical {
		blocked += int(p.Rect.Width * p.Rect.Height)
	}
	return bedroomSpace(plan.Frame.Width, plan.Frame.Depth, blocked)
}

// The standard room by build tier (#1214): its size, its furniture set, and
// planned space at or above bedroomMinSpace.
func TestBedroomStandardRoomByTier(t *testing.T) {
	cases := []struct {
		tier BuildTier
		size [2]int32
		set  []string
	}{
		{BuildTierCamp, [2]int32{3, 4}, []string{"bed", "end_table"}},
		{BuildTierMasonry, [2]int32{3, 4}, []string{"bed", "end_table"}},
		{BuildTierPowered, [2]int32{4, 4}, []string{"bed", "end_table", "dresser"}},
		{BuildTierIndustrial, [2]int32{4, 4}, []string{"bed", "end_table", "dresser"}},
		{BuildTierSpacer, [2]int32{4, 5}, []string{"bed", "end_table", "dresser"}},
	}
	for _, c := range cases {
		size := WingRoomSize(c.tier)
		if size != c.size {
			t.Errorf("%s: room %v, want %v", c.tier, size, c.size)
			continue
		}
		plan := assertInteriorRepeatable(t, RoomRoleBedroom, size[0], size[1], size[0]/2)
		s := bedroomSlots(plan.Canonical)
		for _, slot := range c.set {
			if _, ok := s[slot]; !ok {
				t.Errorf("%s %v: no %s in %+v", c.tier, size, slot, plan.Canonical)
			}
		}
		if len(plan.Canonical) != len(c.set) {
			t.Errorf("%s %v: pieces %+v, want %v", c.tier, size, plan.Canonical, c.set)
		}
		if space := plannedSpace(plan); space < bedroomMinSpace {
			t.Errorf("%s %v: space %.1f below %.1f", c.tier, size, space, bedroomMinSpace)
		}
	}
}

// Optional pieces never take a room below the space floor; only the bed may.
func TestBedroomSpaceBudgetDropsOptionalPieces(t *testing.T) {
	for _, size := range [][2]int32{{3, 4}, {4, 4}, {4, 5}, {3, 3}, {5, 4}, {5, 5}} {
		plan := assertInteriorRepeatable(t, RoomRoleBedroom, size[0], size[1], size[0]/2)
		if space := plannedSpace(plan); space < bedroomMinSpace && len(plan.Canonical) > 1 {
			t.Errorf("%v: space %.1f with %+v", size, space, plan.Canonical)
		}
	}
}
