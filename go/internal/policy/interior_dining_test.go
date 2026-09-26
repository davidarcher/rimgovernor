package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestDiningRecLayoutRepeatsAcrossDoorsAndSizes(t *testing.T) {
	for _, role := range []RoomRole{RoomRoleDiningRoom, RoomRoleRecRoom} {
		for _, size := range [][2]int32{{5, 5}, {6, 7}, {5, 11}, {7, 12}, {8, 16}} {
			assertInteriorRepeatable(t, role, size[0], size[1], 1)
		}
	}
}

func TestDiningTablesHaveAChairOnEveryEdge(t *testing.T) {
	plan := assertInteriorRepeatable(t, RoomRoleDiningRoom, 7, 11, 1)
	var tables, chairs, pins int
	occupied := map[domain.Cell]string{}
	for _, p := range plan.Canonical {
		for _, c := range rectCells(p.Rect) {
			occupied[c] = p.Def
		}
		switch p.Def {
		case "Table1x2c":
			tables++
		case "DiningChair":
			chairs++
		case "HorseshoesPin":
			pins++
		}
	}
	if tables != 1 || chairs != 6 || pins != 1 {
		t.Fatalf("tables=%d chairs=%d pins=%d, want 1, 6, 1", tables, chairs, pins)
	}
	for _, p := range plan.Canonical {
		if p.Def != "Table1x2c" {
			continue
		}
		r := p.Rect
		for _, c := range []domain.Cell{{X: r.X - 1, Z: r.Z}, {X: r.X - 1, Z: r.Z + 1}, {X: r.X + 1, Z: r.Z}, {X: r.X + 1, Z: r.Z + 1}, {X: r.X, Z: r.Z - 1}, {X: r.X, Z: r.Z + 2}} {
			if occupied[c] != "DiningChair" {
				t.Errorf("%s: no chair at %v", p.Slot, c)
			}
		}
	}
	lane := HorseshoesLane(plan.Frame)
	for _, c := range rectCells(lane) {
		if def, taken := occupied[c]; taken {
			t.Errorf("horseshoes lane cell %v holds %s", c, def)
		}
	}
}

func TestDiningAndRecPrioritiesInAShortRoom(t *testing.T) {
	count := func(role RoomRole, def string) int {
		plan := assertInteriorRepeatable(t, role, 6, 8, 1)
		n := 0
		for _, p := range plan.Canonical {
			if p.Def == def {
				n++
			}
		}
		return n
	}
	if count(RoomRoleDiningRoom, "Table1x2c") != 1 || count(RoomRoleDiningRoom, "HorseshoesPin") != 0 {
		t.Error("dining room should keep its table over the pin")
	}
	if count(RoomRoleRecRoom, "HorseshoesPin") != 1 || count(RoomRoleRecRoom, "Table1x2c") != 0 {
		t.Error("rec room should keep its pin over a table")
	}
	if _, ok := PlanInterior(InteriorRoom{Role: RoomRoleDiningRoom, Interior: Rectangle{Width: 4, Height: 9}, Doors: []domain.Cell{{X: 1, Z: -1}}}); ok {
		t.Error("a 4-wide room has no side aisle")
	}
}
