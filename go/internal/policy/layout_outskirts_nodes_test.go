package policy

import (
	"slices"
	"testing"
)

// outskirtsGroups clusters a small colony together with the five outskirts room
// nodes.
func outskirtsGroups(t testing.TB) []RoomGroup {
	t.Helper()
	stove := node("Stove", PurposeWork, 1, workBand)
	table := node("Table", PurposeDine, 2)
	bed := node("Bed", PurposeSleep, 3)
	shelf := node("Shelf", PurposeStore, 2)
	g := StationGraph{
		Nodes: []StationNode{bed, stove, table, shelf},
		Edges: []StationEdge{edge(stove, table, 10, false), edge(bed, table, 6, false), edge(table, shelf, 2, false)},
		Rooms: OutskirtsRoomNodes(),
	}
	got, err := ClusterStations(ClusterInput{Graph: g, Catalog: clusterCatalog(), RoofDistance: testRoof})
	if err != nil {
		t.Fatal(err)
	}
	return got.Groups
}

func groupOf(groups []RoomGroup, role PlannedRole) RoomGroup {
	for _, g := range groups {
		if g.Planned == role {
			return g
		}
	}
	return RoomGroup{ID: -1}
}

// The graph carries the five outskirts rooms as station-less nodes, none banded or flowed
// (no corpse or waste volume model prices them).
func TestGraphCarriesOutskirtsRoomNodes(t *testing.T) {
	g, err := BuildStationGraph(graphTestInput())
	if err != nil {
		t.Fatal(err)
	}
	var roles []PlannedRole
	for _, r := range g.Rooms {
		if r.Role == PlannedCirculation {
			continue
		}
		roles = append(roles, r.Role)
		if len(r.Flows) != 0 || r.Joins || r.Reason != ReasonOutskirtsRoom || r.Area <= 0 {
			t.Errorf("room node %+v", r)
		}
		if r.Cold != (r.Role == PlannedMorgue) {
			t.Errorf("%s cold %v", r.Role, r.Cold)
		}
	}
	want := []PlannedRole{PlannedTomb, PlannedMorgue, PlannedGraveyard, PlannedWasteYard, PlannedIncinerator}
	if !slices.Equal(roles, want) {
		t.Fatalf("outskirts nodes %v, want %v", roles, want)
	}
}

// Clustering makes a group per room node: the floor it asks for, the cooler of a
// cold group for the morgue, a required link from the incinerator to the waste
// yard.
func TestClusterMakesOutskirtsGroups(t *testing.T) {
	groups := outskirtsGroups(t)
	tomb, morgue := groupOf(groups, PlannedTomb), groupOf(groups, PlannedMorgue)
	yard, inc := groupOf(groups, PlannedWasteYard), groupOf(groups, PlannedIncinerator)
	if groupOf(groups, PlannedGraveyard).ID < 0 || tomb.ID < 0 || morgue.ID < 0 || yard.ID < 0 || inc.ID < 0 {
		t.Fatalf("groups %+v", groups)
	}
	if tomb.Area != 25 || len(tomb.Stations) != 0 {
		t.Errorf("tomb %+v", tomb)
	}
	// The morgue's 5x4 floor, the cooler's 2x1 and the cell that keeps it reachable.
	if morgue.Area != 20+2+1 || !slices.Contains(morgue.Requires, EnvCold) || len(morgue.Stations) != 1 || morgue.Stations[0].Purpose != PurposeCool {
		t.Errorf("morgue %+v", morgue)
	}
	if slices.Contains(tomb.Requires, EnvCold) {
		t.Errorf("only the morgue is kept cold: %+v", tomb)
	}
	if yard.Width != WasteYardH || inc.Area != 9+1 || !slices.ContainsFunc(inc.Links, func(l GroupLink) bool { return l.To == yard.ID && l.Required }) ||
		!slices.ContainsFunc(yard.Links, func(l GroupLink) bool { return l.To == inc.ID && l.Required }) {
		t.Errorf("waste yard %+v incinerator %+v", yard, inc)
	}
}

// The embedding sites every outskirts room with no band and the incinerator
// beside the waste yard.
func TestEmbedPlacesOutskirtsWithoutBand(t *testing.T) {
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
	yard, _ := emb.Block(groupOf(groups, PlannedWasteYard).ID)
	inc, _ := emb.Block(groupOf(groups, PlannedIncinerator).ID)
	if gapDist(yard, inc) != 1 {
		t.Errorf("incinerator %+v not beside the waste yard %+v", inc, yard)
	}
}

// The tiler makes the five rooms: the graveyard and the waste yard unfenced and
// doorless, the others walled with an entrance.
func TestTileMakesOutskirtsRooms(t *testing.T) {
	groups := outskirtsGroups(t)
	s := plusSurvey(300, 80)
	in := EmbedInput{Groups: groups, Zones: Zone(s), Survey: s, RoofDistance: testRoof}
	emb, err := Embed(in)
	if err != nil {
		t.Fatal(err)
	}
	res, err := TileEmbedding(TileInput{Groups: groups, Embedding: emb, Catalog: clusterCatalog(), Zones: in.Zones, Survey: s})
	if err != nil {
		t.Fatal(err)
	}
	for _, role := range []PlannedRole{PlannedTomb, PlannedMorgue, PlannedGraveyard, PlannedWasteYard, PlannedIncinerator} {
		rooms := res.Plan.roomsOf(role)
		if len(rooms) != 1 {
			t.Fatalf("%s rooms %d", role, len(rooms))
		}
		if r := rooms[0]; r.Outdoor != role.IsOutdoor() || r.Unfenced() && len(res.Plan.LinkCells(r.ID)) > 0 {
			t.Errorf("%s room %+v", role, r)
		}
	}
	if morgue := res.Plan.roomsOf(PlannedMorgue)[0]; morgue.Crossing == nil || !morgue.Crossing.Cold {
		t.Errorf("the morgue is cold: %+v", morgue.Crossing)
	}
}
