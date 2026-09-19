package buildingruntime

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// yieldDevelopment hands need's development slot on when its planner has
// exhausted every method this review (store.YieldRoutineDevelopment). It
// is scoped to the review the planner loaded, so a review that moved under
// the step yields nothing; the planner's own result still reports the
// exhaustion. Planners call it only on the retry-bound and
// every-fallback-refused paths of a selected goal, never on an ordinary
// refusal of a goal the review did not select.
func yieldDevelopment(ctx context.Context, journal *store.Store, review store.RoutineReview, need domain.GoalID) error {
	_, err := journal.YieldRoutineDevelopment(ctx, review.Revision, need)
	if err == nil {
		clockEvent(ctx, "routine", "development_yield", "development slot yielded", "revision", review.Revision, "goal", string(need))
	}
	return err
}
