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
	return g.generate(LayoutPlan{Zones: zones, Cold: cold}, seed, 3, 0, TechTierCamp)
}

func TestColdShelterHasAnAirlockTemperateNone(t *testing.T) {
	if s := shelterOf(t, coldCorePlan(false)); s.Airlock != nil {
		t.Fatalf("temperate shelter has an airlock: %+v", s.Airlock)
	}
	plan := coldCorePlan(true)
	s := shelterOf(t, plan)
	if s.Airlock == nil {
		t.Fatal("cold shelter has no airlock")
	}
	if want := outside(s.Door, s.DoorRot); s.Airlock.Vestibule != want || s.Airlock.Outer != outside(want, s.DoorRot) {
		t.Fatalf("airlock %+v off door %v %v", s.Airlock, s.Door, s.DoorRot)
	}
	if !slices.Contains(plan.ShellDoors(s), s.Airlock.Outer) {
		t.Fatal("the outer door is no shell door")
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
	room := shelterRoom(in, domain.Cell{X: 40, Z: 11}, true, false)
	plan := LayoutPlan{Rooms: []PlannedRoom{room}}
	if room.DoorRot != domain.East || room.Airlock.Vestibule != (domain.Cell{X: 15, Z: 11}) || room.Airlock.Outer != (domain.Cell{X: 16, Z: 11}) {
		t.Fatalf("room %+v %+v", room, room.Airlock)
	}
	if got := room.AirlockWalls(); !slices.Equal(got, []domain.Cell{{X: 15, Z: 10}, {X: 15, Z: 12}}) {
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
	if op := readyOp(t, rec, OpDoorIn); len(op.Cells) != 1 || op.Cells[0] != room.Airlock.Outer {
		t.Fatalf("outer door owed: %+v", rec.Ready)
	}
	for _, c := range room.AirlockWalls() {
		ring.walls[c] = true
	}
	ring.doors[room.Airlock.Outer] = true
	if !plan.GroundMatches(room, ring) {
		t.Fatal("a built airlock does not match")
	}
	if rec := Reconcile(airlockInput(plan, room, ring)); len(rec.Owed) != 0 {
		t.Fatalf("complete airlock owes %+v", rec.Owed)
	}
	if got := PlannedGround(plan, GroundCensus{}); !slices.ContainsFunc(got, RectRegion(room.AirlockGround()).Equal) {
		t.Fatalf("airlock ground missing from %v", got)
	}
}

func TestTransposeKeepsTheAirlock(t *testing.T) {
	room := shelterRoom(Rectangle{X: 10, Z: 10, Width: 4, Height: 3}, domain.Cell{X: 40, Z: 11}, true, false)
	if again := transposeRoom(transposeRoom(room)); !again.Same(room) {
		t.Fatalf("%+v != %+v", again, room)
	}
	t1 := transposeRoom(room)
	if t1.Airlock.Vestibule != outside(t1.Door, t1.DoorRot) {
		t.Fatalf("transposed vestibule %+v off door %v %v", t1.Airlock, t1.Door, t1.DoorRot)
	}
}
