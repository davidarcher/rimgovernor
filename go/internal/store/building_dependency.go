package store

import (
	"context"
	"database/sql"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// checkDependencies is every admission's prerequisite gate (#937):
// domain.CheckDependencies, and each building prerequisite standing built in
// the last routine review's census (RoutineReview.Built). An accepted
// building intent completes once its blueprint is placed, so a dependent
// (a bed on a floor) waits for the building, not the receipt.
func checkDependencies(ctx context.Context, tx *sql.Tx, state PlanState, action domain.ActionID, current domain.GenerationSnapshot, tick domain.Tick) error {
	if err := state.Spec.CheckDependencies(action, state.Progress, current, tick); err != nil {
		return err
	}
	buildings := map[domain.ActionID]bool{}
	for _, a := range state.Spec.Actions() {
		buildings[a.ID()] = a.Kind() == domain.BuildingAction
	}
	var required []domain.ActionID
	for _, d := range state.Spec.Dependencies() {
		if d.Action == action && buildings[d.Requires] {
			required = append(required, d.Requires)
		}
	}
	if len(required) == 0 {
		return nil
	}
	review, err := loadRoutine(ctx, tx)
	if err != nil {
		return err
	}
	s := review.Snapshot
	if s.Colony != current.Colony || s.Load != current.Load || s.Map != current.Map {
		return domain.ErrDependency
	}
	for _, r := range required {
		if !slices.Contains(review.Built, r) {
			return domain.ErrDependency
		}
	}
	return nil
}
