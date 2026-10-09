package policy

import (
	"reflect"
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func rangePlan(t *testing.T) (LayoutPlan, PlannedRoom) {
	t.Helper()
	plan, _ := growOutskirts(outskirtsPlan(150), OutskirtsSize())
	plan, _ = growOutskirtsRooms(plan, nil)
	if _, added := growRanges(plan, RoomDemand{}); added {
		t.Fatal("a range grown with none owed")
	}
	before := slices.Clone(plan.Rooms)
	next, added := growRanges(plan, RoomDemand{Ranges: 1})
	if !added || len(next.RangeRooms()) != 1 {
		t.Fatalf("range not sited: %d", len(next.RangeRooms()))
	}
	if !reflect.DeepEqual(before, next.Rooms[:len(before)]) {
		t.Fatal("an existing room moved")
	}
	return next, next.RangeRooms()[0]
}

func TestRangeIsSitedInTheOutskirtsClearOfLivingRooms(t *testing.T) {
	plan, room := rangePlan(t)
	if room.Outdoor || room.Interior.Width != RangeWidth || room.Interior.Height != RangeLaneLength {
		t.Fatalf("range %+v", room)
	}
	if wall, door := room.RingDefs(); wall != ShellWallDefinition || door != ShellDoorDefinition {
		t.Fatalf("ring %s %s", wall, door)
	}
	// The door is in the west wall and opens onto an interior cell.
	if room.DoorRot != domain.West || room.Door.X != room.Interior.X-1 || !contains(room.Interior, domain.Cell{X: room.Door.X + 1, Z: room.Door.Z}) {
		t.Fatalf("door %+v rot %v", room.Door, room.DoorRot)
	}
	for _, other := range plan.AllRooms() {
		switch other.Role {
		case PlannedTomb, PlannedMorgue, PlannedWasteYard, PlannedGraveyard, PlannedIncinerator, PlannedTrainingRange:
			if other.Role != PlannedTrainingRange && rectsOverlap(roomWalls(other), roomWalls(room)) {
				t.Fatalf("range overlaps %s", other.Role)
			}
			continue
		}
		if rectsOverlap(pad(roomWalls(other), outskirtsGap), roomWalls(room)) {
			t.Fatalf("range within %d cells of %s", outskirtsGap, other.Role)
		}
	}
	if RangesOwed(plan, RoomDemand{Ranges: 1}) != 0 {
		t.Fatal("still owed")
	}
	if _, added := growRanges(plan, RoomDemand{Ranges: 1}); added {
		t.Fatal("range grown twice")
	}
	if RoomTier(PlannedTrainingRange) != domain.TierExpand {
		t.Fatal("range tier")
	}
	if _, ok := roomOverlay[PlannedTrainingRange]; !ok {
		t.Fatal("range has no overlay style")
	}
}

func TestRangeTemplateReconcilesTheLanes(t *testing.T) {
	plan, room := rangePlan(t)
	template := RangeTemplate(room)
	if len(template) != 2*RangeLanes+(RangeLanes-1)*RangeLaneLength {
		t.Fatalf("template %d", len(template))
	}
	for _, p := range template {
		if !contains(room.Interior, p.Minimum) || p.Minimum != p.Maximum {
			t.Fatal("piece outside the interior", p)
		}
		if p.Minimum == room.gateCell() {
			t.Fatal("piece blocks the door", p)
		}
	}
	in := ReconcileInput{Plan: plan, Room: room, Ground: GroundCensus{}, Rooms: tombStanding(room), Furniture: template}
	if ops := ReconcileRoom(in); len(ops) == 0 || ops[0].Kind != OpWallIn {
		t.Fatalf("unbuilt ring: %+v", ops)
	}
	in.Ground = ringWalls(plan, room)
	ops := ReconcileRoom(in)
	if len(ops) != 1 || ops[0].Kind != OpBuild || len(ops[0].Pieces) != len(template) {
		t.Fatalf("build on site: %+v", ops)
	}
	defs := map[string]int{}
	for _, p := range ops[0].Pieces {
		defs[p.DefName]++
	}
	if defs[RangeDefNames[RangeStand]] != RangeLanes || defs[RangeDefNames[RangeDummy]] != RangeLanes || defs[RangeDefNames[RangePartition]] != (RangeLanes-1)*RangeLaneLength {
		t.Fatal(defs)
	}
}
