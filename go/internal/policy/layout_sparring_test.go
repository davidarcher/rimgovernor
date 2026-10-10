package policy

import (
	"os"
	"path/filepath"
	"reflect"
	"slices"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func sparPlan(t *testing.T, markers int) (LayoutPlan, PlannedRoom) {
	t.Helper()
	plan := outskirtsPlan(150)
	if _, added := growRings(plan, RoomDemand{}); added {
		t.Fatal("a ring grown with none owed")
	}
	next, added := growRings(plan, RoomDemand{Rings: markers})
	if !added || len(next.RingRooms()) != 1 {
		t.Fatalf("ring not sited: %d", len(next.RingRooms()))
	}
	return next, next.RingRooms()[0]
}

func TestRingMarkersScaleWithCapableAdults(t *testing.T) {
	for capable, want := range map[int]int{0: 4, 4: 4, 7: 7, 12: 12, 40: 12} {
		if got := RingMarkersFor(capable); got != want {
			t.Errorf("%d capable adults: %d markers, want %d", capable, got, want)
		}
	}
}

func TestRingIsAFixedUnfencedSquareBesideTheRange(t *testing.T) {
	plan, _ := rangePlan(t, 4)
	rng := plan.RangeRooms()[0]
	next, added := growRings(plan, RoomDemand{Rings: 6})
	if !added {
		t.Fatal("ring not sited")
	}
	ring := next.RingRooms()[0]
	if !ring.Outdoor || !ring.Unfenced() || ring.Interior.Width != RingSide || ring.Interior.Height != RingSide || ring.Markers != 6 {
		t.Fatalf("ring %+v", ring)
	}
	if rectsOverlap(ringBlock(ring), rangeBlock(rng)) {
		t.Fatalf("ring %+v overlaps the range backdrop %+v", ringBlock(ring), rangeBlock(rng))
	}
	for _, other := range next.AllRooms() {
		if other.Role != PlannedSparringRing && rectsOverlap(roomWalls(other), roomWalls(ring)) {
			t.Fatalf("ring overlaps %s", other.Role)
		}
	}
	if RoomTier(PlannedSparringRing) != domain.TierExpand {
		t.Fatal("ring tier")
	}
	if _, again := growRings(next, RoomDemand{Rings: 6}); again {
		t.Fatal("ring grown twice")
	}
	if _, again := growRings(next, RoomDemand{Rings: 2}); again {
		t.Fatal("ring shrunk for a smaller demand")
	}
}

func TestRingGrowsByMarkersWithoutMoving(t *testing.T) {
	plan, ring := sparPlan(t, 4)
	if ring.Markers != 4 {
		t.Fatalf("markers %d", ring.Markers)
	}
	before := slices.Clone(plan.Rooms)
	slotsBefore := RingTemplate(ring)
	grown, added := growRings(plan, RoomDemand{Rings: 9})
	if !added {
		t.Fatal("ring not grown")
	}
	after := grown.RingRooms()[0]
	if after.Markers != 9 || after.Interior != ring.Interior {
		t.Fatalf("grown ring %+v from %+v", after, ring)
	}
	for i, r := range before {
		if r.Role != PlannedSparringRing && !reflect.DeepEqual(r, grown.Rooms[i]) {
			t.Fatal("another room moved")
		}
	}
	slotsAfter := RingTemplate(after)
	if len(slotsAfter) != 9 || !reflect.DeepEqual(slotsBefore, slotsAfter[:4]) {
		t.Fatal("standing markers moved")
	}
	capped, _ := growRings(grown, RoomDemand{Rings: 99})
	if got := capped.RingRooms()[0].Markers; got != RingMaxMarkers {
		t.Fatalf("markers %d past the cap", got)
	}
	if RingsOwed(capped, RoomDemand{Rings: 99}) != 0 {
		t.Fatal("still owed at the cap")
	}
}

func TestRingTemplatePlacesOneMarkerPerSlotOnTheInnerEdge(t *testing.T) {
	room := ringFromBlock(Rectangle{X: 20, Z: 30, Width: 11, Height: 11}, RingMaxMarkers)
	pieces := RingTemplate(room)
	if len(pieces) != RingMaxMarkers {
		t.Fatalf("%d pieces", len(pieces))
	}
	seen := map[domain.Cell]bool{}
	slots := map[string]bool{}
	in := room.Interior
	for _, p := range pieces {
		c := p.Minimum
		onEdge := c.X == in.X || c.X == in.X+in.Width-1 || c.Z == in.Z || c.Z == in.Z+in.Height-1
		corner := (c.X == in.X || c.X == in.X+in.Width-1) && (c.Z == in.Z || c.Z == in.Z+in.Height-1)
		if p.DefName != RingMarkerDef || p.Minimum != p.Maximum || seen[c] || slots[p.Slot] || !contains(in, c) || !onEdge || corner {
			t.Fatalf("bad piece %+v", p)
		}
		seen[c], slots[p.Slot] = true, true
	}
	if len(RingTemplate(PlannedRoom{Role: PlannedSparringRing, Interior: in, Markers: 5})) != 5 {
		t.Fatal("template ignores Markers")
	}
}

func TestRingIsLeftOutWhenNothingFits(t *testing.T) {
	plan := outskirtsPlan(150)
	plan.Rooms = append(slices.Clone(plan.Rooms), PlannedRoom{Role: PlannedWorkshop, Interior: Rectangle{X: 0, Z: 0, Width: 400, Height: 400}})
	if next, added := growRings(plan, RoomDemand{Rings: 5}); added || len(next.RingRooms()) != 0 {
		t.Fatal("a ring sited with no room left")
	}
}

func TestSparringMarkerDefIsDefinedByTheNativeMod(t *testing.T) {
	things, err := os.ReadFile(filepath.Join("..", "..", "..", "integrations", "rimgovernor-native", "Defs", "ThingDefs", "SparringMarker.xml"))
	if err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(string(things), "<defName>"+RingMarkerDef+"</defName>") {
		t.Fatal("ThingDef missing from the native mod", RingMarkerDef)
	}
}
