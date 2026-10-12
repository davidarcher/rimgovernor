package policy

import (
	"reflect"
	"runtime"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// flatEmbedder is the pricing of a flat n x n map for hand-made groups.
func flatEmbedder(t *testing.T, n int32, groups []RoomGroup) *embedder {
	t.Helper()
	s := syntheticSurvey(n, func(x, z int32) SurveyCell { return SurveyCell{Walkable: true, Fertility: 0.5} })(t)
	in := EmbedInput{Groups: groups, Zones: Zone(s), Survey: s, RoofDistance: testRoof}
	depth, _, err := roomLimits(in.RoofDistance)
	if err != nil {
		t.Fatal(err)
	}
	return newEmbedder(in, depth, newPlanScorer(in.Zones, in.Reservations, in.Survey), obstacleLevels[0])
}

func TestUncoveredCountsTheUnionOnce(t *testing.T) {
	k := Rectangle{X: 0, Z: 0, Width: 10, Height: 10}
	rects := []Rectangle{{X: 0, Z: 0, Width: 6, Height: 4}, {X: 4, Z: 2, Width: 6, Height: 4}, {X: 20, Z: 20, Width: 3, Height: 3}}
	// 24 + 24 cells less the 2x2 they share.
	if got := uncovered(k, rects, nil); got != 100-44 {
		t.Fatalf("uncovered %d, want 56", got)
	}
	if got := uncovered(k, nil, nil); got != 100 {
		t.Fatalf("nothing covers: %d", got)
	}
	left := newPrefix(10, 10, func(c domain.Cell) int { return b2i(c.X < 5) })
	if got := uncovered(k, rects, &left); got != 50-22 {
		t.Fatalf("weighted %d, want 28", got)
	}
}

// A room standing beside the others shares their wall; one standing apart pays
// a ring of its own, the same wherever apart it stands.
func TestEnclosureCostChargesScatteredRooms(t *testing.T) {
	e := flatEmbedder(t, 200, []RoomGroup{{ID: 0, Area: 16}, {ID: 1, Area: 16}})
	anchor := Rectangle{X: 80, Z: 80, Width: 4, Height: 4}
	blocks := []Rectangle{anchor, {}}
	at := func(dx int32) float64 {
		return e.enclosureCost(Rectangle{X: 80 + dx, Z: 80, Width: 4, Height: 4}, e.enclosureRects(1, blocks))
	}
	beside, farther, far, fartherStill := at(5), at(15), at(70), at(80)
	if !(beside < farther && farther < far) {
		t.Fatalf("beside %v, 10 cells off %v, 65 off %v: scattering must cost perimeter", beside, farther, far)
	}
	if far != fartherStill {
		t.Fatalf("a room outside the shared ring pays one ring of its own wherever it stands: %v vs %v", far, fartherStill)
	}
	// The ring of an isolated room: grown by yard and thickness, less the room grown by the yard.
	const yard = 1 + perimeterGap
	side := int(4 + 2*yard)
	wall := (side+2*int(perimeterThick))*(side+2*int(perimeterThick)) - side*side
	foot := 6 * 6
	if want := float64(planWeights.Wall*wall + planWeights.Footprint*foot); far != want {
		t.Fatalf("isolated room costs %v, want %v", far, want)
	}
}

// Unpriced rooms have a flow-free link at most; a flowed room and a field are priced elsewhere.
func TestUnpricedMeansNoFlow(t *testing.T) {
	e := flatEmbedder(t, 100, []RoomGroup{
		{ID: 0, Area: 9},
		{ID: 1, Area: 9, Links: []GroupLink{{To: 2, Required: true}}},
		{ID: 2, Area: 9, Links: []GroupLink{{To: 1, Required: true}, {To: 3, Weight: 2}}},
		{ID: 3, Area: 9, Links: []GroupLink{{To: 2, Weight: 2}}},
	})
	for i, want := range []bool{true, true, false, false} {
		if got := e.unpriced(i); got != want {
			t.Errorf("group %d unpriced = %v, want %v", i, got, want)
		}
	}
}

// With no flow to price it, a room is placed by the cell terms and the wall it
// adds: the outskirts rooms stand in one enclosure with the rest, with no band
// keeping them off or out of reach.
func TestUnpricedRoomsClusterByWall(t *testing.T) {
	groups := outskirtsGroups(t)
	s := plusSurvey(300, 80)
	in := EmbedInput{Groups: groups, Zones: Zone(s), Survey: s, RoofDistance: testRoof}
	emb, err := Embed(in)
	if err != nil {
		t.Fatal(err)
	}
	checkEmbedding(t, in, emb)
	if len(emb.Blocks) != len(groups) {
		t.Fatalf("placed %d of %d: %+v", len(emb.Blocks), len(groups), emb.Decisions)
	}
	// Every block's ring meets the enclosure of another block: one wall for all.
	const yard = 1 + perimeterGap
	for _, a := range emb.Blocks {
		shared := false
		for _, b := range emb.Blocks {
			if a.Group != b.Group && clipRect(pad(a.Interior, yard+perimeterThick), pad(b.Interior, yard)).Width > 0 {
				shared = true
			}
		}
		if !shared {
			t.Errorf("group %d (%s) at %+v stands apart from every other room: its wall is a ring of its own", a.Group, groups[a.Group].Planned, a.Interior)
		}
	}
	for _, d := range emb.Decisions {
		if d.Verdict == EmbedUnsatisfied || d.Verdict == EmbedUnplaceable {
			t.Errorf("no band keeps a room off or out of reach, yet: %+v", d)
		}
	}
}

// A longer haul costs more and pulls the room closer: against a pull to a far
// side, a heavy flow keeps the room beside its partner and a light one lets it go.
func TestLongerHaulPullsTheRoomCloser(t *testing.T) {
	link := func(w float64) []RoomGroup {
		return []RoomGroup{
			{ID: 0, Area: 16, Links: []GroupLink{{To: 1, Weight: w}}},
			{ID: 1, Area: 16, Links: []GroupLink{{To: 0, Weight: w}}},
		}
	}
	if linkCost(GroupLink{Weight: 3}, 9) <= linkCost(GroupLink{Weight: 3}, 4) || linkCost(GroupLink{Weight: 6}, 9) <= linkCost(GroupLink{Weight: 3}, 9) {
		t.Fatal("a longer or heavier haul must cost more")
	}
	standoff := func(w float64) int {
		e := flatEmbedder(t, 160, link(w))
		// Cheaper ground toward larger x: the room is drawn away from its partner.
		e.soil = newPrefix(160, 160, func(c domain.Cell) int { return int(c.X) })
		blocks := []Rectangle{{X: 40, Z: 80, Width: 4, Height: 4}, {}}
		r, ok := e.placeAnywhere(1, blocks, false, nil)
		if !ok {
			t.Fatal("not placed")
		}
		return gapDist(blocks[0], r)
	}
	light, heavy := standoff(0.25), standoff(100)
	if heavy != 1 {
		t.Errorf("a heavy haul stands %d cells away, want beside", heavy)
	}
	if light <= heavy {
		t.Errorf("a light haul stands %d cells away, a heavy one %d: the heavier must be closer", light, heavy)
	}
}

// A flow that is not known stays unpriced: no table, no meals or no colonists
// read means no flow, never a guessed one, and a room with none has no link.
func TestUnknownFlowStaysUnpriced(t *testing.T) {
	table := &StationNode{ID: "Table/dine", Purpose: PurposeDine, Count: 1}
	build := func(colonists int, meals float64, nodes map[string]*StationNode) []RoomNode {
		b := &graphBuilder{in: StationGraphInput{Population: StationPopulation{Colonists: colonists}, Seed: SeedRooms{Shelter: true, Colonists: 4}}, nodes: nodes}
		rooms := SeedRoomNodes(b.in.Seed)
		b.shelterMeals(rooms, meals)
		return rooms
	}
	withTable := map[string]*StationNode{table.ID: table}
	rooms := build(8, 12, withTable)
	if f := rooms[0].Flows; len(f) != 1 || f[0].With != table.ID || f[0].Weight != 12*4.0/8 || f[0].Reason != ReasonFlowShelterMeal {
		t.Fatalf("shelter flow %+v: meals a day times the colony share it houses", f)
	}
	for name, got := range map[string][]RoomNode{
		"no table":        build(8, 12, nil),
		"no colonists":    build(0, 12, withTable),
		"no meals":        build(8, 0, withTable),
		"outskirts rooms": OutskirtsRoomNodes(),
	} {
		for _, r := range got {
			if len(r.Flows) != 0 {
				t.Errorf("%s: %s carries a flow %+v", name, r.Role, r.Flows)
			}
		}
	}
	// A room whose flows reach no group links nothing and so is unpriced.
	groups := []RoomGroup{{ID: 0, Area: 9}, {ID: 1, Area: 9}}
	flowRooms(groups, 1, []RoomNode{{ID: "shelter", Role: PlannedShelter, Flows: []RoomFlow{{With: "Table/dine", Weight: 5}}}},
		func(node string, add func(group int, share float64)) {})
	if len(groups[1].Links) != 0 {
		t.Fatalf("a flow to nothing linked %+v", groups[1].Links)
	}
}

// The embedding is the same at any thread count.
func TestEmbedDeterministicAtAnyThreadCount(t *testing.T) {
	groups := outskirtsGroups(t)
	s := plusSurvey(300, 80)
	in := EmbedInput{Groups: groups, Zones: Zone(s), Survey: s, RoofDistance: testRoof}
	prev := runtime.GOMAXPROCS(1)
	defer runtime.GOMAXPROCS(prev)
	want, err := Embed(in)
	if err != nil {
		t.Fatal(err)
	}
	for _, n := range []int{2, 8} {
		runtime.GOMAXPROCS(n)
		got, err := Embed(in)
		if err != nil {
			t.Fatal(err)
		}
		if !reflect.DeepEqual(want, got) {
			t.Fatalf("GOMAXPROCS=%d embeds differently from 1", n)
		}
	}
}

// The grid reads the same uncovered weight as the sweep, rectangles spilling off
// the map and k beyond the grid included.
func TestCoverGridMatchesTheSweep(t *testing.T) {
	const n = 40
	onMap := Rectangle{Width: n, Height: n}
	odd := newPrefix(n, n, func(c domain.Cell) int { return b2i((c.X*7+c.Z*3)%5 != 0) })
	rng := uint32(12345)
	next := func(m int32) int32 { rng = rng*1664525 + 1013904223; return int32(rng>>8) % m }
	for trial := 0; trial < 200; trial++ {
		var rs []Rectangle
		for i := int32(0); i < 1+next(30); i++ {
			rs = append(rs, Rectangle{X: next(n+8) - 4, Z: next(n+8) - 4, Width: 1 + next(9), Height: 1 + next(9)})
		}
		xr := newXRects(rs)
		var sc coverScratch
		for _, w := range []*prefix{nil, &odd} {
			g := newCoverGrid(onMap, xr, w)
			for q := 0; q < 20; q++ {
				k := Rectangle{X: next(n+8) - 4, Z: next(n+8) - 4, Width: next(12) - 1, Height: next(12) - 1}
				if w != nil {
					k = clipRect(k, onMap)
				}
				if want, got := sc.uncovered(k, xr, w), g.uncovered(k, w); want != got {
					t.Fatalf("trial %d k %v weighted %v: sweep %d, grid %d", trial, k, w != nil, want, got)
				}
			}
		}
	}
}
