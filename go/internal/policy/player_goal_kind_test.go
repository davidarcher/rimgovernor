package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Every kind CreateGoal lets the player force-activate must already be an
// autopilot-managed goal with its own deficit assessment here. If a kind ever
// stops naming a real routine goal, player activation would create a goal no
// planner knows how to compose a method for, and it would sit in deficit
// forever.
func TestPlayerGoalKindsMatchRoutineGoals(t *testing.T) {
	t.Parallel()
	routine := map[domain.GoalKind]GoalID{
		domain.EnsureFoodSupplyGoal:        EnsureFoodSupply,
		domain.EnsureInitialShelterGoal:    EnsureInitialShelter,
		domain.EnsureFoodStorageGoal:       EnsureFoodStorage,
		domain.EnsureCookingGoal:           EnsureCooking,
		domain.EnsureTemperatureSafetyGoal: EnsureTemperatureSafety,
		domain.EnsureBasicPowerGoal:        EnsureBasicPower,
		domain.EnsureBasicDefenseGoal:      EnsureBasicDefense,
		domain.MaintainWoodGoal:            MaintainWood,
		domain.MaintainResourceGoal:        MaintainResource,
		domain.MaintainWasteGoal:           MaintainWaste,
	}
	kinds := domain.GoalKinds()
	if len(routine) != len(kinds) {
		t.Fatal("player goal kinds and routine goals diverged", len(routine), len(kinds))
	}
	for _, kind := range kinds {
		id, ok := routine[kind]
		if !ok {
			t.Fatal("player goal kind names no routine goal", kind)
		}
		if string(id) != string(kind) {
			t.Fatal("player goal kind and routine goal ID differ", kind, id)
		}
	}
}
