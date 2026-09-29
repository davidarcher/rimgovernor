package policy

import (
	"math"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// bedroomWing returns p's bedroom wing, failing the test without one.
func testBedroomWing(t *testing.T, p LayoutPlan) Wing {
	t.Helper()
	i := bedroomWing(p.Wings)
	if i < 0 {
		t.Fatalf("no bedroom wing: spine %+v rooms %d", p.Spine, len(p.Rooms))
	}
	return p.Wings[i]
}

// checkWing asserts the wing's shape (#1213): pawns rooms, every door in
// the corridor wall, rooms on a side sharing walls, and no thoroughfare.
func checkWing(t *testing.T, p LayoutPlan, pawns int) Wing {
	t.Helper()
	w := testBedroomWing(t, p)
	if len(w.Rooms) != pawns {
		t.Fatalf("wing rooms %d, want %d", len(w.Rooms), pawns)
	}
	corridor := spineRects([]SpineSegment{w.Corridor})[0]
	sharing := 0
	step := map[domain.Rotation]domain.Cell{domain.North: {Z: 1}, domain.South: {Z: -1}, domain.East: {X: 1}, domain.West: {X: -1}}
	for _, r := range w.Rooms {
		if r.Role != ModuleBedroom {
			t.Fatal("wing room role", r.Role)
		}
		s := step[r.DoorRot]
		if !contains(corridor, domain.Cell{X: r.Door.X + s.X, Z: r.Door.Z + s.Z}) {
			t.Fatalf("door %v (%v) off the corridor %+v", r.Door, r.DoorRot, corridor)
		}
		shared := false
		for _, o := range w.Rooms {
			if o.Interior == r.Interior {
				continue
			}
			if rectsOverlap(r.Interior, roomWalls(o)) {
				t.Fatal("overlap", r, o)
			}
			// A neighbour's wall row is this room's wall row.
			a, b := roomWalls(r), roomWalls(o)
			if a.X == b.X && (a.Z+a.Height-1 == b.Z || b.Z+b.Height-1 == a.Z) {
				shared = true
			}
		}
		if shared {
			sharing++
		}
	}
	// Only a lone room on the west side (three pawns) has no neighbour.
	if want := map[bool]int{true: pawns, false: 2 * (pawns / 3)}[pawns >= 4]; sharing < want {
		t.Fatalf("%d of %d rooms share a wall, want %d", sharing, pawns, want)
	}
	if _, err := CheckRoutes(p); err != nil {
		t.Fatal("routes:", err)
	}
	return w
}

func TestBedroomWingHousesEachPawn(t *testing.T) {
	for _, pawns := range []int{1, 3, 6} {
		p := PlanCore(coreTestZones(), pawns, BuildTierCamp)
		checkWing(t, p, pawns)
		for _, r := range p.Rooms {
			if r.Role == ModuleBedroom {
				t.Fatal("a spine bedroom", r)
			}
		}
	}
}

func TestBedroomWingExtendsAtItsOpenEnd(t *testing.T) {
	p := PlanCore(coreTestZones(), 3, BuildTierCamp)
	before := checkWing(t, p, 3)
	g := Grow(p, 7, 1, BuildTierCamp)
	after := checkWing(t, g, 7)
	if after.Corridor.From != before.Corridor.From {
		t.Fatal("wing moved", before.Corridor, after.Corridor)
	}
	for i, r := range before.Rooms {
		if after.Rooms[i] != r {
			t.Fatal("room moved", r, after.Rooms[i])
		}
	}
	for i, r := range p.Rooms {
		if g.Rooms[i] != r {
			t.Fatal("spine room moved", r, g.Rooms[i])
		}
	}
}

// A new wing's rooms take the tier's size; an existing wing grown at a
// later tier keeps its rooms' size (#1214).
func TestBedroomWingRoomSizeByTier(t *testing.T) {
	size := func(t *testing.T, w Wing, want [2]int32) {
		t.Helper()
		for _, r := range w.Rooms {
			if got := [2]int32{r.Interior.Height, r.Interior.Width}; got != want {
				t.Fatalf("room %+v is %v, want %v", r.Interior, got, want)
			}
		}
	}
	for _, tier := range []BuildTier{BuildTierCamp, BuildTierPowered, BuildTierSpacer} {
		p := PlanCore(coreTestZones(), 4, tier)
		size(t, checkWing(t, p, 4), WingRoomSize(tier))
	}
	camp := PlanCore(coreTestZones(), 3, BuildTierCamp)
	grown := Grow(camp, 6, 1, BuildTierSpacer)
	size(t, checkWing(t, grown, 6), WingRoomSize(BuildTierCamp))
}

// A room the survey drops leaves a hole; the wing is refilled around it and
// its other rooms stay put.
func TestKeepWingRoomsTrimsTheCorridor(t *testing.T) {
	p := PlanCore(coreTestZones(), 6, BuildTierCamp)
	w := testBedroomWing(t, p)
	last := w.Rooms[len(w.Rooms)-1]
	kept := keepWingRooms(p.Wings, func(r LayoutRoom) bool {
		return r.Interior != last.Interior && r.Interior != w.Rooms[len(w.Rooms)-2].Interior
	})
	if len(kept) != 1 || len(kept[0].Rooms) != 4 || kept[0].Corridor.To == w.Corridor.To {
		t.Fatalf("kept %+v", kept)
	}
	p.Wings = kept
	g := Grow(p, 6, 1, BuildTierCamp)
	if got := checkWing(t, g, 6); got.Corridor != w.Corridor {
		t.Fatal("corridor", got.Corridor, w.Corridor)
	}
}

// bedroomWingReach bounds the wing's distance from the storage door
// (#1178): the wing is sited at the nearest column that fits it.
const bedroomWingReach = 20

func TestBedroomsClusterNearStorage(t *testing.T) {
	for _, pawns := range []int{3, 5, 8} {
		p := PlanCore(coreTestZones(), pawns, BuildTierCamp)
		w := checkWing(t, p, pawns)
		var store *LayoutRoom
		for i, r := range p.Rooms {
			if r.Role == ModuleStorage {
				store = &p.Rooms[i]
			}
		}
		if store == nil {
			t.Fatal("no storage")
		}
		d := math.Hypot(float64(w.Corridor.From.X-store.Door.X), float64(w.Corridor.From.Z-store.Door.Z))
		if d > bedroomWingReach {
			t.Errorf("%d: wing base %.1f cells from storage, want <= %d", pawns, d, bedroomWingReach)
		}
	}
}
