package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The planner yields nothing without a meal store or a layout and census,
// and the whole-room meal store otherwise.
func TestPlanStorageMealStoreAndUnknownLayout(t *testing.T) {
	t.Parallel()
	if got := PlanStorage(StorageRequest{}).Sites; len(got) != 0 {
		t.Fatalf("empty view planned %+v", got)
	}
	room := Room{ID: "Room_1", Cells: []domain.Cell{{X: 1, Z: 1}, {X: 2, Z: 1}}}
	plan := PlanStorage(StorageRequest{Meals: &MealStore{Room: room, Filter: domain.PerishablesFilter(), Size: 2, Whole: true}})
	if len(plan.Sites) != 1 || plan.Sites[0].Role != domain.MealsRolePrefix+"Room_1" || plan.Sites[0].Priority != domain.CriticalPriority || len(plan.Sites[0].Candidates) != 1 {
		t.Fatalf("%+v", plan.Sites)
	}
}
