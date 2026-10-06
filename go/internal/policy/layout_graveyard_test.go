package policy

import (
	"fmt"
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func graveyardPlan(t *testing.T) (LayoutPlan, PlannedRoom) {
	t.Helper()
	plan, _ := growOutskirts(outskirtsPlan(150), OutskirtsSize())
	plan, _ = growOutskirtsRooms(plan, nil)
	rooms := plan.roomsOf(PlannedGraveyard)
	if len(rooms) != 1 {
		t.Fatalf("graveyards %d", len(rooms))
	}
	return plan, rooms[0]
}

func TestGraveyardIsAnOutdoorRoomInTheOutskirtsSlot(t *testing.T) {
	plan, room := graveyardPlan(t)
	area, _ := plan.OutskirtsArea()
	l, _ := OutskirtsSlots(area)
	if room.Interior != l.Graveyard.Interior || room.Door != l.Graveyard.Door || room.DoorRot != l.Graveyard.DoorRot || !room.Outdoor {
		t.Fatalf("graveyard %+v want slot %+v outdoor", room, l.Graveyard)
	}
	if !contains(area, domain.Cell{X: room.Interior.X, Z: room.Interior.Z}) {
		t.Fatal("graveyard off the cluster")
	}
	if wall, door := room.RingDefs(); wall != PenFenceDefinition || door != PenGateDefinition {
		t.Fatalf("ring %s %s", wall, door)
	}
	if again, added := growOutskirtsRooms(plan, nil); added || len(again.Rooms) != len(plan.Rooms) {
		t.Fatal("graveyard grown twice")
	}
	// A cluster holding a tomb and a morgue only is still owed its graveyard.
	partial := plan
	partial.Rooms = slices.DeleteFunc(slices.Clone(plan.Rooms), func(r PlannedRoom) bool { return r.Role == PlannedGraveyard })
	if !OutskirtsOwed(partial) || OutskirtsOwed(plan) {
		t.Fatal("OutskirtsOwed ignores the graveyard")
	}
}

func TestGraveyardRingIsFenceAndGateWithNoFloorOwed(t *testing.T) {
	plan, room := graveyardPlan(t)
	if plan.GroundMatches(room, GroundOf(nil)) {
		t.Fatal("an unbuilt graveyard matches")
	}
	built := penRing(t, plan, room)
	if !plan.GroundMatches(room, GroundOf(built)) {
		t.Fatal("a fenced and gated graveyard does not match")
	}
	// Walls and a door round it are not its ring.
	ring := roomWalls(room)
	var walled []CurrentBuilding
	for _, c := range rectCells(ring) {
		if onRing(c, ring) {
			walled = append(walled, penBuilding(t, ShellWallDefinition, c))
		}
	}
	if plan.GroundMatches(room, GroundOf(walled)) {
		t.Fatal("a walled graveyard matches")
	}
	in := ReconcileInput{Plan: plan, Room: room, Ground: GroundOf(nil), WantedFloor: func(domain.Cell) string { return "(wanted)" }}
	rec := Reconcile(in)
	fenced, gated := map[domain.Cell]bool{}, map[domain.Cell]bool{}
	for _, op := range append(slices.Clone(rec.Ready), rec.Owed...) {
		switch op.Kind {
		case OpWallIn:
			for _, c := range op.Cells {
				fenced[c] = true
			}
		case OpDoorIn:
			for _, c := range op.Cells {
				gated[c] = true
			}
		case OpFloorIn:
			t.Fatalf("a graveyard owes a floor: %+v", op)
		}
	}
	if len(gated) != 1 || !gated[room.Door] || len(fenced) != int(2*(room.Interior.Width+2)+2*room.Interior.Height)-1 {
		t.Fatalf("fences %d gates %d", len(fenced), len(gated))
	}
	in.Ground = GroundOf(built)
	if rec := Reconcile(in); len(rec.Ready)+len(rec.Owed) != 0 {
		t.Fatalf("a standing ring owes %+v", rec)
	}
}

func TestGraveyardTemplateHolds12ReachableGraves(t *testing.T) {
	_, room := graveyardPlan(t)
	slots := GraveyardSlots(room.Interior)
	if len(slots) != GraveyardGraves {
		t.Fatalf("slots %d", len(slots))
	}
	grave := map[domain.Cell]int{}
	for i, s := range slots {
		if s.Width != 1 || s.Height != 2 {
			t.Fatalf("grave %+v is not 1x2", s)
		}
		for _, c := range rectCells(s) {
			if !contains(room.Interior, c) {
				t.Fatalf("grave %+v off the interior", s)
			}
			if _, taken := grave[c]; taken {
				t.Fatalf("grave %+v overlaps another", s)
			}
			grave[c] = i
		}
	}
	// Flood the open interior from just inside the gate.
	open := map[domain.Cell]bool{}
	queue := []domain.Cell{room.gateCell()}
	if !contains(room.Interior, queue[0]) {
		t.Fatalf("gate cell %+v outside the interior", queue[0])
	}
	open[queue[0]] = true
	for len(queue) > 0 {
		c := queue[0]
		queue = queue[1:]
		for _, n := range []domain.Cell{{X: c.X + 1, Z: c.Z}, {X: c.X - 1, Z: c.Z}, {X: c.X, Z: c.Z + 1}, {X: c.X, Z: c.Z - 1}} {
			if _, taken := grave[n]; contains(room.Interior, n) && !taken && !open[n] {
				open[n] = true
				queue = append(queue, n)
			}
		}
	}
	for i, s := range slots {
		// Reachable along a long side, where a hauler stands to inter a corpse.
		side := false
		for _, c := range rectCells(s) {
			side = side || open[domain.Cell{X: c.X - 1, Z: c.Z}] || open[domain.Cell{X: c.X + 1, Z: c.Z}]
		}
		if !side {
			t.Errorf("grave %d %+v has no aisle beside its long side", i, s)
		}
	}
	if GraveyardSlots(Rectangle{Width: 4, Height: 4}) != nil {
		t.Fatal("a template in a room too small")
	}
}

// A fenced, unroofed graveyard adds only its own ring to the colony extent: no
// enclosed interior, and no cell between it and the base (colony-extent.md).
func TestGraveyardFenceAddsOnlyItsRingToTheColonyExtent(t *testing.T) {
	plan, room := graveyardPlan(t)
	var cells []domain.Cell
	for _, b := range penRing(t, plan, room) {
		cells = append(cells, b.Cells...)
	}
	r := extentFixture(t, cells[:1]...)
	census, home := CurrentConstruction{Colony: true}, HomeCoverageObservation{}
	for i, c := range cells {
		id := fmt.Sprintf("fence-%d", i)
		b, _ := domain.NewBuilding(PenFenceDefinition, c, domain.North, "")
		census.Buildings = append(census.Buildings, CurrentBuilding{ID: id, Building: b, Cells: []domain.Cell{c}})
		home.Targets = append(home.Targets, HomeCoverageTarget{ID: id, Cells: []domain.Cell{c}, Shape: domain.Known("shape"), Missing: domain.Known(int64(0)), Excluded: domain.Known(int64(0)), ExtentGeometry: domain.Known(HomeExtentGeometry{})})
	}
	r.Bounds, r.Construction, r.Home = domain.Known(Bounds{Width: 300, Height: 300}), domain.Known(census), domain.Known(home)
	got, err := DeriveColonyExtent(r)
	e, known := got.Value()
	if err != nil || !known || len(e.Regions) != 1 {
		t.Fatalf("extent %+v known=%v err=%v", e, known, err)
	}
	for _, c := range rectCells(room.Interior) {
		if len(extentReasons(e, c)) != 0 {
			t.Fatalf("the open interior %+v is in the extent", c)
		}
	}
	for _, c := range cells {
		if !slices.ContainsFunc(extentReasons(e, c), func(p ExtentProvenance) bool { return p.Origin == ExtentFacility }) {
			t.Fatalf("ring cell %+v is no facility", c)
		}
	}
}
