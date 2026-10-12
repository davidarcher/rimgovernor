package policy

import (
	"reflect"
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/slowtest"
)

func circulationNode() RoomNode {
	return RoomNode{ID: string(PlannedCirculation), Role: PlannedCirculation, Width: CirculationWidth, Joins: true, Reason: ReasonCirculation}
}

func testDepth(t testing.TB) int {
	t.Helper()
	depth, _, err := roomLimits(testRoof)
	if err != nil {
		t.Fatal(err)
	}
	return depth
}

// A colony whose private rooms do not outnumber its public ones gets no
// corridor; one with more gets a strip.
func TestCirculationOnlyWhenPrivateRoomsOutnumberPublicOnes(t *testing.T) {
	small := baseRoomGroups(3)
	got, d := addCirculation(small, circulationNode(), testDepth(t))
	if len(got) != len(small) || d.Verdict != ClusterKeep || d.Reason != ReasonClusterNoCirculation {
		t.Fatalf("3 bedrooms against 5 public rooms: %d groups, %+v", len(got), d)
	}
	large := baseRoomGroups(12)
	got, d = addCirculation(large, circulationNode(), testDepth(t))
	if len(got) <= len(large) || d.Verdict != ClusterCirculate {
		t.Fatalf("12 bedrooms against 5 public rooms: %d groups, %+v", len(got), d)
	}
}

// The corridor is a strip of the node's width, sized from the rooms it joins,
// required beside each of them, and joins the table the sleepers eat at.
func TestCirculationStripJoinsPrivateRoomsAndTheirHubs(t *testing.T) {
	groups, _ := addCirculation(baseRoomGroups(12), circulationNode(), testDepth(t))
	var strips []RoomGroup
	for _, g := range groups {
		if g.Planned == PlannedCirculation {
			strips = append(strips, g)
		}
	}
	if len(strips) != 1 {
		t.Fatalf("%d corridors for 12 bedrooms", len(strips))
	}
	c := strips[0]
	if c.Width != CirculationWidth || len(c.Stations) != 0 || c.Area%int(c.Width) != 0 {
		t.Fatalf("corridor %+v", c)
	}
	joined := map[int]bool{}
	for _, l := range c.Links {
		if !l.Required {
			t.Errorf("link %+v is not required", l)
		}
		joined[l.To] = true
		if !slices.ContainsFunc(groups[l.To].Links, func(b GroupLink) bool { return b.To == c.ID && b.Required }) {
			t.Errorf("group %d has no link back to the corridor", l.To)
		}
	}
	for i, g := range groups {
		switch {
		case g.Planned == PlannedCirculation:
		case privateGroup(g):
			if !joined[i] {
				t.Errorf("bedroom %d is not joined", i)
			}
		case g.Role == RoomRoleDiningRoom:
			if !joined[i] {
				t.Errorf("the table the sleepers eat at is not joined")
			}
		default:
			if joined[i] {
				t.Errorf("hub %d (%s) is joined though no bedroom has a flow to it", i, g.Role)
			}
		}
	}
	shapes := groupShapes(c, testDepth(t))
	if shapes[0][1] != int32(CirculationWidth) || int(shapes[0][0]*shapes[0][1]) < c.Area {
		t.Errorf("shapes %v for area %d", shapes, c.Area)
	}
}

// The station graph holds the corridor node when somebody sleeps in a bed, and
// clustering sizes it from the groups it makes.
func TestGraphAndClusteringCarryCirculation(t *testing.T) {
	g := graphTestInput()
	got, err := BuildStationGraph(g)
	if err != nil {
		t.Fatal(err)
	}
	if len(got.Rooms) != 1 || got.Rooms[0].Role != PlannedCirculation || !got.Rooms[0].Joins || got.Rooms[0].Width != CirculationWidth {
		t.Fatalf("graph rooms %+v", got.Rooms)
	}
	g.Population = StationPopulation{}
	g.Population.Colonists = 0
	empty, err := BuildStationGraph(g)
	if err != nil {
		t.Fatal(err)
	}
	if len(empty.Rooms) != 1 {
		t.Fatalf("the spare bed is a sleeper: %+v", empty.Rooms)
	}
}

func TestClusteringAddsCirculationToLargeColoniesOnly(t *testing.T) {
	for _, tc := range []struct {
		pawns int
		want  bool
	}{{1, false}, {12, true}} {
		bed := node("Bed", PurposeSleep, tc.pawns)
		table := node("Table", PurposeDine, 1+tc.pawns/4)
		stove := node("Stove", PurposeWork, 1, workBand)
		g := StationGraph{Nodes: []StationNode{bed, stove, table}, Rooms: []RoomNode{circulationNode()},
			Edges: []StationEdge{edge(bed, table, 6, false), edge(stove, table, 10, false)}}
		got := clusterOf(t, g)
		circulation := slices.ContainsFunc(got.Groups, func(g RoomGroup) bool { return g.Planned == PlannedCirculation })
		if circulation != tc.want {
			t.Errorf("%d pawns: circulation %v, want %v", tc.pawns, circulation, tc.want)
		}
	}
}

// doorReach is the rooms reachable from an exterior door through link doors,
// never entering a room of the avoided roles.
func doorReach(p LayoutPlan, avoid ...PlannedRole) map[int32]bool {
	rooms := map[int32]PlannedRole{}
	for _, r := range p.Rooms {
		rooms[r.ID] = r.Role
	}
	reach := map[int32]bool{}
	var queue []int32
	for _, r := range p.Rooms {
		if !slices.Contains(avoid, r.Role) && p.hasExteriorDoor(r) {
			reach[r.ID] = true
			queue = append(queue, r.ID)
		}
	}
	for h := 0; h < len(queue); h++ {
		for _, l := range p.Links {
			for _, e := range [2][2]int32{{l.A, l.B}, {l.B, l.A}} {
				if e[0] == queue[h] && !reach[e[1]] && !slices.Contains(avoid, rooms[e[1]]) {
					reach[e[1]] = true
					queue = append(queue, e[1])
				}
			}
		}
	}
	return reach
}

// hasExteriorDoor reports that the room's primary door is not a link door.
func (p LayoutPlan) hasExteriorDoor(r PlannedRoom) bool {
	return !slices.ContainsFunc(p.Links, func(l RoomLink) bool { return (l.A == r.ID || l.B == r.ID) && l.Cell == r.Door })
}

// checkCirculatedPlan asserts the acceptance of #2911 on a tiled plan.
func checkCirculatedPlan(t *testing.T, in TileInput, res TileResult) {
	t.Helper()
	p := res.Plan
	var corridor int32
	for _, r := range p.Rooms {
		if r.Role == PlannedCirculation {
			corridor = r.ID
		}
	}
	if corridor == 0 {
		t.Fatal("the plan has no corridor")
	}
	for _, g := range res.Unfit() {
		t.Errorf("group %d (%s) is unfit", g, in.Groups[g].Role)
	}
	role := map[int32]PlannedRole{}
	for _, r := range p.Rooms {
		role[r.ID] = r.Role
	}
	for _, l := range p.Links {
		for _, e := range [2][2]int32{{l.A, l.B}, {l.B, l.A}} {
			if role[e[0]] == PlannedBedroom && role[e[1]] != PlannedCirculation && role[e[1]] != PlannedBedroom {
				t.Errorf("bedroom %d has a door onto %s %d", e[0], role[e[1]], e[1])
			}
		}
	}
	reach := doorReach(p, PlannedKitchen, PlannedFreezer)
	for _, r := range p.Rooms {
		if r.Role == PlannedBedroom && !reach[r.ID] {
			t.Errorf("bedroom %d is reached only through the kitchen or freezer", r.ID)
		}
	}
}

func TestTileCirculationJoinsBedroomsWithoutHubs(t *testing.T) {
	in, _ := tileInput(t, plainScoreSurvey(), 12, true)
	res, err := TileEmbedding(in)
	if err != nil {
		t.Fatal(err)
	}
	checkTiled(t, in, res)
	checkCirculatedPlan(t, in, res)
	again, err := TileEmbedding(in)
	if err != nil || !reflect.DeepEqual(res.Plan, again.Plan) || !reflect.DeepEqual(res.Pieces, again.Pieces) {
		t.Fatalf("tiling is not deterministic: %v", err)
	}
}

// The hundred-colonist base: no hub is unfit for want of room, no bedroom is
// reached through the kitchen or freezer. The trip walk with and without the
// corridor is logged.
func TestTileCirculationHundredColonists(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	s := plusSurvey(300, 80)
	with, _ := tileInput(t, s, 100, true)
	res, err := TileEmbedding(with)
	if err != nil {
		t.Fatal(err)
	}
	checkTiled(t, with, res)
	checkCirculatedPlan(t, with, res)
	without := with
	without.Groups = baseRoomGroups(100)
	var err2 error
	without.Embedding, err2 = Embed(EmbedInput{Groups: without.Groups, Zones: Zone(s), Survey: s, RoofDistance: testRoof})
	if err2 != nil {
		t.Fatal(err2)
	}
	before, err := TileEmbedding(without)
	if err != nil {
		t.Fatal(err)
	}
	t.Logf("trip walk before %d (unfit %v), after %d (unfit %v); rooms %d -> %d", tripWalk(before.Plan), before.Unfit(), tripWalk(res.Plan), res.Unfit(), len(before.Plan.Rooms), len(res.Plan.Rooms))
}
