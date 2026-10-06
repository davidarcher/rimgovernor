package policy

import (
	"math"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// bedroomWing returns p's bedroom wing, failing the test without one.
func testBedroomWing(t *testing.T, p LayoutPlan) Wing {
	t.Helper()
	i := bedroomWings(p.Wings)
	if len(i) == 0 {
		t.Fatalf("no bedroom wing: spine %+v rooms %d", p.Spine, len(p.Rooms))
	}
	return p.Wings[i[0]]
}

// wingCounts is the room count of each bedroom wing, in order.
func wingCounts(p LayoutPlan) []int {
	var out []int
	for _, i := range bedroomWings(p.Wings) {
		out = append(out, len(p.Wings[i].Rooms))
	}
	return out
}

// A plan for N pawns holds ceil(N/10) bedroom wings, each planned at full
// size when sited; one more pawn sites a new wing and moves nothing (#1950).
func TestBedroomWingsPlannedAtFullSize(t *testing.T) {
	p := corePlan(coreTestZones(), 25, BuildTierCamp)
	if got := wingCounts(p); len(got) != 3 || got[0] != 10 || got[1] != 10 || got[2] != 10 {
		t.Fatalf("wings %v, want [10 10 10]", got)
	}
	if _, err := CheckRoutes(p); err != nil {
		t.Fatal("routes:", err)
	}
	all := map[Rectangle]bool{}
	for _, i := range bedroomWings(p.Wings) {
		w := p.Wings[i]
		// Each wing keeps its own reserve: no other wing's room is on it.
		res := wingReserve(w)
		for _, j := range bedroomWings(p.Wings) {
			if j == i {
				continue
			}
			for _, r := range p.Wings[j].Rooms {
				if rectsOverlap(roomWalls(r), res) {
					t.Fatalf("wing %d room %+v on wing %d reserve %+v", j, r.Interior, i, res)
				}
			}
		}
		for _, r := range w.Rooms {
			if all[r.Interior] {
				t.Fatal("room in two wings", r.Interior)
			}
			all[r.Interior] = true
		}
	}
	// One pawn past capacity sites a fourth wing and leaves every existing
	// wing, room and corridor exactly as it was.
	g := growPlan(p, 31, 1, BuildTierCamp)
	if got := wingCounts(g); len(got) != 4 {
		t.Fatalf("grown wings %v, want 4 wings", got)
	}
	for k, i := range bedroomWings(p.Wings) {
		before, after := p.Wings[i], g.Wings[bedroomWings(g.Wings)[k]]
		if after.Corridor != before.Corridor || len(after.Rooms) != len(before.Rooms) {
			t.Fatal("wing changed", before.Corridor, after.Corridor)
		}
		for n, r := range before.Rooms {
			if !after.Rooms[n].Same(r) {
				t.Fatal("room moved", r)
			}
		}
	}
	if _, err := CheckRoutes(g); err != nil {
		t.Fatal("routes:", err)
	}
}

// checkWing asserts the wing's shape (#1213): wingMaxRooms rooms, every
// door in the corridor wall, rooms on a side sharing walls, and no
// thoroughfare.
func checkWing(t *testing.T, p LayoutPlan) Wing {
	t.Helper()
	w := testBedroomWing(t, p)
	pawns := len(w.Rooms)
	if pawns != wingMaxRooms {
		t.Fatalf("wing rooms %d, want %d", pawns, wingMaxRooms)
	}
	corridor := spineRects([]SpineSegment{w.Corridor})[0]
	sharing := 0
	step := map[domain.Rotation]domain.Cell{domain.North: {Z: 1}, domain.South: {Z: -1}, domain.East: {X: 1}, domain.West: {X: -1}}
	for _, r := range w.Rooms {
		if r.Role != PlannedBedroom {
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
	if sharing != pawns {
		t.Fatalf("%d of %d rooms share a wall", sharing, pawns)
	}
	if _, err := CheckRoutes(p); err != nil {
		t.Fatal("routes:", err)
	}
	return w
}

func TestBedroomWingHousesEachPawn(t *testing.T) {
	for _, pawns := range []int{1, 3, 6, 10} {
		p := corePlan(coreTestZones(), pawns, BuildTierCamp)
		checkWing(t, p)
		if got := wingCounts(p); len(got) != 1 {
			t.Fatalf("%d pawns: wings %v, want one", pawns, got)
		}
		for _, r := range p.Rooms {
			if r.Role == PlannedBedroom {
				t.Fatal("a spine bedroom", r)
			}
		}
	}
}

// Growing within a wing's capacity changes nothing; the corridor length is
// fixed at siting.
func TestBedroomWingNeverGrows(t *testing.T) {
	p := corePlan(coreTestZones(), 3, BuildTierCamp)
	before := checkWing(t, p)
	g := growPlan(p, 7, 1, BuildTierCamp)
	after := checkWing(t, g)
	if len(wingCounts(g)) != 1 || after.Corridor != before.Corridor {
		t.Fatal("wing changed", before.Corridor, after.Corridor)
	}
	for i, r := range before.Rooms {
		if !after.Rooms[i].Same(r) {
			t.Fatal("room moved", r, after.Rooms[i])
		}
	}
	for i, r := range p.Rooms {
		if !g.Rooms[i].Same(r) {
			t.Fatal("spine room moved", r, g.Rooms[i])
		}
	}
}

// A new wing's rooms take the tier's size; an existing wing keeps its
// rooms' size at a later tier (#1214).
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
		p := corePlan(coreTestZones(), 4, tier)
		size(t, checkWing(t, p), WingRoomSize(tier))
	}
	camp := corePlan(coreTestZones(), 3, BuildTierCamp)
	// A later tier retires the smaller wing and sites a new one (#1219).
	grown := growPlan(camp, 6, 1, BuildTierSpacer)
	size(t, grown.Wings[0], WingRoomSize(BuildTierCamp))
	if grown.Wings[0].Purpose != WingBedroomsRetiring {
		t.Fatal("camp wing not retiring", grown.Wings[0].Purpose)
	}
	active := grown.Wings[bedroomWings(grown.Wings)[0]]
	size(t, active, WingRoomSize(BuildTierSpacer))
}

// A room the survey drops shortens the corridor; the wing's other rooms
// stay put and the wing is not refilled.
func TestKeepWingRoomsTrimsTheCorridor(t *testing.T) {
	p := corePlan(coreTestZones(), 6, BuildTierCamp)
	w := testBedroomWing(t, p)
	last := w.Rooms[len(w.Rooms)-1]
	kept := keepWingRooms(p.Wings, func(r PlannedRoom) bool {
		return r.Interior != last.Interior && r.Interior != w.Rooms[len(w.Rooms)-2].Interior
	})
	if len(kept) != 1 || len(kept[0].Rooms) != 8 || kept[0].Corridor.To == w.Corridor.To {
		t.Fatalf("kept %+v", kept)
	}
	p.Wings = kept
	g := growPlan(p, 6, 1, BuildTierCamp)
	first := g.Wings[bedroomWings(g.Wings)[0]]
	if len(first.Rooms) != 8 || first.Corridor != kept[0].Corridor {
		t.Fatalf("trimmed wing %d rooms, corridor %+v", len(first.Rooms), first.Corridor)
	}
}

// bedroomWingReach bounds the wing's distance from the dining door: siteBlock
// takes the most rooms, then the cheapest ground, then the column nearest
// dining's door. Storage is no anchor, so a wider warehouse moves its door
// without moving the wing.
const bedroomWingReach = 20

func TestBedroomsClusterNearDining(t *testing.T) {
	for _, pawns := range []int{3, 5, 8} {
		p := corePlan(coreTestZones(), pawns, BuildTierCamp)
		w := checkWing(t, p)
		var dining *PlannedRoom
		for i, r := range p.Rooms {
			if r.Role == PlannedDining {
				dining = &p.Rooms[i]
			}
		}
		if dining == nil {
			t.Fatal("no dining")
		}
		d := math.Hypot(float64(w.Corridor.From.X-dining.Door.X), float64(w.Corridor.From.Z-dining.Door.Z))
		if d > bedroomWingReach {
			t.Errorf("%d: wing base %.1f cells from dining, want <= %d", pawns, d, bedroomWingReach)
		}
	}
}
