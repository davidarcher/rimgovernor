package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// shelterGapTo is the clear cells between the shelter's walls and the nearest
// other room's walls (Chebyshev).
func shelterGapTo(p LayoutPlan, shelter PlannedRoom) int32 {
	walls := roomWalls(shelter)
	gap := int32(1 << 20)
	near := func(o Rectangle) {
		dx := max(o.X-(walls.X+walls.Width), walls.X-(o.X+o.Width), 0)
		dz := max(o.Z-(walls.Z+walls.Height), walls.Z-(o.Z+o.Height), 0)
		gap = min(gap, max(dx, dz))
	}
	for _, r := range p.AllRooms() {
		if r.Role != PlannedShelter {
			near(roomWalls(r))
		}
	}
	return gap
}

func shelterOf(t *testing.T, p LayoutPlan) PlannedRoom {
	t.Helper()
	var found []PlannedRoom
	for _, r := range p.AllRooms() {
		if r.Role == PlannedShelter {
			found = append(found, r)
		}
	}
	if len(found) != 1 {
		t.Fatalf("plan holds %d shelters, want one", len(found))
	}
	return found[0]
}

func TestPlanHoldsOneShelterFiveTilesClearOnOpenGround(t *testing.T) {
	p := corePlan(coreTestZones(), 3, BuildTierCamp)
	s := shelterOf(t, p)
	if gap := shelterGapTo(p, s); gap < shelterGap {
		t.Fatalf("shelter %v is %d tiles from the nearest room, want at least %d", s.Interior, gap, shelterGap)
	}
	want := ShelterSizes(3, ShelterCampfires(false), 0)[0]
	if s.Interior.Width != want[0] || s.Interior.Height != want[1] {
		t.Fatalf("shelter interior %dx%d, sized %v", s.Interior.Width, s.Interior.Height, want)
	}
	if _, err := CheckRoutes(p); err != nil {
		t.Fatal(err)
	}
	if !noThroughfare[PlannedShelter] {
		t.Fatal("the shelter is a thoroughfare")
	}
}

// A replan keeps the shelter it holds and never adds a second.
func TestGrowKeepsTheOneShelter(t *testing.T) {
	p := corePlan(coreTestZones(), 3, BuildTierCamp)
	before := shelterOf(t, p)
	after := shelterOf(t, growPlan(p, 12, 1, BuildTierCamp))
	if !before.Same(after) {
		t.Fatal("shelter moved", before, after)
	}
}

// The gap shrinks, then the shelter takes a hallway slot, when the ground
// leaves no room for the preferred clearance.
func TestShelterFallbackLadder(t *testing.T) {
	zones := coreTestZones()
	open := corePlan(zones, 3, BuildTierCamp)
	base := open
	base.Rooms = nil
	for _, r := range open.Rooms {
		if r.Role != PlannedShelter {
			base.Rooms = append(base.Rooms, r)
		}
	}
	seed := open.Spine[0].From
	size := ShelterSizes(3, ShelterCampfires(false), 0)[0]
	all := base.AllRooms()
	halls := spineRects(base.Hallways())

	// Ground within eight cells of the rooms only.
	g := newCoreGrid(zones, nil)
	for c := range g.core {
		keep := false
		for _, r := range all {
			if rectsOverlap(Rectangle{X: c.X, Z: c.Z, Width: 1, Height: 1}, pad(roomWalls(r), 8)) {
				keep = true
			}
		}
		if !keep {
			delete(g.core, c)
		}
	}
	for gap := shelterGap; gap > 4; gap-- {
		if _, ok := g.shelterOnOpenGround(all, halls, seed, size, gap); ok {
			t.Fatalf("shelter sited %d clear on ground that leaves less", gap)
		}
	}
	spine, rooms := g.siteShelter(base.Spine, base.Rooms, base.Wings, seed, 3, false, false)
	crowded := base
	crowded.Spine, crowded.Rooms = spine, rooms
	s := shelterOf(t, crowded)
	if gap := shelterGapTo(crowded, s); gap >= shelterGap {
		t.Fatalf("crowded site still sited %d clear", gap)
	}
}

func TestShelterSizeGrowsWithColonistsAndCampfires(t *testing.T) {
	cells := func(n, fires int) int32 {
		s := ShelterSizes(n, fires, 0)[0]
		return s[0] * s[1]
	}
	for n := 1; n < 20; n++ {
		if cells(n+1, 1) < cells(n, 1) {
			t.Fatalf("size shrank from %d to %d colonists", n, n+1)
		}
	}
	if cells(20, 1) <= cells(2, 1) {
		t.Fatal("size does not grow with colonists")
	}
	if ShelterInteriorArea(4, 3, 0) <= ShelterInteriorArea(4, 1, 0) {
		t.Fatal("area does not grow with campfires")
	}
	// Every shape holds the contents.
	for n := 1; n < 30; n++ {
		for _, s := range ShelterSizes(n, 2, 0) {
			if int(s[0]*s[1]) < ShelterInteriorArea(n, 2, 0) || s[0] < s[1] {
				t.Fatalf("shape %v too small for %d colonists", s, n)
			}
		}
	}
}

func TestShelterDoorFacesTheBase(t *testing.T) {
	in := Rectangle{X: 10, Z: 10, Width: 6, Height: 4}
	for _, c := range []struct {
		seed domain.Cell
		rot  domain.Rotation
		door domain.Cell
	}{
		{domain.Cell{X: 40, Z: 12}, domain.East, domain.Cell{X: 16, Z: 12}},
		{domain.Cell{X: -20, Z: 12}, domain.West, domain.Cell{X: 9, Z: 12}},
		{domain.Cell{X: 13, Z: 60}, domain.North, domain.Cell{X: 13, Z: 14}},
		{domain.Cell{X: 13, Z: -30}, domain.South, domain.Cell{X: 13, Z: 9}},
	} {
		r := shelterRoom(in, c.seed)
		if r.DoorRot != c.rot || r.Door != c.door {
			t.Fatalf("seed %v: door %v %v, want %v %v", c.seed, r.Door, r.DoorRot, c.door, c.rot)
		}
	}
}
