package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

func TestOperationIntentLabelsGoalMethod(t *testing.T) {
	for _, c := range []struct {
		goal   domain.GoalID
		method domain.MethodID
		reason string
		want   string
	}{
		{"routine-0123456789abcdef-MaintainFoodStorage", "hunt-0123456789abcdef0123456789abcdef", "", "Food storage: hunt"},
		{"routine-0123456789abcdef-MaintainFlooring", "floor-2", "", "Flooring: floor 2"},
		{"player-ab-MaintainSleeping", "comfort-Bed", "", "Bedroom: comfort Bed"},
		{"routine-01-MaintainFireSafety", "fire", "", "Fire safety: fire"},
		{"routine-01-EnsureFoodSupply", "acquire-0123456789abcdef0123456789abcdef", "food runway 1.5d", "Food supply: acquire, food runway 1.5d"},
		{"routine-01-MaintainSleeping", "bedroom-shell-3-4", "  room for 2 unhoused ", "Bedroom: bedroom shell 3 4, room for 2 unhoused"},
	} {
		got := OperationIntent(store.PlanMethod{GoalMethod: domain.GoalMethod{Goal: c.goal, Method: c.method}, Reason: c.reason})
		if got != c.want {
			t.Errorf("%s/%s = %q, want %q", c.goal, c.method, got, c.want)
		}
	}
}

type fakePlanGoals map[domain.PlanID]store.PlanMethod

func (f fakePlanGoals) PlanGoalMethod(_ context.Context, plan domain.PlanID) (store.PlanMethod, bool, error) {
	m, ok := f[plan]
	return m, ok, nil
}

func TestDispatchContextCarriesPlanIntent(t *testing.T) {
	goals := fakePlanGoals{"p": {GoalMethod: domain.GoalMethod{Goal: "routine-01-MaintainResource", Method: "chop-0123456789abcdef", Plan: "p"}, Reason: "WoodLog low"}}
	ctx := withPlanIntent(context.Background(), goals, "p")
	if got := bridge.OperationIntentFrom(ctx); got != "Resource: chop, WoodLog low" {
		t.Fatal(got)
	}
	if got := bridge.OperationIntentFrom(withPlanIntent(context.Background(), goals, "player")); got != "" {
		t.Fatal(got)
	}
}
