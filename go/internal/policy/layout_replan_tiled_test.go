package policy

import (
	"reflect"
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
