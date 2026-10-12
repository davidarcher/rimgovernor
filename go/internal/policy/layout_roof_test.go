package policy

import (
	"reflect"
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// roofPlan is two rooms opening north onto open ground (their entrance at
// (11,12)) and a pen east of them.
func roofPlan() LayoutPlan {
	kitchen := PlannedRoom{ID: 1, Role: PlannedKitchen, Region: RectRegion(Rectangle{X: 10, Z: 14, Width: 4, Height: 4}), Door: domain.Cell{X: 11, Z: 13}, DoorRot: domain.South}
	freezer := PlannedRoom{ID: 2, Role: PlannedFreezer, Region: RectRegion(Rectangle{X: 16, Z: 14, Width: 4, Height: 4}), Door: domain.Cell{X: 17, Z: 13}, DoorRot: domain.South}
	pen := PlannedRoom{ID: 3, Role: PlannedPen, Outdoor: true, Region: RectRegion(Rectangle{X: 28, Z: 9, Width: 5, Height: 5}), Door: domain.Cell{X: 27, Z: 11}, DoorRot: domain.West}
	return LayoutPlan{
		Rooms:     []PlannedRoom{kitchen, freezer, pen},
		Entrances: []domain.Cell{{X: 11, Z: 12}}, NextRoomID: 4,
	}
}

func roofInput(curve []float64) RoofInput {
	return RoofInput{Curve: domain.Known(curve), RoofDistance: testRoof}
}

func hasRoofReason(r RoofResult, reason string) bool {
	return slices.ContainsFunc(r.Decisions, func(d RoofDecision) bool { return d.Reason == reason })
}

func TestRoofedSetCoversRoomsAndSkipsOutdoorRoles(t *testing.T) {
	p := roofPlan()
	got := PlanRoofed(p, roofInput(seaIce))
	for _, r := range p.Rooms[:2] {
		for _, c := range r.Region.Cells() {
			if !got.Roofed.Contains(c) {
				t.Fatalf("room %d cell %v is not roofed", r.ID, c)
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
	// A hall of a room 20 cells across has a middle farther from every wall than a roof holds.
	plan := roofPlan()
	plan.Rooms = append(plan.Rooms, PlannedRoom{ID: 4, Role: PlannedStorage, Region: RectRegion(Rectangle{X: 40, Z: 14, Width: 20, Height: 20}), Door: domain.Cell{X: 41, Z: 13}, DoorRot: domain.South})
	plan.NextRoomID = 5
	got := PlanRoofed(plan, roofInput(seaIce))
	var far []domain.Cell
	for _, d := range got.Decisions {
		if d.Reason == ReasonRoofUnsupported {
			far = d.Cells
		}
	}
	if len(far) == 0 {
		t.Fatal("cells 20 cells from every wall are not reported")
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
	var walls []domain.Cell
	for _, r := range plan.Rooms[:2] {
		walls = append(walls, r.Region.Walls().Cells()...)
	}
	walls = append(walls, plan.Rooms[3].Region.Walls().Cells()...)
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
	p.Roofed = RegionOf([]domain.Cell{{X: 11, Z: 15}})
	if !p.Valid() {
		t.Fatal("a roofed room cell is not valid")
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
