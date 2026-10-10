package policy

import (
	"fmt"
	"reflect"
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func rangePlan(t *testing.T, stands int) (LayoutPlan, PlannedRoom) {
	t.Helper()
	plan := outskirtsPlan(150)
	if _, added := growRanges(plan, RoomDemand{}); added {
		t.Fatal("a range grown with none owed")
	}
	before := slices.Clone(plan.Rooms)
	next, added := growRanges(plan, RoomDemand{Ranges: stands})
	if !added || len(next.RangeRooms()) != 1 {
		t.Fatalf("range not sited: %d", len(next.RangeRooms()))
	}
	if !reflect.DeepEqual(before, next.Rooms[:len(before)]) {
		t.Fatal("an existing room moved")
	}
	return next, next.RangeRooms()[0]
}

func TestRangeStandsScaleWithCapableAdults(t *testing.T) {
	for capable, want := range map[int]int{0: 2, 1: 2, 3: 2, 4: 2, 5: 3, 8: 4, 9: 5, 15: 8, 16: 8, 40: 8} {
		if got := RangeStandsFor(capable); got != want {
			t.Errorf("%d capable adults: %d stands, want %d", capable, got, want)
		}
	}
}

func TestRangeIsAnUnfencedOutdoorRoomNearTheColonyFacingAwayFromTheCore(t *testing.T) {
	plan, room := rangePlan(t, 4)
	if !room.Outdoor || !room.Unfenced() || room.Role != PlannedTrainingRange {
		t.Fatalf("range %+v", room)
	}
	if RangeStandCount(room) != 4 {
		t.Fatalf("stands %d", RangeStandCount(room))
	}
	if room.rangeHorizontal() && room.Interior.Height != RangeDistance+1 || !room.rangeHorizontal() && room.Interior.Width != RangeDistance+1 {
		t.Fatalf("lane %+v", room.Interior)
	}
	ext, _ := outskirtsPlan(150).CoreBounds() // before the range joined the plan
	side, _, beyond := sideOf(ext, rangeBlock(room))
	if !beyond {
		t.Fatalf("range %+v overlaps the core extent %+v", rangeBlock(room), ext)
	}
	if room.Facing != side {
		t.Fatalf("range on the %s side of the core faces %s, want away from the core", side, room.Facing)
	}
	// Nearer than the outskirts clearance, and clear of every other room's walls.
	gap := rangeClearance(plan, room)
	if gap < rangeGap {
		t.Fatalf("range %d cells from the nearest room, want at least %d", gap, rangeGap)
	}
	for _, other := range plan.AllRooms() {
		if other.Role != PlannedTrainingRange && rectsOverlap(roomWalls(other), roomWalls(room)) {
			t.Fatalf("range overlaps %s", other.Role)
		}
	}
	if RangesOwed(plan, RoomDemand{Ranges: 4}) != 0 {
		t.Fatal("still owed")
	}
	if _, added := growRanges(plan, RoomDemand{Ranges: 4}); added {
		t.Fatal("range grown twice")
	}
	if _, added := growRanges(plan, RoomDemand{Ranges: 2}); added {
		t.Fatal("range shrunk or regrown for a smaller demand")
	}
	if RoomTier(PlannedTrainingRange) != domain.TierExpand {
		t.Fatal("range tier")
	}
}

// rangeClearance is the smallest gap, in cells, from the range's blocked
// footprint to another room's walls (a large number when it is alone).
func rangeClearance(plan LayoutPlan, room PlannedRoom) (gap int32) {
	gap = 1 << 20
	b := rangeBlock(room)
	for _, other := range plan.AllRooms() {
		if other.Role == PlannedTrainingRange {
			continue
		}
		o := roomWalls(other)
		dx := max(o.X-(b.X+b.Width), b.X-(o.X+o.Width), 0)
		dz := max(o.Z-(b.Z+b.Height), b.Z-(o.Z+o.Height), 0)
		gap = min(gap, max(dx, dz))
	}
	return gap
}

func TestRangeLayoutIsOneRowOfStandsAndADummyRowFacingThem(t *testing.T) {
	for _, facing := range []domain.Rotation{domain.North, domain.South, domain.East, domain.West} {
		room := rangeFromBlock(Rectangle{X: 20, Z: 30, Width: 20, Height: 20}, facing, 5)
		pieces := RangeLayout(room)
		if len(pieces) != 10 {
			t.Fatalf("%s: %d pieces", facing, len(pieces))
		}
		seen := map[domain.Cell]bool{}
		var stands, dummies []domain.Cell
		for _, p := range pieces {
			if seen[p.Cell] || !contains(room.Interior, p.Cell) {
				t.Fatalf("%s: piece off the interior or doubled: %+v", facing, p)
			}
			seen[p.Cell] = true
			if p.Kind == RangeStand {
				stands = append(stands, p.Cell)
			} else {
				dummies = append(dummies, p.Cell)
			}
		}
		for i := range stands {
			d := domain.Cell{X: dummies[i].X - stands[i].X, Z: dummies[i].Z - stands[i].Z}
			want := map[domain.Rotation]domain.Cell{domain.North: {Z: RangeDistance}, domain.South: {Z: -RangeDistance}, domain.East: {X: RangeDistance}, domain.West: {X: -RangeDistance}}[facing]
			if d != want {
				t.Fatalf("%s: dummy %d lies %+v from its stand, want %+v", facing, i, d, want)
			}
			if i > 0 {
				step := domain.Cell{X: stands[i].X - stands[i-1].X, Z: stands[i].Z - stands[i-1].Z}
				if step.X+step.Z != 1 || step.X*step.Z != 0 {
					t.Fatalf("%s: stands %d and %d are not side by side with no gap: %+v", facing, i-1, i, step)
				}
			}
		}
		// The backdrop behind the dummies is part of the blocked footprint and
		// holds no piece.
		block := rangeBlock(room)
		for _, d := range dummies {
			behind := d
			switch facing {
			case domain.North:
				behind.Z += RangeBackdrop
			case domain.South:
				behind.Z -= RangeBackdrop
			case domain.East:
				behind.X += RangeBackdrop
			case domain.West:
				behind.X -= RangeBackdrop
			}
			if !contains(block, behind) {
				t.Fatalf("%s: cell %d behind a dummy is outside the blocked footprint", facing, RangeBackdrop)
			}
		}
		if !contains(block, domain.Cell{X: block.X, Z: block.Z}) || block.Width*block.Height == 0 {
			t.Fatal("empty block")
		}
	}
}

// A site beside the core (map edge, cliff or water ahead of it) faces along the
// axis of its rows toward the end farther from the core, never back at the core.
func TestRangeFacingFallsBackAcrossTheCore(t *testing.T) {
	ext := Rectangle{X: 100, Z: 100, Width: 20, Height: 20}
	east := Rectangle{X: 130, Z: 125, Width: 6, Height: 15}
	if got := rangeFacing(domain.East, true, east, ext); got != domain.North {
		t.Errorf("east site above the core centre, rows along X: faces %s, want north", got)
	}
	if got := rangeFacing(domain.East, true, Rectangle{X: 130, Z: 90, Width: 6, Height: 15}, ext); got != domain.South {
		t.Errorf("east site below the core centre, rows along X: faces %s, want south", got)
	}
	north := Rectangle{X: 125, Z: 130, Width: 15, Height: 6}
	if got := rangeFacing(domain.North, false, north, ext); got != domain.East {
		t.Errorf("north site right of the core centre, rows along Z: faces %s, want east", got)
	}
	if got := rangeFacing(domain.North, true, north, ext); got != domain.North {
		t.Errorf("north site with rows along X: faces %s, want north (away)", got)
	}
	if got := rangeFacing(domain.West, false, east, ext); got != domain.West {
		t.Errorf("west site with rows along Z: faces %s, want west (away)", got)
	}
}

func TestRangeGrowsOnlyAndKeepsItsPieces(t *testing.T) {
	plan, room := rangePlan(t, 2)
	before := map[domain.Cell]RangePieceKind{}
	for _, p := range RangeLayout(room) {
		before[p.Cell] = p.Kind
	}
	grown, added := growRanges(plan, RoomDemand{Ranges: 5})
	if !added {
		t.Fatal("range not widened")
	}
	wide := grown.RangeRooms()[0]
	if n := RangeStandCount(wide); n <= 2 || n > 5 {
		t.Fatalf("widened to %d stands", n)
	}
	if wide.Facing != room.Facing || len(grown.RangeRooms()) != 1 {
		t.Fatal("widening moved or duplicated the range")
	}
	after := map[domain.Cell]RangePieceKind{}
	for _, p := range RangeLayout(wide) {
		after[p.Cell] = p.Kind
	}
	for c, kind := range before {
		if after[c] != kind {
			t.Fatalf("piece at %+v moved when the range widened", c)
		}
	}
	// A cap, and no shrinking on a smaller demand.
	capped, _ := growRanges(grown, RoomDemand{Ranges: 40})
	if n := RangeStandCount(capped.RangeRooms()[0]); n > RangeMaxStands {
		t.Fatalf("%d stands, cap %d", n, RangeMaxStands)
	}
	if _, shrunk := growRanges(grown, RoomDemand{Ranges: 2}); shrunk {
		t.Fatal("range shrank")
	}
}

func TestRangeTemplateReconcilesWithNoRing(t *testing.T) {
	plan, room := rangePlan(t, 4)
	template := RangeTemplate(room)
	if len(template) != 8 {
		t.Fatalf("template %d", len(template))
	}
	in := ReconcileInput{Plan: plan, Room: room, Ground: GroundCensus{}, Furniture: template}
	ops := ReconcileRoom(in)
	if len(ops) != 1 || ops[0].Kind != OpBuild || len(ops[0].Pieces) != len(template) {
		t.Fatalf("an unbuilt range owes only its pieces: %+v", ops)
	}
	defs := map[string]int{}
	for _, p := range ops[0].Pieces {
		defs[p.DefName]++
	}
	if defs[RangeDefNames[RangeStand]] != 4 || defs[RangeDefNames[RangeDummy]] != 4 {
		t.Fatal(defs)
	}
	if !plan.GroundMatches(room, GroundCensus{}) || len(plan.ShellDoors(room)) != 0 {
		t.Fatal("an unfenced range owes a ring or a door")
	}
	// Standing pieces owe nothing; a stray building on the lane comes down.
	var rows []ClearanceTarget
	for _, p := range template {
		rows = append(rows, ClearanceTarget{EntityID: fmt.Sprint(len(rows) + 1), DefName: p.DefName, Minimum: p.Minimum, Maximum: p.Maximum, Player: true})
	}
	in.Rows = rows
	if ops := ReconcileRoom(in); len(ops) != 0 {
		t.Fatalf("built range owes %+v", ops)
	}
	pair := RangeLayout(room)
	stand, dummy := pair[0].Cell, pair[1].Cell
	lane := domain.Cell{X: stand.X + (dummy.X-stand.X)/2, Z: stand.Z + (dummy.Z-stand.Z)/2}
	in.Rows = append(slices.Clone(rows), ClearanceTarget{EntityID: "99", DefName: "Wall", Minimum: lane, Maximum: lane, Player: true})
	if ops := ReconcileRoom(in); len(ops) != 1 || ops[0].Kind != OpFurnitureOut {
		t.Fatalf("stray building on the lane: %+v", ops)
	}
}

func TestRangeRendersInTheOverlayWithoutADoor(t *testing.T) {
	plan, room := rangePlan(t, 3)
	overlay := plan.Overlay(Bounds{Width: 220, Height: 220})
	var outline *OverlayLayer
	for i, l := range overlay.Layers {
		if l.Label == roomOverlay[PlannedTrainingRange].label {
			outline = &overlay.Layers[i]
		}
	}
	if outline == nil || len(outline.Rects) != 1 || outline.Rects[0] != room.Interior {
		t.Fatalf("range outline %+v, want the interior %+v", outline, room.Interior)
	}
	for _, l := range overlay.Layers {
		if l.Label == "door" {
			for _, run := range l.Runs {
				if run.Z == 0 && run.X == 0 {
					t.Fatal("the range is drawn with a door at the origin")
				}
			}
		}
	}
}
