package policy

import (
	"reflect"
	"runtime"
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// oneTickPoint prices a tick of lost work as a point: a cell walked costs a
// tick's share of the day, so a work-day is TicksPerDay points.
var oneTickPoint = RegroupPrice{Work: WorkPrice{WorkDayW: 1, CellWalkDays: 1.0 / domain.TicksPerDay}, Expected: domain.Unknown[float64]()}

func plainSurvey(n int32) MapSurvey {
	return zoningSurvey(n, func(x, z int32) SurveyCell { return SurveyCell{Walkable: true, Fertility: 1} })
}

func regroupIn(g StationGraph, s MapSurvey) RegroupInput {
	return RegroupInput{
		Cluster: ClusterInput{Graph: g, Catalog: clusterCatalog(), RoofDistance: testRoof},
		Embed:   EmbedInput{Zones: Zone(s), Survey: s, RoofDistance: testRoof},
		Price:   oneTickPoint,
	}
}

// benchStove is a stove and a bench that want their own roles: the seed keeps
// them apart (the merged kitchen slows the bench), and the merge costs the bench's
// quarter more time, a hundred ticks a day at 400 of work.
func benchStove(weight float64) (StationGraph, StationNode, StationNode) {
	stove := node("Stove", PurposeWork, 1, EnvNeed{Kind: NeedRole, Role: RoomRoleKitchen, Factor: 0.8})
	bench := node("Bench", PurposeWork, 1, EnvNeed{Kind: NeedRole, Role: RoomRoleWorkshop, Factor: 0.8})
	bench.DailyWork = 400
	return StationGraph{Nodes: []StationNode{bench, stove}, Edges: []StationEdge{edge(stove, bench, weight, false)}}, bench, stove
}

func roomsOf(r Regrouping) [][]string {
	var out [][]string
	for _, g := range r.Clustering.Groups {
		out = append(out, roomNodes(g))
	}
	return out
}

func assertReasons(t *testing.T, r Regrouping) {
	t.Helper()
	if len(r.Decisions) == 0 {
		t.Fatal("no decision")
	}
	for _, d := range r.Decisions {
		if d.Reason == "" || d.Verdict == "" {
			t.Errorf("decision without a reason: %+v", d)
		}
	}
}

// A pair the seed kept for a bench slowed by the wrong role merges when the walk
// and the grounds it saves outweigh the ticks it costs: on a small site the
// split pair does not fit, on a large one it stands side by side, the merge
// saves nothing and the conflict is only paid.
func TestRegroupMergesCompatiblePairOnSmallSiteOnly(t *testing.T) {
	g, bench, stove := benchStove(50)
	small, err := Regroup(regroupIn(g, plainSurvey(25)))
	if err != nil {
		t.Fatal(err)
	}
	if len(small.Clustering.Groups) != 1 || len(small.Decisions) != 1 || small.Decisions[0].Kind != RegroupMerge ||
		small.Decisions[0].Verdict != RegroupApplied || small.Decisions[0].Reason != ReasonRegroupMerge {
		t.Fatalf("small site: groups %v, decisions %+v", roomsOf(small), small.Decisions)
	}
	if d := small.Decisions[0]; !(d.A == bench.ID && d.B == stove.ID || d.A == stove.ID && d.B == bench.ID) {
		t.Errorf("merge names %q %q", d.A, d.B)
	}
	assertReasons(t, small)

	large, err := Regroup(regroupIn(g, plainSurvey(40)))
	if err != nil {
		t.Fatal(err)
	}
	if len(large.Clustering.Groups) != 2 || len(large.Pairs) != 0 || large.Decisions[0].Verdict != RegroupKept || large.Decisions[0].Reason != ReasonRegroupNoGain {
		t.Fatalf("large site: groups %v, decisions %+v", roomsOf(large), large.Decisions)
	}
	assertReasons(t, large)

	// Nothing improved: the grouping and the embedding are the seed's.
	in := regroupIn(g, plainSurvey(40))
	seed, err := ClusterStations(in.Cluster)
	if err != nil {
		t.Fatal(err)
	}
	if !reflect.DeepEqual(large.Clustering, seed) {
		t.Errorf("grouping changed with no gain: %+v vs %+v", large.Clustering.Groups, seed.Groups)
	}
	emb, err := Embed(EmbedInput{Groups: seed.Groups, Zones: in.Embed.Zones, Survey: in.Embed.Survey, RoofDistance: testRoof})
	if err != nil || !reflect.DeepEqual(large.Embedding, emb) {
		t.Errorf("embedding changed with no gain: %v", err)
	}
}

// Two bedrooms never merge into a barracks: the merged room would lose the
// sleep effect, the move is not proposed however small the site and however
// strong the flow. A suite (#2939) is a target-sized bedroom node of its own: a
// flow to it merges nothing.
func TestRegroupNeverMergesBedroomsOrSuites(t *testing.T) {
	bedA := node("Bed", PurposeSleep, 1)
	bedB := node("Bed", PurposeSleep, 1)
	bedB.ID = "Bed2"
	suite := node("Bed", PurposeSleep, 1)
	suite.ID, suite.Target, suite.Floor = "Bed/sleep/suite0", 40, 28
	g := StationGraph{
		Nodes: []StationNode{bedA, bedB, suite},
		Edges: []StationEdge{edge(bedA, bedB, 500, false), edge(bedA, suite, 500, false)},
	}
	in := regroupIn(g, plainSurvey(25))
	cat := in.Cluster.Catalog
	bed := cat.Stations["Bed"]
	bed.Capacity = StationCapacity{Kind: CapacitySleepers, Slots: 1}
	cat.Stations["Bed"] = bed
	in.Cluster.Catalog = cat
	got, err := Regroup(in)
	if err != nil {
		t.Fatal(err)
	}
	if len(got.Pairs) != 0 {
		t.Errorf("rules %+v", got.Pairs)
	}
	var beds, suites int
	for _, grp := range got.Clustering.Groups {
		switch {
		case grp.Planned == PlannedSuite:
			suites++
			if len(grp.Stations) != 1 || grp.Area < 28 {
				t.Errorf("suite is one bed in a 28 cell room: %+v", grp)
			}
		case grp.Role == RoomRoleBedroom && len(grp.Stations) == 1:
			beds++
		default:
			t.Errorf("a bedroom merged into %+v", grp)
		}
	}
	if beds != 2 || suites != 1 {
		t.Errorf("bedrooms %d suites %d: %+v", beds, suites, got.Clustering.Groups)
	}
	assertReasons(t, got)
}

// A bed and the tables of its dining are one bedroom the game reads as a
// barracks (the role guard): the mixed room splits, the bed alone is a bedroom.
func TestRegroupSplitsAGroupTheRoleGuardFlags(t *testing.T) {
	bed := node("Bed", PurposeSleep, 1)
	table := node("Table", PurposeDine, 2)
	g := StationGraph{Nodes: []StationNode{bed, table}, Edges: []StationEdge{edge(bed, table, 6, false)}}
	in := regroupIn(g, plainSurvey(60))
	cat := in.Cluster.Catalog
	b := cat.Stations["Bed"]
	b.Capacity = StationCapacity{Kind: CapacitySleepers, Slots: 1}
	cat.Stations["Bed"] = b
	in.Cluster.Catalog = cat
	in.Price.Mood = testMoodStages()
	in.Price.Expected = domain.Known(0.38)

	seed, err := ClusterStations(in.Cluster)
	if err != nil {
		t.Fatal(err)
	}
	if len(seed.Groups) != 1 || seed.Groups[0].Role != RoomRoleBedroom {
		t.Fatalf("seed %+v", seed.Groups)
	}
	in.Embed.Built = []BuiltRegion{{Room: 7, Role: RoomRoleBedroom, Region: RectRegion(Rectangle{X: 20, Z: 20, Width: 4, Height: 4})}}
	// Without the mismatch the room stays whole.
	whole, err := Regroup(in)
	if err != nil {
		t.Fatal(err)
	}
	if len(whole.Clustering.Groups) != 1 || whole.Decisions[0].Verdict != RegroupKept {
		t.Fatalf("unflagged room: %v %+v", roomsOf(whole), whole.Decisions)
	}
	in.Mismatches = []RoleMismatch{{Room: 7, Native: RoomRoleBarracks, Predicted: RoomRoleBedroom}}
	got, err := Regroup(in)
	if err != nil {
		t.Fatal(err)
	}
	if len(got.Clustering.Groups) != 2 || len(got.Decisions) != 1 || got.Decisions[0].Kind != RegroupSplit || got.Decisions[0].Reason != ReasonRegroupSplitRoleGuard {
		t.Fatalf("flagged room: %v %+v", roomsOf(got), got.Decisions)
	}
	if d := got.Decisions[0]; !(d.After < d.Before) {
		t.Errorf("total did not fall: %+v", d)
	}
	assertReasons(t, got)
}

// A bench in a kitchen pays a quarter more time; a room of workshop role stands
// beside: the bench moves there, the stove keeps its kitchen. Merging the
// pair, or splitting the bench into a room of its own, costs more rooms.
func TestRegroupMoveANeedRelievesARoleConflict(t *testing.T) {
	g, bench, stove := benchStove(5)
	other := node("Bench", PurposeWork, 1, EnvNeed{Kind: NeedRole, Role: RoomRoleWorkshop, Factor: 0.8})
	other.ID = "Bench2"
	g.Nodes = append(g.Nodes, other)
	in := regroupIn(g, plainSurvey(60))
	in.Cluster.Pairs = []NodePair{{A: bench.ID, B: stove.ID, Rule: PairTogether}}
	seed, err := ClusterStations(in.Cluster)
	if err != nil {
		t.Fatal(err)
	}
	kitchen := groupWith(t, seed, bench.ID)
	if !slices.ContainsFunc(kitchen.Costs, func(c ConflictCost) bool { return c.Kind == NeedRole && c.Node == bench.ID }) {
		t.Fatalf("no conflict to relieve: %+v", kitchen)
	}
	got, err := Regroup(in)
	if err != nil {
		t.Fatal(err)
	}
	if len(got.Decisions) != 1 || got.Decisions[0].Kind != RegroupMove || got.Decisions[0].Reason != ReasonRegroupMove {
		t.Fatalf("decisions %+v", got.Decisions)
	}
	workshop := groupWith(t, got.Clustering, bench.ID)
	if !slices.ContainsFunc(workshop.Stations, func(s GroupStation) bool { return s.Node == "Bench2" }) || groupWith(t, got.Clustering, stove.ID).ID == workshop.ID {
		t.Errorf("rooms %v", roomsOf(got))
	}
	for _, grp := range got.Clustering.Groups {
		if len(grp.Costs) != 0 {
			t.Errorf("conflict left: %+v", grp)
		}
	}
	assertReasons(t, got)
}

// A band station in a cold room pays; the cold room's required link keeps the
// shelf beside it, so splitting them costs only where the site is tight.
func TestRegroupSplitsAColdRoomOnTheBandStation(t *testing.T) {
	stove := node("Stove", PurposeWork, 1, workBand)
	stove.DailyWork = 3000
	cold := node("ColdShelf", PurposeStoreCold, 1, EnvNeed{Kind: NeedCold})
	g := StationGraph{Nodes: []StationNode{stove, cold}, Edges: []StationEdge{edge(stove, cold, 10, true)}}
	got, err := Regroup(regroupIn(g, plainSurvey(60)))
	if err != nil {
		t.Fatal(err)
	}
	if len(got.Clustering.Groups) != 2 || len(got.Decisions) != 1 || got.Decisions[0].Kind != RegroupSplit || got.Decisions[0].Reason != ReasonRegroupSplitConflict {
		t.Fatalf("rooms %v decisions %+v", roomsOf(got), got.Decisions)
	}
	if emb := got.Embedding; emb.Score.UnmetLinks != 0 {
		t.Errorf("required link unmet: %+v", emb.Decisions)
	}
	for _, grp := range got.Clustering.Groups {
		if len(grp.Costs) != 0 {
			t.Errorf("band cost left: %+v", grp)
		}
	}
}

// The search is seeded and the embedding deterministic, so the result is the
// same at any thread count.
func TestRegroupDeterministicAtAnyThreadCount(t *testing.T) {
	g, _, _ := benchStove(50)
	run := func(threads int) Regrouping {
		defer runtime.GOMAXPROCS(runtime.GOMAXPROCS(threads))
		got, err := Regroup(regroupIn(g, plainSurvey(25)))
		if err != nil {
			t.Fatal(err)
		}
		return got
	}
	one := run(1)
	for _, n := range []int{2, 8} {
		if got := run(n); !reflect.DeepEqual(one, got) {
			t.Fatalf("GOMAXPROCS %d differs", n)
		}
	}
}

// Without a work price nothing is weighed: the seed stands, and says why.
func TestRegroupUnpricedKeepsTheSeed(t *testing.T) {
	g, _, _ := benchStove(50)
	in := regroupIn(g, plainSurvey(25))
	in.Price = RegroupPrice{}
	got, err := Regroup(in)
	if err != nil {
		t.Fatal(err)
	}
	if len(got.Clustering.Groups) != 2 || got.Decisions[0].Verdict != RegroupSkipped || got.Decisions[0].Reason != ReasonRegroupUnpriced {
		t.Fatalf("%v %+v", roomsOf(got), got.Decisions)
	}
}

// An unpriced lost effect breaks ties between equal totals, fewer first.
func TestRegroupTrialRanking(t *testing.T) {
	a := &regroupTrial{points: 10, lost: 2}
	b := &regroupTrial{points: 10, lost: 1}
	if !b.better(a) || a.better(b) || a.better(a) {
		t.Error("ties do not break by lost effects")
	}
	if c := (&regroupTrial{points: 99, failed: 1}); !a.better(c) || c.better(a) {
		t.Error("an unplaced group does not outrank the total")
	}
}

func TestPairRulesApartBeatsTogether(t *testing.T) {
	stove := node("Stove", PurposeWork, 1)
	table := node("Table", PurposeDine, 1)
	g := StationGraph{Nodes: []StationNode{stove, table}, Edges: []StationEdge{edge(stove, table, 10, false)}}
	in := ClusterInput{Graph: g, Catalog: clusterCatalog(), RoofDistance: testRoof}
	got, err := ClusterStations(in)
	if err != nil || len(got.Groups) != 1 {
		t.Fatalf("seed %v %+v", err, got.Groups)
	}
	in.Pairs = []NodePair{{A: stove.ID, B: table.ID, Rule: PairTogether}, {A: table.ID, B: stove.ID, Rule: PairApart}}
	got, err = ClusterStations(in)
	if err != nil || len(got.Groups) != 2 || !slices.ContainsFunc(got.Decisions, func(d ClusterDecision) bool { return d.Reason == ReasonClusterApart }) {
		t.Fatalf("apart: %v %+v %+v", err, got.Groups, got.Decisions)
	}
}
