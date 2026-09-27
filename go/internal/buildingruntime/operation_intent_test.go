package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestOperationIntentLabelsGoalMethod(t *testing.T) {
	for _, c := range []struct {
		goal   domain.GoalID
		method domain.MethodID
		want   string
	}{
		{"routine-0123456789abcdef-MaintainFoodStorage", "hunt-0123456789abcdef0123456789abcdef", "Food reserve: hunt"},
		{"routine-0123456789abcdef-MaintainFlooring", "floor-2", "Flooring: floor 2"},
		{"player-ab-MaintainSleeping", "comfort-Bed", "Bedroom: comfort Bed"},
		{"routine-01-MaintainFireSafety", "fire", "Fire safety: fire"},
	} {
		if got := OperationIntent(domain.GoalMethod{Goal: c.goal, Method: c.method}); got != c.want {
			t.Errorf("%s/%s = %q, want %q", c.goal, c.method, got, c.want)
		}
	}
}

type fakePlanGoals map[domain.PlanID]domain.GoalMethod

func (f fakePlanGoals) PlanGoalMethod(_ context.Context, plan domain.PlanID) (domain.GoalMethod, bool, error) {
	m, ok := f[plan]
	return m, ok, nil
}

func TestDispatchContextCarriesPlanIntent(t *testing.T) {
	goals := fakePlanGoals{"p": {Goal: "routine-01-MaintainResource", Method: "chop-0123456789abcdef", Plan: "p"}}
	ctx := withPlanIntent(context.Background(), goals, "p")
	if got := bridge.OperationIntentFrom(ctx); got != "Resource: chop" {
		t.Fatal(got)
	}
	if got := bridge.OperationIntentFrom(withPlanIntent(context.Background(), goals, "player")); got != "" {
		t.Fatal(got)
	}
}
