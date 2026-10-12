package policy

import (
	"reflect"
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// roofPlan is two rooms opening north onto a hallway (rows 10..12, x 10..30)
// and a pen that overlaps the hallway's far end.
func roofPlan() LayoutPlan {
	kitchen := PlannedRoom{ID: 1, Role: PlannedKitchen, Region: RectRegion(Rectangle{X: 10, Z: 14, Width: 4, Height: 4}), Door: domain.Cell{X: 11, Z: 13}, DoorRot: domain.South}
	freezer := PlannedRoom{ID: 2, Role: PlannedFreezer, Region: RectRegion(Rectangle{X: 16, Z: 14, Width: 4, Height: 4}), Door: domain.Cell{X: 17, Z: 13}, DoorRot: domain.South}
	pen := PlannedRoom{ID: 3, Role: PlannedPen, Outdoor: true, Region: RectRegion(Rectangle{X: 28, Z: 9, Width: 5, Height: 5}), Door: domain.Cell{X: 27, Z: 11}, DoorRot: domain.West}
	return LayoutPlan{
		Spine:     []SpineSegment{{From: domain.Cell{X: 10, Z: 11}, To: domain.Cell{X: 30, Z: 11}}},
		Rooms:     []PlannedRoom{kitchen, freezer, pen},
		Entrances: []domain.Cell{{X: 10, Z: 11}}, NextRoomID: 4,
	}
}

func roofInput(curve []float64) RoofInput {
	return RoofInput{Curve: domain.Known(curve), Climate: testClimate(), RoofDistance: testRoof}
}

func hasRoofReason(r RoofResult, reason string) bool {
	return slices.ContainsFunc(r.Decisions, func(d RoofDecision) bool { return d.Reason == reason })
}

func TestRoofedSetCoversRoomsAndPaidHallwaysAndSkipsOutdoorRoles(t *testing.T) {
	p := roofPlan()
	got := PlanRoofed(p, roofInput(seaIce))
	for _, r := range p.Rooms[:2] {
		for _, c := range r.Region.Cells() {
			if !got.Roofed.Contains(c) {
				t.Fatalf("room %d cell %v is not roofed", r.ID, c)
			}
		}
	}
	for x := int32(10); x < 28; x++ {
		for z := int32(10); z <= 12; z++ {
			if !got.Roofed.Contains(domain.Cell{X: x, Z: z}) {
				t.Fatalf("hallway cell (%d,%d) is not roofed: two doors open onto it for one entrance", x, z)
			}
		}
	}
	for _, c := range p.Rooms[2].Region.Cells() {
		if got.Roofed.Contains(c) {
			t.Fatalf("pen cell %v is roofed", c)
		}
	}
	p.Roofed = got.Roofed
	if !p.Valid() {
		t.Fatal("the plan with its roofed set is not valid")
	}
}

func TestRoofedSetReportsUnsupportedCellsWithoutDroppingThem(t *testing.T) {
	got := PlanRoofed(roofPlan(), roofInput(seaIce))
	var far []domain.Cell
	for _, d := range got.Decisions {
		if d.Reason == ReasonRoofUnsupported {
			far = d.Cells
		}
	}
	if len(far) == 0 {
		t.Fatal("hallway cells 20+ cells from every wall are not reported")
	}
	for _, c := range far {
		if !got.Roofed.Contains(c) {
			t.Errorf("unsupported cell %v was dropped from the set", c)
		}
	}
	if !slices.IsSortedFunc(far, func(a, b domain.Cell) int {
		if cellLess(a, b) {
			return -1
		}
		return 1
	}) {
		t.Error("unsupported cells are not in cell order")
	}
	// Every roofed cell is within the distance of a wall or reported.
	walls := roofPlan().Rooms[0].Region.Walls().Cells()
	walls = append(walls, roofPlan().Rooms[1].Region.Walls().Cells()...)
	for _, c := range got.Roofed.Cells() {
		near := slices.ContainsFunc(walls, func(w domain.Cell) bool {
			dx, dz := float64(c.X-w.X), float64(c.Z-w.Z)
			return dx*dx+dz*dz <= testRoof*testRoof
		})
		if !near && !slices.Contains(far, c) {
			t.Fatalf("cell %v is neither supported nor reported", c)
		}
	}
	in := roofInput(seaIce)
	in.RoofDistance = 0
	if !hasRoofReason(PlanRoofed(roofPlan(), in), ReasonRoofSupportless) {
		t.Error("an unknown support distance is not reported")
	}
}

func TestRoofedSetIsEmptyOnATemperateCurve(t *testing.T) {
	got := PlanRoofed(roofPlan(), roofInput(temperate))
	if !got.Roofed.Empty() || !hasRoofReason(got, ReasonRoofTemperate) {
		t.Fatalf("temperate curve roofs %d cells: %+v", got.Roofed.Area(), got.Decisions)
	}
	in := roofInput(temperate)
	in.Hot = true
	if PlanRoofed(roofPlan(), in).Roofed.Empty() {
		t.Error("the hot latch alone does not make a bad climate")
	}
	if !PlanRoofed(roofPlan(), RoofInput{}).Roofed.Empty() {
		t.Error("an unknown curve with no latch roofs")
	}
}

func TestRoofedHallwayIsPricedNotAssumed(t *testing.T) {
	p := roofPlan()
	p.Rooms = []PlannedRoom{p.Rooms[0], p.Rooms[2]} // one door onto the hallway, one entrance
	got := PlanRoofed(p, roofInput(seaIce))
	if !hasRoofReason(got, ReasonRoofHallwaySkip) || got.Roofed.Contains(domain.Cell{X: 20, Z: 11}) {
		t.Fatalf("a hallway that costs what it saves was roofed: %+v", got.Decisions)
	}
	if !got.Roofed.Contains(domain.Cell{X: 11, Z: 15}) {
		t.Error("the room is not roofed with the hallway skipped")
	}
	in := roofInput(seaIce)
	in.Climate = ClimateModel{}
	if !hasRoofReason(PlanRoofed(roofPlan(), in), ReasonRoofUnpriced) {
		t.Error("an unknown model did not leave the hallway unpriced")
	}
	// A hot map is priced the same way.
	if r := PlanRoofed(roofPlan(), RoofInput{Curve: domain.Known([]float64{30, 36, 40, 38, 33, 30, 29, 31, 35, 38, 34, 31}), Climate: testClimate(), RoofDistance: testRoof}); r.Roofed.Empty() {
		t.Error("a hot curve roofs nothing")
	}
}

func TestRoofedSetIsDeterministic(t *testing.T) {
	a, b := PlanRoofed(roofPlan(), roofInput(seaIce)), PlanRoofed(roofPlan(), roofInput(seaIce))
	if !reflect.DeepEqual(a, b) {
		t.Fatal("same input gave different roofed sets")
	}
}

func TestValidRejectsARoofedCellOnAnOutdoorRole(t *testing.T) {
	p := roofPlan()
	p.Roofed = RegionOf([]domain.Cell{{X: 30, Z: 10}})
	if p.Valid() {
		t.Fatal("a roofed pen cell is valid")
	}
	p.Roofed = RegionOf([]domain.Cell{{X: 20, Z: 11}})
	if !p.Valid() {
		t.Fatal("a roofed hallway cell is not valid")
	}
}

func TestTilerRoofsEveryRoomOnAColdMapAndNoneOnATemperateOne(t *testing.T) {
	in, _ := tileInput(t, plainScoreSurvey(), 3, true)
	in.Catalog.Climate = testClimate()
	in.RoofDistance = testRoof
	in.OutdoorCurve = domain.Known(seaIce)
	cold, err := TileEmbedding(in)
	if err != nil {
		t.Fatal(err)
	}
	for _, r := range cold.Plan.Rooms {
		if r.Role.IsOutdoor() {
			continue
		}
		for _, c := range r.Region.Cells() {
			if !cold.Plan.Roofed.Contains(c) {
				t.Fatalf("room %d (%s) cell %v is not roofed on a cold curve", r.ID, r.Role, c)
			}
		}
	}
	if len(cold.RoofDecisions) == 0 {
		t.Error("no roof decisions on a cold map")
	}
	again, _ := TileEmbedding(in)
	if !reflect.DeepEqual(cold.Plan.Roofed, again.Plan.Roofed) {
		t.Error("the roofed set varies between runs")
	}
	in.OutdoorCurve = domain.Known(temperate)
	mild, err := TileEmbedding(in)
	if err != nil {
		t.Fatal(err)
	}
	if !mild.Plan.Roofed.Empty() {
		t.Errorf("a temperate curve roofs %d cells", mild.Plan.Roofed.Area())
	}
}
