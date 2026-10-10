package policy

import (
	"reflect"
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
	if room.Interior != l.Graveyard.Interior || !room.Outdoor || !room.Unfenced() || room.Door != (domain.Cell{}) {
		t.Fatalf("graveyard %+v want unfenced slot %+v", room, l.Graveyard)
	}
	if !contains(area, domain.Cell{X: room.Interior.X, Z: room.Interior.Z}) {
		t.Fatal("graveyard off the cluster")
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

// An unfenced graveyard owes no ring, gate, door or floor: nothing is built.
func TestGraveyardOwesNoRingGateOrFloor(t *testing.T) {
	plan, room := graveyardPlan(t)
	if !plan.GroundMatches(room, GroundOf(nil)) || len(plan.ShellDoors(room)) != 0 {
		t.Fatal("an unfenced graveyard owes a ring or a door")
	}
	rec := Reconcile(ReconcileInput{Plan: plan, Room: room, Ground: GroundOf(nil), WantedFloor: func(domain.Cell) string { return "(wanted)" }})
	for _, op := range append(slices.Clone(rec.Ready), rec.Owed...) {
		if op.Kind == OpWallIn || op.Kind == OpDoorIn || op.Kind == OpFloorIn {
			t.Fatalf("a graveyard owes %+v", op)
		}
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
	// Flood the open interior from its south-west corner (an aisle cell).
	open := map[domain.Cell]bool{}
	queue := []domain.Cell{{X: room.Interior.X, Z: room.Interior.Z}}
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

func TestFurtherGraveyardIsSitedOffCoreClearOfLivingRooms(t *testing.T) {
	plan, first := graveyardPlan(t)
	if _, added := growGraveyards(plan, RoomDemand{Graveyards: 1}); added {
		t.Fatal("a graveyard grown with none owed")
	}
	before := slices.Clone(plan.Rooms)
	next, added := growGraveyards(plan, RoomDemand{Graveyards: 2})
	if !added || len(next.roomsOf(PlannedGraveyard)) != 2 {
		t.Fatalf("further graveyard not sited: %d", len(next.roomsOf(PlannedGraveyard)))
	}
	if !reflect.DeepEqual(before, next.Rooms[:len(before)]) || next.roomsOf(PlannedGraveyard)[0].Interior != first.Interior {
		t.Fatal("an existing room moved")
	}
	room := next.roomsOf(PlannedGraveyard)[1]
	if !room.Outdoor || room.Interior.Width != GraveyardW || room.Interior.Height != GraveyardH || len(GraveyardSlots(room.Interior)) != GraveyardGraves {
		t.Fatalf("further graveyard %+v", room)
	}
	if !room.Unfenced() {
		t.Fatal("further graveyard is fenced")
	}
	for _, other := range plan.AllRooms() {
		if other.Role == PlannedTomb || other.Role == PlannedMorgue || other.Role == PlannedWasteYard || other.Role == PlannedGraveyard || other.Role == PlannedIncinerator {
			continue
		}
		if rectsOverlap(pad(roomWalls(other), outskirtsGap), roomWalls(room)) {
			t.Fatalf("further graveyard within %d cells of %s", outskirtsGap, other.Role)
		}
	}
	if GraveyardsOwed(next, RoomDemand{Graveyards: 2}) != 0 {
		t.Fatal("still owed")
	}
}
