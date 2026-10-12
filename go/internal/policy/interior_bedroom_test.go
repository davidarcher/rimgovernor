package policy

import (
	"testing"
)

func bedroomSlots(pieces []InteriorPiece) map[string]InteriorPiece {
	out := map[string]InteriorPiece{}
	for _, p := range pieces {
		out[p.Slot] = p
	}
	return out
}

// bedroomLinks is the facility links the plan serves and wants, priced by the
// objective the placer and the search use.
func bedroomLinks(room InteriorRoom, plan InteriorPlan) (served, wanted int) {
	s := ScoreInterior(InteriorObjective{Region: room.Floor(), Doors: room.Doors, Pieces: plan.Pieces, Shapes: testShapes})
	return s.LinksServed, s.LinksWanted
}

func TestBedroomFurnitureLinksToTheBed(t *testing.T) {
	for _, size := range [][3]int32{{5, 4, 0}, {5, 4, 1}, {4, 4, 1}, {6, 5, 2}, {7, 6, 1}} {
		room, plan := planAround(t, RoomRoleBedroom, size[0], size[1], size[2], InteriorPieceDef{})
		s := bedroomSlots(plan.Pieces)
		for _, slot := range []string{"bed", "end_table", "dresser"} {
			if _, ok := s[slot]; !ok {
				t.Fatalf("%v: no %s in %v", size, slot, slotNames(plan.Pieces))
			}
		}
		if served, wanted := bedroomLinks(room, plan); wanted < 2 || served != wanted {
			t.Errorf("%v: the end table and dresser serve %d of %d links", size, served, wanted)
		}
		// A four-wide back row is full with bed, table and dresser.
		if _, ok := s["lamp"]; !ok && size[0] > 4 {
			t.Errorf("%v: no lamp", size)
		}
	}
}

func TestBedroomCrampedRoomsKeepTheBed(t *testing.T) {
	_, plan := planAround(t, RoomRoleBedroom, 3, 3, 1, InteriorPieceDef{})
	s := bedroomSlots(plan.Pieces)
	if _, ok := s["bed"]; !ok {
		t.Fatalf("3x3: no bed")
	}
	if _, ok := s["dresser"]; ok {
		t.Errorf("3x3: a dresser fits beside a centred bed: %v", slotNames(plan.Pieces))
	}
}

// A requested or standing double bed is planned at its 2x2 size with the end
// table linked to it.
func TestBedroomPlansTheRequestedBed(t *testing.T) {
	for _, def := range []string{"DoubleBed", "RoyalBed"} {
		for _, room := range interiorRoomsAround(RoomRoleBedroom, 6, 5, 1) {
			plan, ok := PlanRoomInterior(room, testShapes.Defs[def])
			if !ok {
				t.Fatalf("%s %+v: no plan", def, room)
			}
			s := bedroomSlots(plan.Pieces)
			bed := s["bed"]
			if _, ok := s["end_table"]; !ok || bed.Def != def || bed.Rect.Width != 2 || bed.Rect.Height != 2 {
				t.Fatalf("%s: bed %+v, slots %v", def, bed, slotNames(plan.Pieces))
			}
			if served, wanted := bedroomLinks(room, plan); served < 1 {
				t.Errorf("%s: the end table serves %d of %d links", def, served, wanted)
			}
			room.Standing = []string{def}
			standing, ok := PlanRoomInterior(room, InteriorPieceDef{})
			if !ok || bedroomSlots(standing.Pieces)["bed"].Def != def {
				t.Errorf("%s: standing plan %v", def, slotNames(standing.Pieces))
			}
		}
	}
}

func plannedSpace(size [2]int32, plan InteriorPlan) float64 {
	blocked := 0
	for _, p := range plan.Pieces {
		blocked += int(p.Rect.Width * p.Rect.Height)
	}
	return bedroomSpace(size[0], size[1], blocked)
}

// The standard room by tech tier: its size, its furniture set, and
// planned space at or above bedroomMinSpace.
func TestBedroomStandardRoomByTier(t *testing.T) {
	cases := []struct {
		tier TechTier
		size [2]int32
		set  []string
	}{
		{TechTierCamp, [2]int32{3, 4}, []string{"bed", "end_table"}},
		{TechTierMasonry, [2]int32{3, 4}, []string{"bed", "end_table"}},
		{TechTierPowered, [2]int32{4, 4}, []string{"bed", "end_table", "dresser"}},
		{TechTierIndustrial, [2]int32{4, 4}, []string{"bed", "end_table", "dresser"}},
		{TechTierSpacer, [2]int32{4, 5}, []string{"bed", "end_table", "dresser"}},
	}
	for _, c := range cases {
		size := c.size
		_, plan := planAround(t, RoomRoleBedroom, size[0], size[1], size[0]/2, InteriorPieceDef{})
		s := bedroomSlots(plan.Pieces)
		for _, slot := range c.set {
			if _, ok := s[slot]; !ok {
				t.Errorf("%s %v: no %s in %v", c.tier, size, slot, slotNames(plan.Pieces))
			}
		}
		if small := size[0]*size[1] < bedroomFullSetTiles; small && len(plan.Pieces) != len(c.set) {
			t.Errorf("%s %v: pieces %v, want exactly %v", c.tier, size, slotNames(plan.Pieces), c.set)
		}
		if space := plannedSpace(size, plan); space < bedroomMinSpace {
			t.Errorf("%s %v: space %.1f below %.1f", c.tier, size, space, bedroomMinSpace)
		}
	}
}

// Optional pieces never take a room below the space floor; only the bed may.
func TestBedroomSpaceBudgetDropsOptionalPieces(t *testing.T) {
	for _, size := range [][2]int32{{3, 4}, {4, 4}, {4, 5}, {3, 3}, {5, 4}, {5, 5}} {
		_, plan := planAround(t, RoomRoleBedroom, size[0], size[1], size[0]/2, InteriorPieceDef{})
		if space := plannedSpace(size, plan); space < bedroomMinSpace && len(plan.Pieces) > 1 {
			t.Errorf("%v: space %.1f with %v", size, space, slotNames(plan.Pieces))
		}
	}
}
