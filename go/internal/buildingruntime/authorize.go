package buildingruntime

import (
	"context"
	"errors"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// planAuthorizer decides which non-root plans may dispatch under the root
// plan's authority: routine methods (when enabled) and player submissions for
// the same world. It satisfies executor.RoutineScope so the executor recheck
// before every write uses the same rule as the worker and the leases.
type planAuthorizer struct {
	journal *store.Store
	routine bool
}

// ErrUnauthorizedPlan is the refusal for a plan that is neither a routine
// method bound under the current review nor a player submission for the root
// world: a retired method, one whose goal has recovered or re-epoched, or one
// admitted under an earlier root. It wraps store.ErrConflict, the sentinel
// every authorization path reports, so callers keep matching it, and names
// the refusal instead of the sentinel's identity-collision text (#214).
var ErrUnauthorizedPlan = fmt.Errorf("%w: plan is not authorized under the root plan", store.ErrConflict)

func (a planAuthorizer) AuthorizeRoutinePlan(ctx context.Context, root, target domain.GenerationSnapshot) error {
	if a.routine {
		// A player plan has no goal method row, so any routine refusal
		// falls through to the player check.
		if err := a.journal.AuthorizeRoutinePlan(ctx, root, target); err == nil {
			return nil
		}
	}
	if err := a.journal.AuthorizePlayerPlan(ctx, root, target); err != nil {
		if errors.Is(err, store.ErrConflict) {
			return ErrUnauthorizedPlan
		}
		return errors.Join(store.ErrConflict, err)
	}
	return nil
}
