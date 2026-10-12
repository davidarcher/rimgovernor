package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// A battery placed on the shelter porch brings the Build Roof designation of
// the whole porch; a battery elsewhere does not.
func TestPorchRoofActionAccompaniesAPorchBattery(t *testing.T) {
	t.Parallel()
	plan := policy.LayoutPlan{Cold: true, NextRoomID: 3, Rooms: []policy.PlannedRoom{{
		ID: 1, Role: policy.PlannedShelter, Region: policy.RectRegion(policy.Rectangle{X: 10, Z: 10, Width: 5, Height: 4}),
		Door: domain.Cell{X: 15, Z: 12}, DoorRot: domain.East,
	}, {
		ID: 2, Parent: 1, Role: policy.PlannedPorch, DoorRot: domain.East,
		Region: policy.RectRegion(policy.Rectangle{X: 8, Z: 10, Width: 1, Height: 4}),
	}}}
	facts := observation.ColonyProjection{LayoutPlan: domain.Known(plan)}
	battery := func(c domain.Cell) policy.Preview {
		b, err := domain.NewBuilding(policy.BatteryDefinition, c, domain.North, "")
		if err != nil {
			t.Fatal(err)
		}
		a, err := domain.NewBuildingAction("b", b, domain.TierSurvive)
		if err != nil {
			t.Fatal(err)
		}
		return policy.Preview{Action: a}
	}
	admit := func(c domain.Cell) roundsAdmission {
		return roundsAdmission{facts: facts, snapshot: domain.GenerationSnapshot{Plan: "p"}, selected: []policy.Preview{battery(c)}}
	}
	action, ok, err := porchRoofAction(admit(domain.Cell{X: 8, Z: 10}), 1)
	if err != nil || !ok {
		t.Fatal("porch battery brings no roof", ok, err)
	}
	roof, _ := action.BuildRoof()
	if got := roof.Cells(); len(got) != 4 || got[0] != (domain.Cell{X: 8, Z: 10}) {
		t.Fatalf("roof cells %v", got)
	}
	if _, ok, err = porchRoofAction(admit(domain.Cell{X: 30, Z: 30}), 1); err != nil || ok {
		t.Fatal("a battery off the porch brings a roof", ok, err)
	}
}
