package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestWorkshopLayoutRepeatsAcrossDoorsAndSizes(t *testing.T) {
	for _, size := range [][3]int32{{7, 5, 1}, {7, 5, 3}, {3, 3, 1}, {8, 4, 0}, {11, 6, 2}, {12, 3, 5}} {
		assertInteriorRepeatable(t, RoomRoleWorkshop, size[0], size[1], size[2])
	}
	if _, ok := PlanInterior(InteriorRoom{Role: RoomRoleWorkshop, Interior: Rectangle{X: 0, Z: 0, Width: 7, Height: 2}, Doors: []domain.Cell{{X: 1, Z: -1}}}, InteriorPieceDef{}); ok {
		t.Error("a two-deep room planned a workshop")
	}
}

func TestWorkshopCabinetsServeTwoBenchesAndShelvesKeepTheRole(t *testing.T) {
	plan := assertInteriorRepeatable(t, RoomRoleWorkshop, 11, 5, 1)
	var benches, cabinets, shelves []InteriorPiece
	for _, p := range plan.Canonical {
		switch p.Def {
		case workshopBenchDef:
			benches = append(benches, p)
		case workshopCabinetDef:
			cabinets = append(cabinets, p)
		case workshopShelfDef:
			shelves = append(shelves, p)
		}
	}
	if len(benches) != 3 || len(cabinets) != 2 || len(shelves) != 3 {
		t.Fatalf("benches %d cabinets %d shelves %d, want 3 2 3", len(benches), len(cabinets), len(shelves))
	}
	adjacent := func(a, b Rectangle) bool {
		return a.X <= b.X+b.Width && b.X <= a.X+a.Width && a.Z <= b.Z+b.Height && b.Z <= a.Z+a.Height
	}
	for _, b := range benches {
		n := 0
		for _, c := range cabinets {
			if adjacent(b.Rect, c.Rect) {
				n++
			}
		}
		if n < 1 || n > 2 {
			t.Errorf("%s: %d adjacent cabinets", b.Slot, n)
		}
		if b.Rot != domain.North || b.Rect.Z != plan.Frame.Depth-1 {
			t.Errorf("%s: %s at %+v, want North on the back wall", b.Slot, b.Rot, b.Rect)
		}
	}
	for _, c := range cabinets {
		n := 0
		for _, b := range benches {
			if adjacent(b.Rect, c.Rect) {
				n++
			}
		}
		if n != 2 {
			t.Errorf("%s: serves %d benches, want 2", c.Slot, n)
		}
	}
	for i, b := range benches {
		ic := domain.Cell{X: b.Rect.X + 1, Z: b.Rect.Z - 1}
		s := shelves[i].Rect
		if (s.X-ic.X)*(s.X-ic.X)+(s.Z-ic.Z)*(s.Z-ic.Z) != 1 {
			t.Errorf("%s: shelf %+v not beside interaction cell %v", b.Slot, s, ic)
		}
	}
	// RoomRoleWorker_Storeroom scores 1 per shelf, Workshop 27 per bench.
	if len(shelves) >= 27*len(benches) {
		t.Error("shelves outscore benches")
	}
}
