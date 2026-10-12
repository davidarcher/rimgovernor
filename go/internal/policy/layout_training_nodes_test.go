package policy

import (
	"reflect"
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// trainingGroups clusters a small colony together with the range and ring nodes.
func trainingGroups(t testing.TB, stands, markers int) []RoomGroup {
	t.Helper()
	stove := node("Stove", PurposeWork, 1, workBand)
	table := node("Table", PurposeDine, 2)
	bed := node("Bed", PurposeSleep, 3)
	shelf := node("Shelf", PurposeStore, 2)
	g := StationGraph{
		Nodes: []StationNode{bed, stove, table, shelf},
		Edges: []StationEdge{edge(stove, table, 10, false), edge(bed, table, 6, false), edge(table, shelf, 2, false)},
		Rooms: TrainingRoomNodes(stands, markers),
	}
	got, err := ClusterStations(ClusterInput{Graph: g, Catalog: clusterCatalog(), RoofDistance: testRoof})
	if err != nil {
		t.Fatal(err)
	}
	return got.Groups
}

// The graph holds a range and a ring node while the colony asks for them: the
// exact floor, the training band, the ring beside the range.
func TestTrainingRoomNodes(t *testing.T) {
	if got := TrainingRoomNodes(0, 0); len(got) != 0 {
		t.Fatalf("no demand, nodes %+v", got)
	}
	nodes := TrainingRoomNodes(5, 7)
	if len(nodes) != 2 {
		t.Fatalf("nodes %+v", nodes)
	}
	rng, ring := nodes[0], nodes[1]
	if rng.Role != PlannedTrainingRange || rng.Area != 5*(RangeDistance+1) || rng.Width != RangeDistance+1 || rng.Beside != "" ||
		rng.Reason != ReasonTrainingRoom || rng.Joins || rng.Cold {
		t.Errorf("range %+v", rng)
	}
	if ring.Role != PlannedSparringRing || ring.Area != int(RingSide*RingSide) || ring.Width != RingSide || ring.Markers != 7 ||
		ring.Beside != rng.ID {
		t.Errorf("ring %+v", ring)
	}
	// Counts clamp as the siting does.
	if lo := TrainingRoomNodes(1, 1); lo[0].Area != RangeMinStands*(RangeDistance+1) || lo[1].Markers != RingMinMarkers || lo[1].Beside != lo[0].ID {
		t.Errorf("floor %+v", lo)
	}
	if hi := TrainingRoomNodes(99, 99); hi[0].Area != RangeMaxStands*(RangeDistance+1) || hi[1].Markers != RingMaxMarkers {
		t.Errorf("cap %+v", hi)
	}
	// A ring alone has nothing to stand beside.
	if only := TrainingRoomNodes(0, 4); len(only) != 1 || only[0].Beside != "" {
		t.Errorf("ring alone %+v", only)
	}
}

// Every stand count a range widens to is a node the same size as the widened
// plan room: growing in place and re-asking the graph agree.
func TestTrainingNodeMatchesWidenedRange(t *testing.T) {
	for stands := int32(RangeMinStands); stands <= RangeMaxStands; stands++ {
		w, h := rangeBlockSize(stands, true)
		room := rangeFromBlock(Rectangle{X: 3, Z: 3, Width: w, Height: h}, domain.North, stands)
		n := TrainingRoomNodes(int(stands), 0)[0]
		in := room.Region.Bounds()
		if int32(n.Area) != in.Width*in.Height || n.Width != in.Height || RangeStandCount(room) != stands {
			t.Errorf("stands %d: node %+v room %+v", stands, n, in)
		}
	}
}

// Clustering keeps the exact floor of both rooms (no door threshold on an open
// room), carries the facing and markers, and links the ring to the range.
func TestClusterMakesTrainingGroups(t *testing.T) {
	groups := trainingGroups(t, 4, 6)
	rng, ring := groupOf(groups, PlannedTrainingRange), groupOf(groups, PlannedSparringRing)
	if rng.ID < 0 || ring.ID < 0 {
		t.Fatalf("groups %+v", groups)
	}
	if rng.Area != 4*(RangeDistance+1) || rng.Width != RangeDistance+1 || len(rng.Stations) != 0 {
		t.Errorf("range %+v", rng)
	}
	if ring.Area != int(RingSide*RingSide) || ring.Width != RingSide || ring.Markers != 6 {
		t.Errorf("ring %+v", ring)
	}
	if !slices.ContainsFunc(ring.Links, func(l GroupLink) bool { return l.To == rng.ID && l.Required }) ||
		!slices.ContainsFunc(rng.Links, func(l GroupLink) bool { return l.To == ring.ID && l.Required }) {
		t.Errorf("ring not linked to the range: %+v %+v", ring.Links, rng.Links)
	}
}

func embedTraining(t *testing.T, stands, markers int) ([]RoomGroup, EmbedInput, Embedding) {
	t.Helper()
	groups := trainingGroups(t, stands, markers)
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
	return groups, in, emb
}

// The embedding stands the ring beside the range, keeps with no band.
func TestEmbedPlacesRingBesideRange(t *testing.T) {
	groups, _, emb := embedTraining(t, 4, 6)
	rid, gid := groupOf(groups, PlannedTrainingRange).ID, groupOf(groups, PlannedSparringRing).ID
	rng, _ := emb.Block(rid)
	ring, _ := emb.Block(gid)
	if gapDist(rng, ring) != 1 {
		t.Errorf("ring %+v not beside the range %+v", ring, rng)
	}
	for _, d := range emb.Decisions {
		if d.Reason == ReasonEmbedLinkNotAdjacent {
			t.Errorf("unsatisfied: %+v", d)
		}
	}
}

// The tiler writes the range's facing (away from the base) and the ring's
// markers onto the tiled rooms, both outdoor and unfenced.
func TestTileWritesFacingAndMarkers(t *testing.T) {
	groups, in, emb := embedTraining(t, 4, 6)
	res, err := TileEmbedding(TileInput{Groups: groups, Embedding: emb, Catalog: clusterCatalog(), Zones: in.Zones, Survey: in.Survey})
	if err != nil {
		t.Fatal(err)
	}
	rooms := res.Plan.roomsOf(PlannedTrainingRange)
	rings := res.Plan.roomsOf(PlannedSparringRing)
	if len(rooms) != 1 || len(rings) != 1 {
		t.Fatalf("rooms %d rings %d", len(rooms), len(rings))
	}
	rng, ring := rooms[0], rings[0]
	if !rng.Outdoor || !ring.Outdoor || !rng.Unfenced() || !ring.Unfenced() {
		t.Errorf("range %+v ring %+v", rng, ring)
	}
	if ring.Markers != 6 || ring.Facing != "" || RangeStandCount(rng) != 4 {
		t.Errorf("ring %+v range stands %d", ring, RangeStandCount(rng))
	}
	ref, _ := awayReference(groups, placedBlocks(groups, emb.Blocks))
	if rng.Facing != dummyFacing(rng.Region.Bounds(), ref) {
		t.Errorf("facing %v", rng.Facing)
	}
	// The dummies point away from the base:.
	b := rng.Region.Bounds()
	switch rng.Facing {
	case domain.North:
		if 2*b.Z+b.Height < 2*ref.Z+ref.Height {
			t.Errorf("range %+v faces the base %+v", b, ref)
		}
	case domain.South:
		if 2*b.Z+b.Height >= 2*ref.Z+ref.Height {
			t.Errorf("range %+v faces the base %+v", b, ref)
		}
	case domain.East:
		if 2*b.X+b.Width < 2*ref.X+ref.Width {
			t.Errorf("range %+v faces the base %+v", b, ref)
		}
	case domain.West:
		if 2*b.X+b.Width >= 2*ref.X+ref.Width {
			t.Errorf("range %+v faces the base %+v", b, ref)
		}
	default:
		t.Errorf("facing %v", rng.Facing)
	}
	if got := len(RangeLayout(rng)); got != 2*4 {
		t.Errorf("RangeLayout of the tiled range holds %d pieces", got)
	}
	if got := len(RingTemplate(ring)); got != 6 {
		t.Errorf("RingTemplate holds %d markers", got)
	}
}

// The embedding and tiling are deterministic.
func TestTrainingRoomsAreDeterministic(t *testing.T) {
	var plans []LayoutPlan
	var embs []Embedding
	for range 2 {
		groups, in, emb := embedTraining(t, 3, 5)
		res, err := TileEmbedding(TileInput{Groups: groups, Embedding: emb, Catalog: clusterCatalog(), Zones: in.Zones, Survey: in.Survey})
		if err != nil {
			t.Fatal(err)
		}
		plans, embs = append(plans, res.Plan), append(embs, emb)
	}
	if !reflect.DeepEqual(embs[0], embs[1]) || !reflect.DeepEqual(plans[0], plans[1]) {
		t.Errorf("two runs differ:\n%+v\n%+v", embs[0].Blocks, embs[1].Blocks)
	}
}

// The graph builder adds the training nodes only for a demand.
func TestGraphCarriesTrainingRoomNodes(t *testing.T) {
	g, err := BuildStationGraph(graphTestInput())
	if err != nil {
		t.Fatal(err)
	}
	if slices.ContainsFunc(g.Rooms, func(r RoomNode) bool { return r.Role == PlannedTrainingRange || r.Role == PlannedSparringRing }) {
		t.Errorf("training nodes without a demand: %+v", g.Rooms)
	}
	in := graphTestInput()
	in.TrainingStands, in.TrainingMarkers = 4, 5
	g, err = BuildStationGraph(in)
	if err != nil {
		t.Fatal(err)
	}
	var got []PlannedRole
	for _, r := range g.Rooms {
		if r.Reason == ReasonTrainingRoom {
			got = append(got, r.Role)
		}
	}
	if !slices.Equal(got, []PlannedRole{PlannedTrainingRange, PlannedSparringRing}) {
		t.Errorf("training nodes %v", got)
	}
}
