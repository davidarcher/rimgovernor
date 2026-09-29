package buildingruntime

import (
	"fmt"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// storageSlot is a 4x4-interior storage room at (11..14, 21..24), its door
// on the south wall at (12,20).
func storageSlot(t *testing.T) domain.RoomFootprint {
	t.Helper()
	var interior []domain.Cell
	for x := int32(11); x <= 14; x++ {
		for z := int32(21); z <= 24; z++ {
			interior = append(interior, domain.Cell{X: x, Z: z})
		}
	}
	room, err := domain.NewRoomFootprint(interior, domain.Cell{X: 12, Z: 20}, domain.South)
	if err != nil {
		t.Fatal(err)
	}
	return room
}

// #1186: the starter shell standing on the storage slot takes the covered
// stockpile inside it, beside the bunks and off the door aisle, and no new
// walls are raised.
func TestStorageRoomStepStockpilesInsideAStandingShell(t *testing.T) {
	room := storageSlot(t)
	var cells []policy.SiteCell
	for _, w := range room.Walls() {
		c := policy.SiteCell{Cell: w, PlayerEdifice: domain.Known("Wall"), Doorway: domain.Known(false)}
		if w == room.Door() {
			c = policy.SiteCell{Cell: w, PlayerEdifice: domain.Known(""), Doorway: domain.Known(true)}
		}
		cells = append(cells, c)
	}
	bunk := domain.Cell{X: 14, Z: 24}
	for _, in := range room.Interior() {
		cells = append(cells, policy.SiteCell{Cell: in, Roofed: domain.Known(true), Occupied: domain.Known(in == bunk), Zone: domain.Known(false)})
	}
	stockpile, perimeter := storageRoomStep(room, cells)
	if perimeter != nil {
		t.Fatal(perimeter)
	}
	got := map[domain.Cell]bool{}
	for _, c := range stockpile {
		got[c] = true
	}
	if len(stockpile) != 16-1-3 || got[bunk] || got[domain.Cell{X: 12, Z: 21}] || got[domain.Cell{X: 11, Z: 21}] || got[domain.Cell{X: 13, Z: 21}] || !got[domain.Cell{X: 14, Z: 21}] {
		t.Fatal(stockpile)
	}
}

// #1186: with no shell on the slot the step raises walls exactly on the
// storage room's ring, door first, and never an ad-hoc site.
func TestStorageRoomStepRaisesTheSlotRingWithoutAShell(t *testing.T) {
	room := storageSlot(t)
	stockpile, perimeter := storageRoomStep(room, nil)
	if stockpile != nil || len(perimeter) != len(room.Walls()) || perimeter[0] != room.Door() {
		t.Fatal(stockpile, perimeter)
	}
	ring := map[domain.Cell]bool{}
	for _, w := range room.Walls() {
		ring[w] = true
	}
	for _, c := range perimeter {
		if !ring[c] {
			t.Fatal(c)
		}
	}
}

func supplyRoomShellPlan(t *testing.T, id domain.PlanID, originX, originZ int32) domain.PlanSpec {
	t.Helper()
	door := domain.Cell{X: originX + 3, Z: originZ}
	var actions []domain.Action
	i := 0
	for x := originX; x < originX+6; x++ {
		for z := originZ; z < originZ+6; z++ {
			cell := domain.Cell{X: x, Z: z}
			if cell != door && (x != originX && x != originX+5 && z != originZ && z != originZ+5) {
				continue
			}
			def := "Wall"
			if cell == door {
				def = "Door"
			}
			b, err := domain.NewBuilding(def, cell, domain.North, "")
			if err != nil {
				t.Fatal(err)
			}
			a, err := domain.NewBuildingAction(domain.ActionID(fmt.Sprintf("%s-%d", id, i)), b)
			if err != nil {
				t.Fatal(err)
			}
			actions = append(actions, a)
			i++
		}
	}
	spec, err := domain.NewPlan(id, 1, actions)
	if err != nil {
		t.Fatal(err)
	}
	return spec
}

func TestSecureSuppliesRoomShellPlanRecognizesWallDoorShell(t *testing.T) {
	spec := supplyRoomShellPlan(t, "supply-room-shell-1", 10, 20)
	if !secureSuppliesRoomShellPlan(spec) {
		t.Fatal("expected a Wall/Door perimeter plan to be recognized as the room shell")
	}
}

func TestSecureSuppliesRoomShellPlanIgnoresUnrelatedPlans(t *testing.T) {
	b, err := domain.NewBuilding("SleepingSpot", domain.Cell{X: 1, Z: 1}, domain.North, "")
	if err != nil {
		t.Fatal(err)
	}
	a, err := domain.NewBuildingAction("other-0", b)
	if err != nil {
		t.Fatal(err)
	}
	spec, err := domain.NewPlan("other", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	if secureSuppliesRoomShellPlan(spec) {
		t.Fatal("expected an unrelated plan not to be recognized as the room shell")
	}
}

func TestSecureSuppliesRoomShellPlanIgnoresHaulAndZoneActions(t *testing.T) {
	haul, err := domain.NewHaul("pawn-1", "item-1", "Silver", domain.Cell{X: 2, Z: 2})
	if err != nil {
		t.Fatal(err)
	}
	a, err := domain.NewHaulAction("haul-0", haul)
	if err != nil {
		t.Fatal(err)
	}
	spec, err := domain.NewPlan("haul-plan", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	if secureSuppliesRoomShellPlan(spec) {
		t.Fatal("expected a haul plan not to be recognized as the room shell")
	}
}
