package policy

import (
	"math"
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
	cat.Stations["Cooler"] = shape("Cooler", 2, 1, Station{Access: AccessWall, Cooling: StationCooling{HeatPerSecond: 12, PowerDraw: 200}})
	cat.Stations["Butcher"] = shape("Butcher", 3, 1, Station{Contributions: cat.Stations["Stove"].Contributions})
	cat.Stations["Wasteful"] = shape("Wasteful", 1, 1, Station{Cooling: StationCooling{HeatPerSecond: 12, PowerDraw: 900}})
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
// shelf joins the stove that works in a temperature band (the conflict is
// recorded, not refused) and the group carries the most efficient cooler, and
// beds split to one per group.
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
	if !slices.Contains(freezer.Requires, EnvCold) || freezer.ID != kitchen.ID {
		t.Errorf("cold shelf: %+v", freezer)
	}
	if !slices.ContainsFunc(kitchen.Stations, func(s GroupStation) bool { return s.Def == "Cooler" && s.Purpose == PurposeCool && s.Count == 1 }) {
		t.Errorf("the cold group has no cooler: %+v", kitchen.Stations)
	}
	if !slices.ContainsFunc(got.Decisions, func(d ClusterDecision) bool { return d.Verdict == ClusterMerge && d.Conflict == ConflictColdBand }) {
		t.Errorf("the cold-band conflict is not recorded: %+v", got.Decisions)
	}
	// stove 3x1, cold shelf 2x1, cooler 2x1, table 1x2; a cell each for the
	// four and the door.
	if kitchen.Footprint != 3+2+2+2 || kitchen.Clearance != 4+1 || kitchen.Area != kitchen.Footprint+kitchen.Clearance {
		t.Errorf("kitchen area %+v", kitchen)
	}
}

// The outside node holds no station: its edges weigh the groups holding the
// other end, in proportion to their stations.
func TestClusterStationsSplitOutsideWeightOverGroups(t *testing.T) {
	bed := node("Bed", PurposeSleep, 3)
	table := node("Table", PurposeDine, 1)
	g := StationGraph{
		Nodes: []StationNode{bed, table},
		Edges: []StationEdge{edge(bed, table, 6, false), {A: bed.ID, B: OutsideNode, Weight: 6}},
	}
	got := clusterOf(t, g)
	total := 0.0
	for _, grp := range got.Groups {
		if grp.Role == RoomRoleBedroom && grp.Outside != 2 {
			t.Errorf("a one-bed group weighs 2 of the 6 trips: %+v", grp)
		}
		total += grp.Outside
	}
	if total != 6 {
		t.Errorf("outside weight %v, want 6", total)
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

// A cold group without a cooler in the catalog is reported, not guessed at.
func TestClusterStationsReportsNoCooler(t *testing.T) {
	cat := clusterCatalog()
	delete(cat.Stations, "Cooler")
	delete(cat.Stations, "Wasteful")
	cold := node("ColdShelf", PurposeStoreCold, 1, EnvNeed{Kind: NeedCold})
	got, err := ClusterStations(ClusterInput{Graph: StationGraph{Nodes: []StationNode{cold}}, Catalog: cat, RoofDistance: testRoof})
	if err != nil {
		t.Fatal(err)
	}
	if !slices.ContainsFunc(got.Decisions, func(d ClusterDecision) bool {
		return d.Verdict == ClusterUnsatisfied && d.Reason == ReasonClusterNoCooler && d.A == cold.ID
	}) {
		t.Errorf("no unsatisfied decision: %+v", got.Decisions)
	}
}

func costOf(costs []ConflictCost, kind EnvNeedKind, node string) (ConflictCost, bool) {
	for _, c := range costs {
		if c.Kind == kind && c.Node == node {
			return c, true
		}
	}
	return ConflictCost{}, false
}

// A bench slowed by the wrong role records the ticks it loses: daily work times
// (1/0.8 - 1), 100 ticks costing 25; a band station in a cold room records the
// x0.7 band the same way; lost role effects keep their name and sleepers.
func TestClusterStationsRecordsConflictCosts(t *testing.T) {
	kitchenNeed := EnvNeed{Kind: NeedRole, Role: RoomRoleKitchen, Factor: 0.8}
	benchNeed := EnvNeed{Kind: NeedRole, Role: RoomRoleWorkshop, Factor: 0.8}
	stove := node("Stove", PurposeWork, 1, kitchenNeed, workBand)
	stove.DailyWork = 300
	bench := node("Bench", PurposeWork, 1, benchNeed)
	bench.DailyWork = 100
	cold := node("ColdShelf", PurposeStoreCold, 1, EnvNeed{Kind: NeedCold})
	g := StationGraph{
		Nodes: []StationNode{bench, stove, cold},
		Edges: []StationEdge{edge(stove, bench, 50, false), edge(stove, cold, 10, true)},
	}
	got := clusterOf(t, g)

	var kept ClusterDecision
	for _, d := range got.Decisions {
		if d.Reason == ReasonClusterLostEffect {
			kept = d
		}
	}
	c, ok := costOf(kept.Costs, NeedRole, bench.ID)
	if !ok || c.TicksPerDay != 25 || c.Role != RoomRoleWorkshop || c.Factor != 0.8 || c.Count != 1 {
		t.Errorf("bench role cost: %+v", kept)
	}

	kitchen := groupWith(t, got, stove.ID)
	c, ok = costOf(kitchen.Costs, NeedTemperature, stove.ID)
	if want := 300 * (1/0.7 - 1); !ok || math.Abs(c.TicksPerDay-want) > 1e-9 || c.Factor != 0.7 {
		t.Errorf("stove band cost: %+v", kitchen.Costs)
	}
	if _, ok := costOf(kitchen.Costs, NeedRole, stove.ID); ok {
		t.Errorf("the stove is in a kitchen: %+v", kitchen.Costs)
	}
	merged := false
	for _, d := range got.Decisions {
		if d.Conflict == ConflictColdBand && d.Verdict == ClusterMerge {
			_, merged = costOf(d.Costs, NeedTemperature, stove.ID)
		}
	}
	if !merged {
		t.Errorf("the cold-band merge carries no band cost: %+v", got.Decisions)
	}

	cat := clusterCatalog()
	bed := cat.Stations["Bed"]
	bed.Capacity = StationCapacity{Kind: CapacitySleepers, Slots: 1}
	cat.Stations["Bed"] = bed
	in := ClusterInput{Graph: StationGraph{Nodes: []StationNode{node("Bed", PurposeSleep, 3)}}, Catalog: cat, RoofDistance: testRoof}
	split, err := ClusterStations(in)
	if err != nil {
		t.Fatal(err)
	}
	if len(split.Decisions) == 0 || split.Decisions[0].Verdict != ClusterSplit ||
		!reflect.DeepEqual(split.Decisions[0].LostEffects, []LostRoleEffect{{Effect: EffectSleepMood, Role: RoomRoleBedroom, Got: RoomRoleBarracks, Pawns: 3}}) {
		t.Errorf("split decisions: %+v", split.Decisions)
	}
	again, err := ClusterStations(in)
	if err != nil || !reflect.DeepEqual(split, again) {
		t.Errorf("not deterministic: %v", err)
	}
}

// Two beds never merge into a barracks, even when a pair rule asks for it: no
// planned room is a barracks, so the tiler could not place the group (#3017).
func TestClusterNeverMergesBedsIntoAnUnplaceableBarracks(t *testing.T) {
	cat := clusterCatalog()
	bed := cat.Stations["Bed"]
	bed.Capacity = StationCapacity{Kind: CapacitySleepers, Slots: 1}
	cat.Stations["Bed"] = bed
	cat.Stations["Bed2"] = bed
	a, b := node("Bed", PurposeSleep, 1), node("Bed2", PurposeSleep, 1)
	got, err := ClusterStations(ClusterInput{Graph: StationGraph{Nodes: []StationNode{a, b}, Edges: []StationEdge{edge(a, b, 5, false)}}, Catalog: cat, RoofDistance: testRoof, Pairs: []NodePair{{A: a.ID, B: b.ID, Rule: PairTogether}}})
	if err != nil {
		t.Fatal(err)
	}
	if len(got.Groups) != 2 {
		t.Fatalf("groups %+v", got.Groups)
	}
	for _, g := range got.Groups {
		if _, ok := PlannedRoleOfGroup(g); !ok {
			t.Errorf("group %d predicts %s, no planned room", g.ID, g.Role)
		}
	}
	if len(got.Decisions) == 0 || got.Decisions[len(got.Decisions)-1].Reason != ReasonClusterNoRoom {
		t.Errorf("decisions %+v", got.Decisions)
	}
}
