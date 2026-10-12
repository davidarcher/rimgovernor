package policy

import (
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func coldCorePlan(cold bool) LayoutPlan {
	zones := coreTestZones()
	g := newCoreGrid(zones, nil)
	seed, _ := g.seed()
	return g.generate(LayoutPlan{Zones: zones, Cold: cold}, seed, 3, 0, TechTierCamp).WithRoomIDs()
}

// childOf is the one child of room s with role in plan.
func childOf(t *testing.T, plan LayoutPlan, s PlannedRoom, role PlannedRole) PlannedRoom {
	t.Helper()
	var found []PlannedRoom
	for _, c := range plan.ChildrenOf(s.ID) {
		if c.Role == role {
			found = append(found, c)
		}
	}
	if len(found) != 1 {
		t.Fatalf("room %d has %d %s children, want one", s.ID, len(found), role)
	}
	return found[0]
}

// shelterWithKids is a plan of one shelter with the children asked for, ids
// handed out.
func shelterWithKids(in Rectangle, seed domain.Cell, airlock, porch bool) (LayoutPlan, PlannedRoom) {
	plan := LayoutPlan{Rooms: shelterWithChildren(in, seed, airlock, porch)}.WithRoomIDs()
	return plan, plan.Rooms[0]
}

func TestColdShelterHasAnAirlockTemperateNone(t *testing.T) {
	temperate := coldCorePlan(false)
	if kids := temperate.ChildrenOf(shelterOf(t, temperate).ID); len(kids) != 0 {
		t.Fatalf("temperate shelter has children: %+v", kids)
	}
	plan := coldCorePlan(true)
	s := shelterOf(t, plan)
	a := childOf(t, plan, s, PlannedAirlock)
	if a.Parent != s.ID || a.ID == 0 || a.ID == s.ID {
		t.Fatalf("airlock %+v not a child of %d", a, s.ID)
	}
	if want := outside(s.Door, s.DoorRot); a.Vestibule() != want || a.Door != outside(want, s.DoorRot) || a.DoorRot != s.DoorRot {
		t.Fatalf("airlock %+v off door %v %v", a, s.Door, s.DoorRot)
	}
	if !slices.Contains(plan.ShellDoors(s), a.Door) {
		t.Fatal("the outer door is no shell door")
	}
	if !plan.Valid() {
		t.Fatal("plan with children is invalid")
	}
	if _, err := CheckRoutes(plan); err != nil {
		t.Fatal(err)
	}
}

func airlockInput(plan LayoutPlan, room PlannedRoom, ground GroundCensus) ReconcileInput {
	return ReconcileInput{Plan: plan, Room: room, Ground: ground, Rooms: RoomObservation{Shapes: testShapes}, WantedFloor: func(domain.Cell) string { return "" }}
}

func TestAirlockReconcileOwesWallsAndOuterDoor(t *testing.T) {
	in := Rectangle{X: 10, Z: 10, Width: 4, Height: 3}
	plan, room := shelterWithKids(in, domain.Cell{X: 40, Z: 11}, true, false)
	a := childOf(t, plan, room, PlannedAirlock)
	if room.DoorRot != domain.East || a.Vestibule() != (domain.Cell{X: 15, Z: 11}) || a.Door != (domain.Cell{X: 16, Z: 11}) {
		t.Fatalf("room %+v airlock %+v", room, a)
	}
	if got := a.AirlockWalls(); !slices.Equal(got, []domain.Cell{{X: 15, Z: 10}, {X: 15, Z: 12}}) {
		t.Fatalf("walls %v", got)
	}
	ring := ringWalls(plan, room)
	if plan.GroundMatches(room, ring) {
		t.Fatal("a ring without the airlock matches")
	}
	rec := Reconcile(airlockInput(plan, room, ring))
	if op := readyOp(t, rec, OpWallIn); len(op.Cells) != 2 {
		t.Fatalf("airlock walls owed: %+v", rec.Ready)
	}
	if op := readyOp(t, rec, OpDoorIn); len(op.Cells) != 1 || op.Cells[0] != a.Door {
		t.Fatalf("outer door owed: %+v", rec.Ready)
	}
	for _, c := range a.AirlockWalls() {
		ring.walls[c] = true
	}
	ring.doors[a.Door] = true
	if !plan.GroundMatches(room, ring) {
		t.Fatal("a built airlock does not match")
	}
	if rec := Reconcile(airlockInput(plan, room, ring)); len(rec.Owed) != 0 {
		t.Fatalf("complete airlock owes %+v", rec.Owed)
	}
	if got := PlannedGround(plan, GroundCensus{}); !slices.ContainsFunc(got, RectRegion(a.AirlockGround()).Equal) {
		t.Fatalf("airlock ground missing from %v", got)
	}
	// The child owes no ring of its own.
	if !plan.GroundMatches(a, GroundCensus{}) {
		t.Fatal("an airlock child owes a ring")
	}
}

// An airlock hangs off any door: here a kitchen's, facing south.
func TestAirlockAttachesToAnyParent(t *testing.T) {
	door := domain.Cell{X: 20, Z: 9}
	a := attachAirlock(door, domain.South)
	if a.Role != PlannedAirlock || a.Vestibule() != (domain.Cell{X: 20, Z: 8}) || a.Door != (domain.Cell{X: 20, Z: 7}) {
		t.Fatalf("airlock %+v", a)
	}
	if got := a.AirlockWalls(); !slices.Equal(got, []domain.Cell{{X: 19, Z: 8}, {X: 21, Z: 8}}) {
		t.Fatalf("walls %v", got)
	}
}

func TestTransposeKeepsTheAirlock(t *testing.T) {
	plan, room := shelterWithKids(Rectangle{X: 10, Z: 10, Width: 4, Height: 3}, domain.Cell{X: 40, Z: 11}, true, false)
	a := plan.Rooms[1]
	if again := transposeRoom(transposeRoom(a)); !again.Same(a) {
		t.Fatalf("%+v != %+v", again, a)
	}
	t1, tr := transposeRoom(a), transposeRoom(room)
	if t1.Vestibule() != outside(tr.Door, tr.DoorRot) || t1.Door != outside(t1.Vestibule(), t1.DoorRot) {
		t.Fatalf("transposed airlock %+v off door %v %v", t1, tr.Door, tr.DoorRot)
	}
}

func TestChildrenOfAndRetireWithParent(t *testing.T) {
	plan, room := shelterWithKids(Rectangle{X: 10, Z: 10, Width: 5, Height: 4}, domain.Cell{X: 40, Z: 11}, true, true)
	if kids := plan.ChildrenOf(room.ID); len(kids) != 2 || kids[0].Role != PlannedAirlock || kids[1].Role != PlannedPorch {
		t.Fatalf("children %+v", kids)
	}
	if plan.ChildrenOf(0) != nil || plan.ChildrenOf(plan.Rooms[1].ID) != nil {
		t.Fatal("children of no room or of a child")
	}
	for _, c := range plan.Rooms[1:] {
		if c.Parent != room.ID || !c.Unfenced() {
			t.Fatalf("child %+v", c)
		}
	}
	if !plan.Valid() {
		t.Fatal("invalid")
	}
	bad := plan
	bad.Rooms = slices.Clone(plan.Rooms)
	bad.Rooms[1].Parent = 0
	if bad.Valid() {
		t.Fatal("an orphan child is valid")
	}
	next, dropped := dropRooms(plan, map[int32]bool{room.ID: true})
	if !dropped || len(next.Rooms) != 0 {
		t.Fatalf("children outlive the parent: %+v", next.Rooms)
	}
}
