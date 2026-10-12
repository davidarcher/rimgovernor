package policy

import (
	"reflect"
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// builtReplanAdvice derives the fixture's plan, stands every room of it and
// tiles the chain over it as a replan does (the plan as the saved plan, its
// rooms Built): the plan, the facts and the tiled advice.
func builtReplanAdvice(t *testing.T, f replayFixture) (LayoutPlan, StationFacts, StationAdvice) {
	t.Helper()
	s := f.survey(t)
	facts := replayStationFacts(f, s)
	// The departments ask for a training range, a sparring ring, an armory and
	// a wardrobe, and a toddler owes a playroom.
	facts.Demand = RoomDemand{Known: true, Ranges: 6, Rings: 6, Armory: true, Wardrobe: true}
	facts.Child = []ChildRoomShape{{Module: PlannedPlayroom, Pieces: []PieceCount{{Size: domain.Cell{X: 2, Z: 1}, Count: 2}}}}
	fact, _, err := DeriveLayoutPlan(s, facts, nil, 0)
	plan, ok := fact.Value()
	if err != nil || !ok {
		t.Fatalf("derive: %v %v", ok, err)
	}
	facts.Plan, facts.Built = plan, map[int32]bool{}
	for _, r := range plan.AllRooms() {
		facts.Built[r.ID] = true
	}
	advice, err := TileStations(ReplanTileFacts(facts, s, nil))
	if err != nil {
		t.Fatalf("tile: %v", err)
	}
	return plan, facts, advice
}

// Every replay fixture's built colony re-tiles to the rooms it stands: each
// built room the chain pins is kept where it stands, and the standing rooms
// with no node are listed with why, never silently dropped.
func TestDiffReplanTileKeepsBuiltRoomsAndListsTheUnmatched(t *testing.T) {
	for _, f := range replayFixtures {
		if f.pawns > 10 {
			continue // the long tier's fixture; the parity gate covers it
		}
		t.Run(f.name, func(t *testing.T) {
			plan, facts, advice := builtReplanAdvice(t, f)
			diff := DiffReplanTile(plan, facts.Built, advice)
			listed := map[int32]UnmatchedBuilt{}
			for _, u := range diff.Unmatched {
				listed[u.Room] = u
			}
			byRoom := map[int32]TileRoomDiff{}
			for _, d := range diff.Rooms {
				if d.Room != 0 {
					byRoom[d.Room] = d
				}
			}
			for _, r := range plan.AllRooms() {
				d, ok := byRoom[r.ID]
				if r.attached() {
					if ok {
						t.Errorf("attached %s %d is diffed", r.Role, r.ID)
					}
					continue
				}
				if !ok || !d.Built {
					t.Fatalf("%s %d: row %+v", r.Role, r.ID, d)
				}
				// Kept where it stands, or listed with why the chain could not pin it.
				if _, unmatched := listed[r.ID]; unmatched == (d.Reason == ReasonDiffKept) {
					t.Errorf("%s %d: row %+v, listed %v", r.Role, r.ID, d, unmatched)
				}
				if d.Reason == ReasonDiffKept && d.Verdict != TileDiffSame {
					t.Errorf("%s %d is kept but %s", r.Role, r.ID, d.Verdict)
				}
			}
			for _, b := range BuiltRegions(plan, facts.Built) {
				if u, ok := listed[b.Room]; ok && u.Reason != ReasonDiffNoNode {
					t.Errorf("room %d has a native role but is listed %+v", b.Room, u)
				}
			}
			if counted := diff.Count(TileDiffSame) + diff.Count(TileDiffMoved) + diff.Count(TileDiffNew) + diff.Count(TileDiffDropped); counted != len(diff.Rooms) {
				t.Errorf("verdicts count %d of %d rows", counted, len(diff.Rooms))
			}
			if diff.Count(TileDiffSame) == 0 {
				t.Errorf("no built room is kept: %+v", diff.Rooms)
			}
			// Every room node's room is pinned: circulation, the outskirts rooms
			// and the shelter stand as the chain's nodes, outdoor or with no
			// native role as they may be (J2 of #2965), none a rebuild.
			for _, r := range plan.AllRooms() {
				if !roomNodeRoles[r.Role] {
					continue
				}
				if d := byRoom[r.ID]; d.Verdict != TileDiffSame || d.Reason != ReasonDiffKept {
					t.Errorf("%s %d is not kept as it stands: %+v", r.Role, r.ID, d)
				}
			}
			if f.name == "baseline" {
				for _, role := range []PlannedRole{PlannedTrainingRange, PlannedSparringRing, PlannedArmory, PlannedWardrobe, PlannedPlayroom} {
					if len(plan.roomsOf(role)) == 0 {
						t.Errorf("the fixture stands no %s", role)
					}
				}
			}
			for _, r := range plan.RangeRooms() {
				for _, tr := range advice.Tiled.Plan.RangeRooms() {
					if tr.ID == r.ID && tr.Facing != r.Facing {
						t.Errorf("range %d faces %v, tiled %v", r.ID, r.Facing, tr.Facing)
					}
				}
			}
			if len(diff.Unmatched) != 0 {
				t.Errorf("unmatched %+v, want none", diff.Unmatched)
			}
			if again := DiffReplanTile(plan, facts.Built, advice); !reflect.DeepEqual(again, diff) {
				t.Error("the diff is not deterministic")
			}
		})
	}
}

// With nothing standing the old plan's rooms are sited again by role: the
// tiled plan of a plan the tiler derived has the same rooms, none built.
func TestDiffReplanTileMatchesUnbuiltRoomsByRole(t *testing.T) {
	f := replayFixtures[0]
	plan, facts, _ := builtReplanAdvice(t, f)
	facts.Built = map[int32]bool{}
	advice, err := TileStations(ReplanTileFacts(facts, f.survey(t), nil))
	if err != nil {
		t.Fatal(err)
	}
	diff := DiffReplanTile(plan, facts.Built, advice)
	if len(diff.Unmatched) != 0 {
		t.Errorf("unmatched with nothing built: %+v", diff.Unmatched)
	}
	for _, d := range diff.Rooms {
		if d.Verdict == TileDiffSame || d.Verdict == TileDiffMoved {
			if d.Reason != ReasonDiffSited || d.Tiled == 0 || d.Built {
				t.Errorf("unbuilt room matched with %+v", d)
			}
		}
	}
	if diff.Count(TileDiffSame)+diff.Count(TileDiffMoved) == 0 {
		t.Errorf("no unbuilt room is matched: %+v", diff.Rooms)
	}
}

// Bedrooms and suites are nodes of the tiled replan (#3004, J3 of #2965): the
// sleepers' node and one node per suite wish, a standing bedroom or suite pinned
// as the block of its node. A raised suite target shows as the floor its group
// lacks (the embedding does not grow a standing room), a new wish as a new suite
// that takes one bed off the ordinary sleepers, more colonists as new bedrooms,
// and fewer as standing bedrooms with no node (retirement-eligible).
func TestTiledReplanHousesColonistsOnBedroomAndSuiteNodes(t *testing.T) {
	f := replayFixtures[0]
	s := f.survey(t)
	base := colonyStationFacts(8, s)
	base.Suites = []SuiteWish{{Target: 60}, {Target: 90}}
	fact, _, err := DeriveLayoutPlan(s, base, nil, 0)
	plan, ok := fact.Value()
	if err != nil || !ok {
		t.Fatalf("derive: %v %v", ok, err)
	}
	built := map[int32]bool{}
	var kept []SuiteWish
	var suites []int32
	for _, r := range plan.AllRooms() {
		built[r.ID] = true
		if r.Role == PlannedSuite {
			kept = append(kept, SuiteWish{Kept: r.Region.Area()})
			suites = append(suites, r.ID)
		}
	}
	if len(suites) != 2 {
		t.Fatalf("derived suites %v", suites)
	}
	// diff tiles the standing plan for colonists colonists and wishes, and
	// counts the bedroom and suite rows by verdict.
	var last StationAdvice
	diff := func(colonists int, wishes []SuiteWish) (ReplanTileDiff, map[string]int) {
		facts := colonyStationFacts(colonists, s)
		facts.Plan, facts.Built, facts.Suites = plan, built, wishes
		advice, err := TileStations(ReplanTileFacts(facts, s, nil))
		if err != nil {
			t.Fatalf("tile: %v", err)
		}
		last = advice
		d := DiffReplanTile(plan, built, advice)
		counts := map[string]int{}
		for _, r := range d.Rooms {
			if r.Role == PlannedBedroom || r.Role == PlannedSuite {
				counts[string(r.Role)+"/"+r.Verdict]++
			}
		}
		return d, counts
	}
	row := func(d ReplanTileDiff, id int32) TileRoomDiff {
		for _, r := range d.Rooms {
			if r.Room == id {
				return r
			}
		}
		t.Fatalf("no row for room %d", id)
		return TileRoomDiff{}
	}
	t.Run("standing", func(t *testing.T) {
		d, counts := diff(8, kept)
		if counts["bedroom/same"] != 7 || counts["suite/same"] != 2 || len(counts) != 2 || len(d.Unmatched) != 0 {
			t.Errorf("counts %v, unmatched %+v", counts, d.Unmatched)
		}
		// A standing room is short of nothing: a suite as built already holds the
		// door cells its group counts (#3020).
		for _, r := range d.Rooms {
			if r.Short != 0 {
				t.Errorf("room %d (%s) is short %d", r.Room, r.Role, r.Short)
			}
		}
	})
	t.Run("raised target", func(t *testing.T) {
		d, counts := diff(8, []SuiteWish{{Kept: kept[0].Kept, Target: 400}, kept[1]})
		got := row(d, suites[0])
		// The outgrown suite is replaced by a larger one beside it (#3020); the
		// other keeps its room.
		if got.Verdict != TileDiffMoved || got.Reason != ReasonDiffSuccessor || got.Short <= 1 || row(d, suites[1]).Short != 0 || counts["suite/same"] != 1 || counts["suite/moved"] != 1 {
			t.Errorf("raised suite row %+v, counts %v", got, counts)
		}
		var succ *PlannedRoom
		for _, r := range last.Tiled.Plan.AllRooms() {
			if r.Replaces == suites[0] {
				succ = &r
			}
		}
		old := plan.AllRooms()[slices.IndexFunc(plan.AllRooms(), func(r PlannedRoom) bool { return r.ID == suites[0] })].Region
		if succ == nil || succ.Region.Area() < old.Area()+got.Short || collides(succ.Region.Bounds(), old.Bounds()) {
			t.Errorf("successor %+v of the suite at %v short %d", succ, old.Bounds(), got.Short)
		}
	})
	t.Run("new wish", func(t *testing.T) {
		d, counts := diff(8, append(slices.Clone(kept), SuiteWish{Target: 70}))
		if counts["suite/same"] != 1 || counts["suite/moved"] != 1 || counts["suite/new"] != 1 || counts["bedroom/dropped"] != 1 || counts["bedroom/same"] != 6 {
			t.Errorf("counts %v", counts)
		}
		if len(d.Unmatched) != 1 || d.Unmatched[0].Reason != ReasonDiffNoNode {
			t.Errorf("unmatched %+v", d.Unmatched)
		}
	})
	t.Run("more colonists", func(t *testing.T) {
		_, counts := diff(14, kept)
		if counts["bedroom/same"] != 7 || counts["suite/same"] != 2 || counts["bedroom/new"] != 6 {
			t.Errorf("counts %v", counts)
		}
	})
	t.Run("fewer colonists", func(t *testing.T) {
		d, counts := diff(4, kept)
		if counts["bedroom/same"] != 3 || counts["suite/same"] != 2 || counts["bedroom/dropped"] != 4 || len(d.Unmatched) != 4 {
			t.Errorf("counts %v, unmatched %+v", counts, d.Unmatched)
		}
	})
}

// The tiled replan pins only what is in flight, slept in, in use or a rebuild's
// pair (#3004); a plan with the census unread keeps every room.
func TestTilePinned(t *testing.T) {
	plan := LayoutPlan{Rooms: []PlannedRoom{{ID: 1, Role: PlannedBedroom}, {ID: 2, Role: PlannedBedroom}, {ID: 3, Role: PlannedBedroom}, {ID: 4, Role: PlannedBedroom}, {ID: 5, Role: PlannedBedroom, Replaces: 4}, {ID: 6, Role: PlannedKitchen}}}
	if got := TilePinned(plan, RoomGrowth{}); len(got) != 6 {
		t.Errorf("census unread pins %v", got)
	}
	growth := RoomGrowth{Fixed: map[int32]bool{1: true, 6: true}, InFlight: map[int32]bool{2: true}, Slept: map[int32]bool{3: true}}
	want := map[int32]bool{2: true, 3: true, 4: true, 5: true}
	if got := TilePinned(plan, growth); !reflect.DeepEqual(got, want) {
		t.Errorf("pins %v, want %v", got, want)
	}
}

// The graph holds a node for every training room and graveyard the plan stands
// or the demand asks for, so each standing one is pinned (#3003): the larger of
// the two, a graveyard beyond the first with an id of its own, matched by
// planned role to the standing graveyards in turn.
func TestRoomNodesFollowStandingAndDemandedRooms(t *testing.T) {
	f := StationFacts{Demand: RoomDemand{Ranges: 20, Rings: 2}}
	f.Plan = LayoutPlan{Rooms: []PlannedRoom{
		{ID: 1, Role: PlannedTrainingRange, Outdoor: true, Facing: domain.North, Region: RectRegion(Rectangle{X: 0, Z: 0, Width: 5, Height: RangeDistance + 1})},
		{ID: 2, Role: PlannedSparringRing, Outdoor: true, Markers: 9, Region: RectRegion(Rectangle{X: 20, Z: 0, Width: RingSide, Height: RingSide})},
	}}
	if stands, markers := f.trainingAsk(); stands != RangeMaxStands || markers != 9 {
		t.Errorf("training ask %d stands, %d markers", stands, markers)
	}
	nodes := GraveyardRoomNodes(3)
	if len(nodes) != 2 || nodes[0].ID == nodes[1].ID || nodes[0].Role != PlannedGraveyard || GraveyardRoomNodes(1) != nil {
		t.Errorf("graveyard nodes %+v", nodes)
	}
	graveyard := func(id int32, x int32) PlannedRoom {
		return PlannedRoom{ID: id, Role: PlannedGraveyard, Outdoor: true, Region: RectRegion(Rectangle{X: x, Z: 0, Width: GraveyardW, Height: GraveyardH})}
	}
	plan := LayoutPlan{Rooms: []PlannedRoom{graveyard(1, 0), graveyard(2, 30)}}
	built := BuiltRegions(plan, map[int32]bool{1: true, 2: true})
	groups := []RoomGroup{{ID: 0, Planned: PlannedGraveyard, Area: 117}, {ID: 1, Planned: PlannedGraveyard, Area: 117}}
	for _, m := range MatchGroupRooms(groups, built) {
		if len(m.Rooms) != 1 {
			t.Errorf("group %d holds %v", m.Group, m.Rooms)
		}
	}
}

// The gear and add-on rooms are nodes while the demand or a shape asks for
// them or the plan holds one, a standing one at no less than the floor it has.
func TestGearAndChildRoomNodes(t *testing.T) {
	held := LayoutPlan{Rooms: []PlannedRoom{
		{ID: 1, Role: PlannedArmory, Region: RectRegion(Rectangle{X: 0, Z: 0, Width: 7, Height: 5})},
		{ID: 2, Role: PlannedWorship, Region: RectRegion(Rectangle{X: 10, Z: 0, Width: 8, Height: 6})},
	}}
	if nodes := GearRoomNodes(held, RoomDemand{Wardrobe: true}); len(nodes) != 2 || nodes[0].Role != PlannedArmory || nodes[1].Role != PlannedWardrobe {
		t.Errorf("gear nodes %+v", nodes)
	}
	if nodes := GearRoomNodes(LayoutPlan{}, RoomDemand{}); len(nodes) != 0 {
		t.Errorf("gear nodes with no demand %+v", nodes)
	}
	shape := ChildRoomShape{Module: PlannedPlayroom, Pieces: []PieceCount{{Size: domain.Cell{X: 1, Z: 1}, Count: 2}}}
	nodes := ChildRoomNodes(held, []ChildRoomShape{shape})
	if len(nodes) != 2 || nodes[0].Role != PlannedPlayroom || nodes[0].Area <= 0 || nodes[1].Role != PlannedWorship || nodes[1].Area != 48 || nodes[1].Width != 6 {
		t.Errorf("child nodes %+v", nodes)
	}
	if nodes := ChildRoomNodes(LayoutPlan{}, nil); len(nodes) != 0 {
		t.Errorf("child nodes with no need %+v", nodes)
	}
}

// A standing room the old replan retired but the chain keeps is a new room in
// the diff, a superseded standing room is dropped without being listed as
// unmatched, and one the chain cannot pin is listed and, when the tiler builds
// a room of its role, resited.
func TestDiffReplanTileReadsClaimsAndSupersededRooms(t *testing.T) {
	old := LayoutPlan{Rooms: []PlannedRoom{
		{ID: 1, Role: PlannedKitchen, Region: RectRegion(Rectangle{X: 0, Z: 0, Width: 4, Height: 4})},
		{ID: 2, Role: PlannedStorage, Region: RectRegion(Rectangle{X: 10, Z: 0, Width: 4, Height: 4})},
		{ID: 3, Role: PlannedStorage, Replaces: 2, Region: RectRegion(Rectangle{X: 20, Z: 0, Width: 6, Height: 4})},
		{ID: 4, Role: PlannedYard, Outdoor: true, Region: RectRegion(Rectangle{X: 30, Z: 0, Width: 6, Height: 6})},
		{ID: 5, Role: PlannedBattery, Region: RectRegion(Rectangle{X: 40, Z: 0, Width: 6, Height: 6})},
		{ID: 6, Role: PlannedMorgue, Region: RectRegion(Rectangle{X: 60, Z: 0, Width: 4, Height: 4})},
	}}
	advice := StationAdvice{
		Embedding: Embedding{Blocks: []EmbeddedBlock{
			{Group: 0, Interior: Rectangle{X: 1, Z: 0, Width: 4, Height: 4}, Built: 1},
			{Group: 1, Interior: Rectangle{X: 20, Z: 0, Width: 6, Height: 4}, Built: 3},
			{Group: 2, Interior: Rectangle{X: 50, Z: 0, Width: 4, Height: 4}, Built: 9},
			{Group: 3, Interior: Rectangle{X: 70, Z: 0, Width: 4, Height: 4}},
		}},
		Tiled: &TileResult{Rooms: map[int]int32{0: 1, 1: 3, 2: 9, 3: 10}, Plan: LayoutPlan{Rooms: []PlannedRoom{
			{ID: 1, Role: PlannedKitchen, Region: RectRegion(Rectangle{X: 1, Z: 0, Width: 4, Height: 4})},
			{ID: 3, Role: PlannedStorage, Region: RectRegion(Rectangle{X: 20, Z: 0, Width: 6, Height: 4})},
			{ID: 9, Role: PlannedBedroom, Region: RectRegion(Rectangle{X: 50, Z: 0, Width: 4, Height: 4})},
			{ID: 10, Role: PlannedMorgue, Region: RectRegion(Rectangle{X: 70, Z: 0, Width: 4, Height: 4})},
		}}},
	}
	built := map[int32]bool{1: true, 2: true, 3: true, 4: true, 5: true, 6: true}
	diff := DiffReplanTile(old, built, advice)
	type row struct {
		verdict, reason string
		tiled           int32
	}
	want := map[int32]row{
		1: {TileDiffMoved, ReasonDiffKept, 1}, 2: {TileDiffDropped, ReasonDiffSuperseded, 0}, 3: {TileDiffSame, ReasonDiffKept, 3},
		4: {TileDiffDropped, ReasonDiffOutdoor, 0}, 5: {TileDiffDropped, ReasonDiffNoNativeRole, 0}, 6: {TileDiffMoved, ReasonDiffUnpinned, 10},
		0: {TileDiffNew, ReasonDiffRetired, 9},
	}
	if len(diff.Rooms) != len(want) {
		t.Fatalf("rows %+v", diff.Rooms)
	}
	for _, d := range diff.Rooms {
		if got := (row{d.Verdict, d.Reason, d.Tiled}); got != want[d.Room] {
			t.Errorf("room %d is %+v, want %+v", d.Room, got, want[d.Room])
		}
	}
	wantUnmatched := []UnmatchedBuilt{{Room: 4, Role: PlannedYard, Reason: ReasonDiffOutdoor}, {Room: 5, Role: PlannedBattery, Reason: ReasonDiffNoNativeRole}, {Room: 6, Role: PlannedMorgue, Reason: ReasonDiffNoNode}}
	if !reflect.DeepEqual(diff.Unmatched, wantUnmatched) {
		t.Errorf("unmatched %+v, want %+v", diff.Unmatched, wantUnmatched)
	}
}

// A pinned standing room that no group carries (a surplus bedroom still slept
// in) stays an obstacle and a room of the tiled plan until it retires (#3019).
func TestPinnedRoomWithNoGroupStaysInTheTiledPlan(t *testing.T) {
	f := replayFixtures[0]
	s := f.survey(t)
	fact, _, err := DeriveLayoutPlan(s, colonyStationFacts(8, s), nil, 0)
	plan, ok := fact.Value()
	if err != nil || !ok {
		t.Fatalf("derive: %v %v", ok, err)
	}
	built := map[int32]bool{}
	standing := map[int32]PlannedRoom{}
	for _, r := range plan.AllRooms() {
		built[r.ID], standing[r.ID] = true, r
	}
	tile := func(pinned map[int32]bool) (StationAdvice, ReplanTileDiff) {
		facts := colonyStationFacts(4, s) // four colonists need fewer bedrooms than stand
		facts.Plan, facts.Built, facts.Pinned = plan, built, pinned
		advice, err := TileStations(ReplanTileFacts(facts, s, nil))
		if err != nil {
			t.Fatalf("tile: %v", err)
		}
		return advice, DiffReplanTile(plan, built, advice)
	}
	_, free := tile(nil)
	pinned := map[int32]bool{}
	for _, u := range free.Unmatched {
		if u.Role == PlannedBedroom {
			pinned[u.Room] = true
		}
	}
	if len(pinned) == 0 {
		t.Fatal("no surplus bedroom to pin")
	}
	advice, d := tile(pinned)
	if len(advice.Embedding.Held) != len(pinned) {
		t.Fatalf("held %+v, want the surplus bedrooms %v", advice.Embedding.Held, pinned)
	}
	all := advice.Tiled.Plan.AllRooms()
	for id := range pinned {
		i := slices.IndexFunc(all, func(r PlannedRoom) bool { return r.ID == id })
		if i < 0 {
			t.Fatalf("pinned room %d is not in the tiled plan", id)
		}
		at := standing[id].Region.Bounds()
		if all[i].Region.Bounds() != at {
			t.Errorf("room %d tiled at %v, stands at %v", id, all[i].Region.Bounds(), at)
		}
		for _, r := range all {
			if r.ID != id && !r.attached() && collides(r.Region.Bounds(), at) {
				t.Errorf("room %d (%s) of the tiled plan touches pinned room %d", r.ID, r.Role, id)
			}
		}
	}
	for _, u := range d.Unmatched {
		if pinned[u.Room] {
			t.Errorf("pinned room %d is still unmatched: %+v", u.Room, u)
		}
	}
}
