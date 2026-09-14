package buildingruntime

import (
	"fmt"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

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
