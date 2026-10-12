package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A sleeper with a target is a bedroom node of its own, sized by suiteCells and
// not by the old generator's snap; the ordinary node keeps the other beds.
func TestStationGraphSuiteIsATargetSizedBedroomNode(t *testing.T) {
	in := graphTestInput()
	in.Population = StationPopulation{Colonists: 4}
	in.Suites = []SuiteWish{{Target: 30}, {Target: 60}}
	g, err := BuildStationGraph(in)
	if err != nil {
		t.Fatal(err)
	}
	wide, ok := g.Node("Bed/sleep/suite0")
	if !ok || wide.Target != 60 || wide.Floor != int(suiteCells(60)) || wide.Count != 1 {
		t.Fatalf("widest suite = %+v", wide)
	}
	if small, _ := g.Node("Bed/sleep/suite1"); small.Floor != int(suiteCells(30)) {
		t.Errorf("second suite floor %d, want %d", small.Floor, suiteCells(30))
	}
	if rest, ok := g.Node("Bed/sleep"); !ok || rest.Count != 3 || rest.Floor != 0 {
		t.Errorf("four colonists and a spare bed less two suites leave 3 ordinary beds: %+v", rest)
	}
	if w := g.Weight("Bed/sleep/suite0", OutsideNode); w != 1 {
		t.Errorf("a suite holds one colonist: %v trips", w)
	}
	if w := g.Weight("Bed/sleep", OutsideNode); w != 2 {
		t.Errorf("the other two colonists: %v trips", w)
	}
}

// A standing suite keeps its own floor when the target asks for less, and a
// raised target asks for more.
func TestSuiteWishFloorNeverShrinksAStandingSuite(t *testing.T) {
	if got := (SuiteWish{Target: 10, Kept: 40}).Floor(); got != 40 {
		t.Errorf("floor %d, want the kept 40", got)
	}
	if got := (SuiteWish{Target: 200, Kept: 40}).Floor(); got != int(suiteCells(200)) || got <= 40 {
		t.Errorf("floor %d, want %d", got, suiteCells(200))
	}
}

// No pair rule merges a suite with a bedroom or another suite, and the group is
// the suite plan role at the node's floor.
func TestClusterKeepsASuiteApartFromBedrooms(t *testing.T) {
	bed := node("Bed", PurposeSleep, 1)
	suite := node("Bed", PurposeSleep, 1)
	suite.ID, suite.Target, suite.Floor = "Bed/sleep/suite0", 40, 30
	other := node("Bed", PurposeSleep, 1)
	other.ID, other.Target, other.Floor = "Bed/sleep/suite1", 40, 30
	cat := clusterCatalog()
	b := cat.Stations["Bed"]
	b.Capacity = StationCapacity{Kind: CapacitySleepers, Slots: 1}
	cat.Stations["Bed"] = b
	got, err := ClusterStations(ClusterInput{
		Graph:   StationGraph{Nodes: []StationNode{bed, other, suite}, Edges: []StationEdge{edge(bed, suite, 900, true), edge(suite, other, 900, true)}},
		Pairs:   []NodePair{{A: bed.ID, B: suite.ID, Rule: PairTogether}},
		Catalog: cat, RoofDistance: testRoof,
	})
	if err != nil {
		t.Fatal(err)
	}
	suites := 0
	for _, g := range got.Groups {
		role, _ := PlannedRoleOfGroup(g)
		if role == PlannedSuite {
			suites++
			if len(g.Stations) != 1 || g.Area < 30 || g.Footprint+g.Clearance < 30 {
				t.Errorf("suite group %+v", g)
			}
		} else if len(g.Stations) != 1 || g.Role != RoomRoleBedroom {
			t.Errorf("bedroom merged: %+v", g)
		}
	}
	if suites != 2 || len(got.Groups) != 3 {
		t.Fatalf("groups %+v", got.Groups)
	}
	for _, d := range got.Decisions {
		if d.Verdict == ClusterMerge {
			t.Errorf("merge %+v", d)
		}
	}
}

// A raised target outgrows the standing suite (its group floor passes the
// suite's), and the successor is a larger suite.
func TestRaisedTargetOutgrowsASuiteAndAsksForALargerSuite(t *testing.T) {
	plan := LayoutPlan{Rooms: []PlannedRoom{{ID: 1, Role: PlannedSuite, Region: region(4, 6)}}, NextRoomID: 2}
	wish := SuiteWish{Target: 120, Kept: 24}
	node := StationNode{ID: "Bed/sleep/suite0", Def: "Bed", Purpose: PurposeSleep, Count: 1, Target: wish.Target, Floor: wish.Floor()}
	if node.Floor <= 24 {
		t.Fatalf("a target of 120 needs more than 24 cells, got %d", node.Floor)
	}
	group := RoomGroup{ID: 0, Role: RoomRoleBedroom, Planned: PlannedSuite, Area: node.Floor,
		Stations: []GroupStation{{Node: node.ID, Def: "Shelf", Count: 1}}}
	built := []BuiltRegion{{Room: 1, Role: RoomRoleSuite, Planned: PlannedSuite, Region: region(4, 6)}}
	m := MatchGroupRooms([]RoomGroup{group}, built)
	if len(m[0].Rooms) != 1 || !GroupOutgrown(group, m[0]) {
		t.Fatalf("match %+v: the suite group is matched only to the suite and outgrown", m)
	}
	advice := StationAdvice{
		Groups:    []GroupAdvice{{Group: group, Rooms: m[0].Rooms, Capacity: m[0].Capacity, Outgrown: true}},
		Embedding: Embedding{Blocks: []EmbeddedBlock{{Group: 0, Interior: Rectangle{Width: 10, Height: 8}}}},
	}
	got := OutgrownSuccession(advice, successionCatalog(), plan)
	if got.Replaces != 1 || got.Role != PlannedSuite || got.Floor != 24 || got.Shortfall != node.Floor-24 {
		t.Fatalf("successor = %+v, want a suite replacing room 1 at 24 + %d", got, node.Floor-24)
	}
}

// A replaced suite's owner moves into the successor suite, never into an
// ordinary bedroom, and the old suite is slept in until then.
func TestSuiteSuccessorMigratesItsOwnerToTheLargerSuite(t *testing.T) {
	at := func(id, replaces int32, role PlannedRole, x int32) PlannedRoom {
		return PlannedRoom{ID: id, Replaces: replaces, Role: role, Region: RectRegion(Rectangle{X: x, Z: 0, Width: 4, Height: 6})}
	}
	old, next, plain := at(1, 0, PlannedSuite, 0), at(2, 1, PlannedSuite, 10), at(3, 0, PlannedBedroom, 20)
	plan := LayoutPlan{Rooms: []PlannedRoom{old, next, plain}, NextRoomID: 4}
	standing := func(r PlannedRoom, id string, beds ...string) Room {
		return Room{ID: id, Role: domain.Known(RoomRoleBedroom), Enclosed: domain.Known(true), Beds: beds, Cells: []domain.Cell{r.Region.anchor()}}
	}
	bed := func(id string, owners ...PawnID) SleepingBed {
		return SleepingBed{ID: id, Definition: "Bed", Humanlike: domain.Known(true), Medical: domain.Known(false), Prisoners: domain.Known(false), Owners: owners, AccessibleTo: []PawnID{"a"}}
	}
	rooms := RoomObservation{Shapes: testShapes, Rooms: []Room{standing(old, "old", "ob"), standing(next, "new", "nb"), standing(plain, "plain", "pb")}}
	sleeping := SleepingObservation{Colonists: 1,
		People: []SleepingPerson{{ID: "a", OwnedBed: domain.Known("ob")}},
		Beds:   []SleepingBed{bed("ob", "a"), bed("nb"), bed("pb")}}
	if slept := SleptBedrooms(plan, domain.Known(rooms), domain.Known(sleeping)); !slept[old.ID] {
		t.Fatalf("slept = %v, want the old suite while a sleeps in it", slept)
	}
	step := NextMigrateStep(plan, rooms, sleeping)
	if step.Kind != BedroomMove || step.Pawn != "a" || step.Bed != "nb" || step.PreviousBed != "ob" {
		t.Fatalf("step = %+v, want a moved from the old suite straight to the new suite's bed", step)
	}
	// The successor not yet furnished: reconcile it, not the ordinary bedroom.
	rooms.Rooms[1].Beds = nil
	sleeping.Beds = []SleepingBed{bed("ob", "a"), bed("pb")}
	if step := NextMigrateStep(plan, rooms, sleeping); step.Kind != BedroomReconcile || step.Room.ID != next.ID {
		t.Fatalf("step = %+v, want the successor suite reconciled", step)
	}
}

// SuiteWishes: every planned suite is one wish, its owner's target on it, and a
// claim of a standard room no vacant suite answers adds one; a too-small
// suite adds none beyond its own.
func TestSuiteWishesCountASuiteOnceAndAddUnansweredClaims(t *testing.T) {
	plan := LayoutPlan{Rooms: []PlannedRoom{{ID: 1, Role: PlannedSuite, Region: region(4, 6)}}, NextRoomID: 2}
	claims := []SuiteClaim{{Pawn: "a", Bed: "ob", Target: 90, Reason: SuiteClaimTooSmall}, {Pawn: "b", Bed: "pb", Target: 50, Reason: SuiteClaimSpace}, {Pawn: "c", Bed: "qb", Target: 60, Reason: SuiteClaimFloor}}
	got := SuiteWishes(plan, RoomObservation{}, SleepingObservation{}, nil, claims)
	// The unowned suite is vacant and answers b; c is the one new wish.
	if len(got) != 2 || got[0].Kept != 24 || got[1] != (SuiteWish{Target: 60}) {
		t.Fatalf("wishes = %+v, want the standing suite and one new one for c", got)
	}
}
