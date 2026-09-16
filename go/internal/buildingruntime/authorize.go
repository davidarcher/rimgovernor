package buildingruntime

import (
	"context"
	"errors"

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

func (a planAuthorizer) AuthorizeRoutinePlan(ctx context.Context, root, target domain.GenerationSnapshot) error {
	if a.routine {
		if err := a.journal.AuthorizeRoutinePlan(ctx, root, target); err == nil {
			return nil
		}
	}
	if err := a.journal.AuthorizePlayerPlan(ctx, root, target); err != nil {
		return errors.Join(store.ErrConflict, err)
	}
	return nil
}
