package policy

import (
	"reflect"
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// clusterCatalog is a small catalog: humanlike beds, a stove and a bench
// that want their own role, a table, a plain and a cold shelf.
func clusterCatalog() StationCatalog {
	cat := roleTestCatalog()
	shape := func(def string, x, z int32, s Station) Station {
		s.Def = def
		s.Shape = InteriorPieceDef{Def: def, Size: domain.Cell{X: x, Z: z}}
		return s
	}
	band := EnvNeed{Kind: NeedTemperature, Min: 9, Max: 35, Factor: 0.7}
	stove, bench := cat.Stations["Stove"], cat.Stations["Bench"]
	stove.Needs = append(stove.Needs, band)
	bench.Needs = append(bench.Needs, band)
	cold := cat.Stations["Shelf"]
	cold.Needs = []EnvNeed{{Kind: NeedCold}}
	cat.Stations["Bed"] = shape("Bed", 1, 2, cat.Stations["Bed"])
	cat.Stations["Stove"] = shape("Stove", 3, 1, stove)
	cat.Stations["Bench"] = shape("Bench", 3, 1, bench)
	cat.Stations["Table"] = shape("Table", 1, 2, cat.Stations["Table"])
	cat.Stations["Shelf"] = shape("Shelf", 2, 1, cat.Stations["Shelf"])
	cat.Stations["ColdShelf"] = shape("ColdShelf", 2, 1, cold)
	return cat
}

func node(def string, purpose StationPurpose, count int, needs ...EnvNeed) StationNode {
	return StationNode{ID: nodeID(def, purpose), Def: def, Purpose: purpose, Count: count, Needs: needs}
}

func edge(a, b StationNode, weight float64, required bool) StationEdge {
	x, y := orderedPair(a.ID, b.ID)
	return StationEdge{A: x, B: y, Weight: weight, Required: required}
}

const testRoof = 6.9

var workBand = EnvNeed{Kind: NeedTemperature, Min: 9, Max: 35, Factor: 0.7}

func clusterOf(t *testing.T, g StationGraph) Clustering {
	t.Helper()
	got, err := ClusterStations(ClusterInput{Graph: g, Catalog: clusterCatalog(), RoofDistance: testRoof})
	if err != nil {
		t.Fatal(err)
	}
	return got
}

func groupWith(t *testing.T, c Clustering, id string) RoomGroup {
	t.Helper()
	for _, g := range c.Groups {
		for _, s := range g.Stations {
			if s.Node == id {
				return g
			}
		}
	}
	t.Fatalf("no group holds %s: %+v", id, c.Groups)
	return RoomGroup{}
}

// Beds never share with the stove, tables follow the heavier flow, a cold
// shelf stays apart from a stove that works in a temperature band but keeps a
// required link to it, and beds split to one per group.
func TestClusterStationsSplitsAndMerges(t *testing.T) {
	stove := node("Stove", PurposeWork, 1, workBand)
	table := node("Table", PurposeDine, 1)
	bed := node("Bed", PurposeSleep, 3)
	med := node("Bed", PurposeSleepMed, 2)
	cold := node("ColdShelf", PurposeStoreCold, 1, EnvNeed{Kind: NeedCold})
	g := StationGraph{
		Nodes: []StationNode{bed, med, cold, stove, table},
		Edges: []StationEdge{edge(stove, cold, 10, true), edge(stove, table, 10, false), edge(bed, table, 6, false), edge(med, table, 2, false)},
	}
	got := clusterOf(t, g)
	kitchen := groupWith(t, got, stove.ID)
	if kitchen.Role != RoomRoleKitchen || groupWith(t, got, table.ID).ID != kitchen.ID {
		t.Errorf("stove and table: %+v", kitchen)
	}
	if groupWith(t, got, bed.ID).ID == kitchen.ID || groupWith(t, got, med.ID).ID == kitchen.ID {
		t.Errorf("a bed joined the kitchen: %+v", got.Groups)
	}
	beds, hospitals := 0, 0
	for _, grp := range got.Groups {
		switch grp.Role {
		case RoomRoleBedroom:
			beds++
		case RoomRoleHospital:
			hospitals++
			if grp.Stations[0].Count != 2 {
				t.Errorf("hospital beds split: %+v", grp)
			}
		}
	}
	if beds != 3 || hospitals != 1 {
		t.Errorf("bedrooms %d hospitals %d: %+v", beds, hospitals, got.Groups)
	}
	freezer := groupWith(t, got, cold.ID)
	if !slices.Contains(freezer.Requires, EnvCold) || freezer.ID == kitchen.ID {
		t.Errorf("cold shelf: %+v", freezer)
	}
	if !slices.ContainsFunc(freezer.Links, func(l GroupLink) bool { return l.To == kitchen.ID && l.Required }) {
		t.Errorf("no required link from the cold shelf to the kitchen: %+v", freezer.Links)
	}
	if kitchen.Footprint != 3+2 || kitchen.Clearance != 2+1 || kitchen.Area != 8 {
		t.Errorf("kitchen area %+v", kitchen)
	}
}

// A bench and a stove flip each other's role: they stay apart even with a
// heavy flow; more tables than the stove can outweigh also separate.
func TestClusterStationsKeepsLostEffectsApart(t *testing.T) {
	stove := node("Stove", PurposeWork, 1, workBand)
	bench := node("Bench", PurposeWork, 1, workBand)
	table := node("Table", PurposeDine, 3)
	g := StationGraph{
		Nodes: []StationNode{bench, stove, table},
		Edges: []StationEdge{edge(stove, bench, 50, false), edge(stove, table, 10, false)},
	}
	got := clusterOf(t, g)
	if groupWith(t, got, stove.ID).ID == groupWith(t, got, bench.ID).ID {
		t.Errorf("stove and bench share: %+v", got.Groups)
	}
	// Three tables outscore a stove (36 against 28), so the stove would be
	// slowed in their room.
	if groupWith(t, got, stove.ID).ID == groupWith(t, got, table.ID).ID {
		t.Errorf("stove joined three tables: %+v", got.Groups)
	}
	reasons := map[string]bool{}
	for _, d := range got.Decisions {
		reasons[d.Reason] = true
	}
	if !reasons[ReasonClusterLostEffect] {
		t.Errorf("decisions %+v", got.Decisions)
	}
}

// The area limit follows the roof: a pool larger than the largest roofed
// room splits.
func TestClusterStationsAreaLimit(t *testing.T) {
	limit, err := maxRoomArea(testRoof)
	if err != nil || limit <= 0 || limit > shellMaxInterior {
		t.Fatalf("limit %d: %v", limit, err)
	}
	shelf := node("Shelf", PurposeStore, limit, EnvNeed{Kind: NeedIndoors})
	got := clusterOf(t, StationGraph{Nodes: []StationNode{shelf}})
	if len(got.Groups) < 2 {
		t.Fatalf("%d shelves of 2 cells fit one room of %d cells", shelf.Count, limit)
	}
	total := 0
	for _, g := range got.Groups {
		total += g.Stations[0].Count
		if g.Area > limit {
			t.Errorf("group area %d over %d", g.Area, limit)
		}
	}
	if total != shelf.Count {
		t.Errorf("stations %d, want %d", total, shelf.Count)
	}
	if _, err := maxRoomArea(0); err == nil {
		t.Error("no roof distance accepted")
	}
}

func TestClusterStationsDeterministicAndErrors(t *testing.T) {
	stove := node("Stove", PurposeWork, 2, workBand)
	table := node("Table", PurposeDine, 1)
	bed := node("Bed", PurposeSleep, 4)
	g := StationGraph{Nodes: []StationNode{bed, stove, table}, Edges: []StationEdge{edge(stove, table, 4, false), edge(bed, table, 4, false)}}
	first := clusterOf(t, g)
	for range 5 {
		if again := clusterOf(t, g); !reflect.DeepEqual(first, again) {
			t.Fatal("clustering changed")
		}
	}
	bad := StationGraph{Nodes: []StationNode{node("Nothing", PurposeStore, 1)}}
	if _, err := ClusterStations(ClusterInput{Graph: bad, Catalog: clusterCatalog(), RoofDistance: testRoof}); err == nil {
		t.Error("unknown station accepted")
	}
}
