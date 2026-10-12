package policy

import (
	"errors"
	"reflect"
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/slowtest"
)

// baseRoomGroups are the room groups of a colony whose stations the clustering
// keeps apart: a kitchen, a freezer (cold storeroom) beside it by a required
// link, dining, a workshop, storage and a bedroom per pawn. The test catalog's
// clustering merges the table and cold shelf into the kitchen, so these are
// built by hand to give the old plan's base rooms a group each.
func baseRoomGroups(pawns int) []RoomGroup {
	cat := clusterCatalog()
	mk := func(role RoomRole, purpose StationPurpose, def string, count int, req ...EnvRequirement) RoomGroup {
		size := cat.Stations[def].Shape.Size
		foot := count * int(size.X) * int(size.Z)
		return RoomGroup{Role: role, Requires: req, Footprint: foot, Clearance: count + 1, Area: foot + count + 1,
			Stations: []GroupStation{{Node: nodeID(def, purpose), Def: def, Purpose: purpose, Count: count}}}
	}
	groups := []RoomGroup{
		mk(RoomRoleKitchen, PurposeWork, "Stove", 1),
		mk(RoomRoleStoreroom, PurposeStoreCold, "ColdShelf", 1+pawns/10, EnvCold),
		mk(RoomRoleDiningRoom, PurposeDine, "Table", 1+pawns/4),
		mk(RoomRoleWorkshop, PurposeWork, "Bench", 1+pawns/10),
		mk(RoomRoleStoreroom, PurposeStore, "Shelf", 1+pawns/5),
	}
	for range pawns {
		groups = append(groups, mk(RoomRoleBedroom, PurposeSleep, "Bed", 1))
	}
	for i := range groups {
		groups[i].ID = i
	}
	link := func(a, b int, weight float64, required bool) {
		groups[a].Links = append(groups[a].Links, GroupLink{To: b, Weight: weight, Required: required})
		groups[b].Links = append(groups[b].Links, GroupLink{To: a, Weight: weight, Required: required})
	}
	link(0, 1, 12, true)
	link(0, 2, 10, false)
	link(3, 4, 8, false)
	link(2, 4, 2, false)
	for i := 5; i < len(groups); i++ {
		link(i, 2, 6/float64(pawns), false)
	}
	for i := range groups {
		groups[i].AddRequiredDoors()
	}
	return groups
}

// tileInput tiles groups of the colony: baseRoomGroups when base is set, else
// the test catalog's clustering of the same colony.
func tileInput(t testing.TB, s MapSurvey, pawns int, base bool) (TileInput, Embedding) {
	t.Helper()
	return tileInputCirculation(t, s, pawns, base, true)
}

// tileInputCirculation is tileInput with or without the circulation corridor
// that a base of many bedrooms gets (#2911).
func tileInputCirculation(t testing.TB, s MapSurvey, pawns int, base, circulation bool) (TileInput, Embedding) {
	t.Helper()
	in := embedInput(t, s, pawns)
	if base {
		in.Groups = baseRoomGroups(pawns)
		if circulation {
			in.Groups, _ = addCirculation(in.Groups, circulationNode(), testDepth(t))
		}
	}
	emb, err := Embed(in)
	if err != nil {
		t.Fatal(err)
	}
	return TileInput{Groups: in.Groups, Embedding: emb, Catalog: clusterCatalog(), Zones: in.Zones, Survey: s}, emb
}

func TestPlannedRoleOfGroupReadsContents(t *testing.T) {
	for _, tc := range []struct {
		name string
		g    RoomGroup
		want PlannedRole
		ok   bool
	}{
		{"storeroom", RoomGroup{Role: RoomRoleStoreroom}, PlannedStorage, true},
		{"cold need", RoomGroup{Role: RoomRoleStoreroom, Requires: []EnvRequirement{EnvCold}}, PlannedFreezer, true},
		{"cold shelf", RoomGroup{Role: RoomRoleStoreroom, Stations: []GroupStation{{Purpose: PurposeStoreCold}}}, PlannedFreezer, true},
		{"cooler", RoomGroup{Role: RoomRoleStoreroom, Stations: []GroupStation{{Purpose: PurposeCool}}}, PlannedFreezer, true},
		{"bed", RoomGroup{Role: RoomRoleBedroom}, PlannedBedroom, true},
		{"medical bed", RoomGroup{Role: RoomRoleHospital}, PlannedHospital, true},
		{"stove", RoomGroup{Role: RoomRoleKitchen}, PlannedKitchen, true},
		{"barracks has no planned room", RoomGroup{Role: RoomRoleBarracks}, "", false},
		{"generic room has none", RoomGroup{Role: RoomRoleRoom}, "", false},
	} {
		got, ok := PlannedRoleOfGroup(tc.g)
		if got != tc.want || ok != tc.ok {
			t.Errorf("%s: %q %v, want %q %v", tc.name, got, ok, tc.want, tc.ok)
		}
	}
}

func TestTileRefusesARoleNoPlannedRoomCarries(t *testing.T) {
	groups := []RoomGroup{{ID: 0, Role: RoomRoleBarracks}}
	_, err := TileEmbedding(TileInput{Groups: groups, Embedding: Embedding{Blocks: []EmbeddedBlock{{Group: 0, Interior: Rectangle{X: 10, Z: 10, Width: 4, Height: 4}}}}, Survey: plainScoreSurvey()})
	if !errors.Is(err, ErrTileRole) {
		t.Fatalf("err %v, want ErrTileRole", err)
	}
}

// checkTiled asserts the tiled plan's invariants: valid, routes, one room per
// placed group, rooms walled apart, furniture inside the rooms.
func checkTiled(t *testing.T, in TileInput, res TileResult) {
	t.Helper()
	p := res.Plan
	if !p.Valid() {
		t.Fatal("tiled plan is not Valid")
	}
	if _, err := CheckRoutes(p); err != nil {
		t.Fatalf("CheckRoutes: %v", err)
	}
	if len(p.Rooms) != len(in.Embedding.Blocks) {
		t.Fatalf("%d rooms for %d blocks", len(p.Rooms), len(in.Embedding.Blocks))
	}
	for _, b := range in.Embedding.Blocks {
		id, ok := res.Rooms[b.Group]
		if !ok {
			t.Fatalf("group %d has no room", b.Group)
		}
		var room PlannedRoom
		for _, r := range p.Rooms {
			if r.ID == id {
				room = r
			}
		}
		if room.Region.Bounds() != b.Interior {
			t.Errorf("group %d room %+v is not its block %+v", b.Group, room.Region.Bounds(), b.Interior)
		}
		if !inWall(b.Interior, room.Door) {
			t.Errorf("room %d door %v is not in its wall", id, room.Door)
		}
		for _, pc := range res.Pieces[id] {
			for _, c := range rectCells(pc.Rect) {
				if !room.Region.Contains(c) {
					t.Errorf("room %d piece %s leaves the room at %v", id, pc.Slot, c)
				}
			}
		}
		for _, g := range in.Groups[b.Group].Stations {
			if st, _ := in.Catalog.Station(g.Def); st.Access == AccessWall || slices.Contains(res.Unfit(), b.Group) {
				continue
			}
			n := 0
			for _, pc := range res.Pieces[id] {
				if pc.Def == g.Def {
					n++
				}
			}
			if n < g.Count {
				t.Errorf("room %d holds %d of the %d %s", id, n, g.Count, g.Def)
			}
		}
	}
	for _, l := range p.Links {
		if l.A >= l.B {
			t.Errorf("link %+v not ordered", l)
		}
	}
	for _, c := range p.Entrances {
		if !p.besideExteriorDoor(c) {
			t.Errorf("entrance %v is beside no exterior door", c)
		}
	}
}

func TestTileBuildsAValidPlanDeterministically(t *testing.T) {
	in, _ := tileInput(t, plainScoreSurvey(), 6, true)
	res, err := TileEmbedding(in)
	if err != nil {
		t.Fatal(err)
	}
	checkTiled(t, in, res)
	if unfit := res.Unfit(); len(unfit) > 0 {
		t.Errorf("groups %v are not furnished", unfit)
	}
	roles := map[PlannedRole]bool{}
	for _, r := range res.Plan.Rooms {
		roles[r.Role] = true
	}
	for _, role := range coreBaseRooms {
		if !roles[role] {
			t.Errorf("no %s room: %v", role, roles)
		}
	}
	again, err := TileEmbedding(in)
	if err != nil || !reflect.DeepEqual(res.Plan, again.Plan) || !reflect.DeepEqual(res.Pieces, again.Pieces) {
		t.Fatalf("tiling is not deterministic: %v", err)
	}
	if sc := Score(res.Plan, in.Survey); !sc.Passes() {
		t.Fatalf("hard tier: missing=%v routes=%q rich=%d", sc.Missing, sc.RoutesErr, sc.RichCells)
	}
}

// A required link between sharing rooms is a door in their shared wall.
func TestTileOpensRequiredLinksInTheSharedWall(t *testing.T) {
	in, emb := tileInput(t, plainScoreSurvey(), 6, true)
	res, err := TileEmbedding(in)
	if err != nil {
		t.Fatal(err)
	}
	for i, g := range in.Groups {
		for _, l := range g.Links {
			if !l.Required || l.To < i {
				continue
			}
			a, _ := emb.Block(i)
			b, _ := emb.Block(l.To)
			if gapDist(a, b) != 1 {
				continue
			}
			idA, idB := res.Rooms[i], res.Rooms[l.To]
			if idA > idB {
				idA, idB = idB, idA
			}
			if !slices.ContainsFunc(res.Plan.Links, func(k RoomLink) bool { return k.A == idA && k.B == idB }) {
				t.Errorf("required link %d-%d has no door", i, l.To)
			}
		}
	}
}

// The door graph is read by the route walk and the shell doors.
func TestRoomLinksAreWalkedAndFramed(t *testing.T) {
	a := PlannedRoom{ID: 1, Role: PlannedKitchen, Region: RectRegion(Rectangle{X: 10, Z: 10, Width: 4, Height: 4}), Door: domain.Cell{X: 9, Z: 11}, DoorRot: domain.West}
	b := PlannedRoom{ID: 2, Role: PlannedFreezer, Region: RectRegion(Rectangle{X: 15, Z: 10, Width: 4, Height: 4}), Door: domain.Cell{X: 19, Z: 11}, DoorRot: domain.East}
	p := LayoutPlan{Rooms: []PlannedRoom{a, b}, Entrances: []domain.Cell{{X: 8, Z: 11}, {X: 20, Z: 11}}, Links: []RoomLink{{A: 1, B: 2, Cell: domain.Cell{X: 14, Z: 11}}}, NextRoomID: 3}
	if !p.Valid() {
		t.Fatal("not valid")
	}
	if _, err := CheckRoutes(p); err != nil {
		t.Fatal(err)
	}
	if !slices.Contains(p.ShellDoors(a), domain.Cell{X: 14, Z: 11}) || !slices.Contains(p.ShellDoors(b), domain.Cell{X: 14, Z: 11}) {
		t.Error("the link door is not a shell door of both rooms")
	}
	p.Links[0].Cell = domain.Cell{X: 14, Z: 20}
	if p.Valid() {
		t.Error("a link door off the shared wall is valid")
	}
	p.Links[0] = RoomLink{A: 2, B: 1, Cell: domain.Cell{X: 14, Z: 11}}
	if p.Valid() {
		t.Error("an unordered link is valid")
	}
}

// An entrance off every hallway is valid beside an exterior door, and not
// elsewhere.
func TestEntranceBesideExteriorDoor(t *testing.T) {
	r := PlannedRoom{ID: 1, Role: PlannedKitchen, Region: RectRegion(Rectangle{X: 10, Z: 10, Width: 4, Height: 4}), Door: domain.Cell{X: 9, Z: 11}, DoorRot: domain.West}
	p := LayoutPlan{Rooms: []PlannedRoom{r}, Entrances: []domain.Cell{{X: 8, Z: 11}}, NextRoomID: 2}
	if !p.Valid() {
		t.Fatal("entrance beside the door is invalid")
	}
	p.Entrances = []domain.Cell{{X: 30, Z: 30}}
	if p.Valid() {
		t.Fatal("entrance in the open is valid")
	}
}

// The test catalog's clustering merges the table and the cold shelf into the
// kitchen group and the shelf into the workshop's: its tiled plan is valid and
// routes, but has no freezer, dining or storage room to miss.
func TestTileClusteredGroupsAreValid(t *testing.T) {
	in, _ := tileInput(t, plainScoreSurvey(), 6, false)
	res, err := TileEmbedding(in)
	if err != nil {
		t.Fatal(err)
	}
	checkTiled(t, in, res)
}

// Replay fixtures: the tiled plan against the old generator's. The hard tier is
// asserted unless the fixture's map cannot pass it; the scores are printed.
func TestTileReplayFixtures(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	for _, f := range replayFixtures {
		t.Run(f.name, func(t *testing.T) {
			s := f.survey(t)
			in, emb := tileInput(t, s, f.pawns, true)
			res, err := TileEmbedding(in)
			old := Score(SiteCore(LayoutPlan{Zones: in.Zones}, s, f.pawns, 1, TechTierCamp), s)
			if err != nil {
				if f.mayFail {
					t.Skipf("%s cannot be tiled: %v", f.name, err)
				}
				t.Fatal(err)
			}
			checkTiled(t, in, res)
			sc := Score(res.Plan, s)
			t.Logf("%-16s tile: rooms=%d links=%d entrances=%d unfit=%v %s", f.name, len(res.Plan.Rooms), len(res.Plan.Links), len(res.Plan.Entrances), res.Unfit(), sc)
			t.Logf("%-16s old:  rooms=%d %s", f.name, len(SiteCore(LayoutPlan{Zones: in.Zones}, s, f.pawns, 1, TechTierCamp).AllRooms()), old)
			if len(emb.Unplaceable()) == 0 && !sc.Passes() && !f.mayFail {
				t.Fatalf("hard tier: missing=%v routes=%q rich=%d", sc.Missing, sc.RoutesErr, sc.RichCells)
			}
			if len(emb.Unplaceable()) > 0 && len(sc.Missing) < len(emb.Unplaceable()) {
				t.Errorf("%d groups unplaced but missing=%v", len(emb.Unplaceable()), sc.Missing)
			}
			// A hub that carries the doors of sealed neighbours packed in around
			// it holds more thresholds than its area counts; only hallways
			// (#2879) take those doors off the hub, so such a plan is exempt.
			sealed := slices.ContainsFunc(res.Decisions, func(d TileDecision) bool { return d.Reason == ReasonTileSealedRoom && d.Verdict == TilePlace }) &&
				!slices.ContainsFunc(res.Plan.Rooms, func(r PlannedRoom) bool { return r.Role == PlannedCirculation }) // circulation (#2911) carries those doors
			if old.Passes() && !sealed && len(res.Unfit()) > 0 {
				t.Errorf("the old generator furnishes this map but groups %v are unfit", res.Unfit())
			}
		})
	}
}

// The hard tier counts a room per owed group: two bedroom groups need two
// bedrooms, so one standing bedroom leaves the second group's room missing.
func TestMissingRoomsIsPerGroup(t *testing.T) {
	rooms := []PlannedRoom{{Role: PlannedBedroom}, {Role: PlannedKitchen}}
	owed := []PlannedRole{PlannedKitchen, PlannedBedroom, PlannedBedroom}
	if got := missingRooms(rooms, owed); !slices.Equal(got, []PlannedRole{PlannedBedroom}) {
		t.Errorf("missing %v, want one bedroom", got)
	}
	if got := missingRooms(rooms, nil); len(got) != len(coreBaseRooms)-1 {
		t.Errorf("no owed groups keeps the base set once each: missing %v", got)
	}
}

// A group the embedding could not place is still owed a room.
func TestTileOwesARoomPerGroup(t *testing.T) {
	in, _ := tileInput(t, plainScoreSurvey(), 3, true)
	in.Embedding.Blocks = in.Embedding.Blocks[:len(in.Embedding.Blocks)-1]
	res, err := TileEmbedding(in)
	if err != nil {
		t.Fatal(err)
	}
	if len(res.Plan.Owed) != len(in.Groups) {
		t.Fatalf("owed %d rooms for %d groups", len(res.Plan.Owed), len(in.Groups))
	}
	if sc := Score(res.Plan, in.Survey); !slices.Contains(sc.Missing, PlannedBedroom) {
		t.Errorf("the dropped bedroom is not missing: %v", sc.Missing)
	}
}

// A required link adds a door threshold to the group's floor; a plain one adds
// none, and the tiler opens that one only while the room still furnishes.
func TestRequiredLinksAddDoorClearance(t *testing.T) {
	g := RoomGroup{Clearance: 2, Area: 5, Links: []GroupLink{{To: 1, Required: true}, {To: 2}}}
	g.AddRequiredDoors()
	if g.Clearance != 3 || g.Area != 6 {
		t.Errorf("clearance %d area %d, want 3 and 6", g.Clearance, g.Area)
	}
}
