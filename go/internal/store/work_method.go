package store

import (
	"context"
	"database/sql"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func admitWorkMethod(ctx context.Context, tx *sql.Tx, owner methodOwner, plan domain.PlanSpec) error {
	hasWork := false
	for _, action := range plan.Actions() {
		hasWork = hasWork || action.Kind() == domain.WorkAssignmentAction
	}
	if !hasWork {
		return nil
	}
	review, err := loadRoutine(ctx, tx)
	if err != nil {
		return err
	}
	if !review.Enabled || review.Snapshot != owner.ownerSnapshot() || !owner.ownerAutopilot() || len(plan.Actions()) > 8 {
		return ErrConflict
	}
	bound := false
	areaOnly := false
	need := policy.EnsureWorkAssignments
	if served, ok := owner.ownerNeed(review); ok {
		bound = served == need
		areaOnly = served == policy.RecoverDisasterServices
	}
	if !bound && !areaOnly {
		return ErrConflict
	}
	pawns := map[domain.PawnID]bool{}
	for _, action := range plan.Actions() {
		w, ok := action.WorkAssignment()
		if !ok || pawns[w.Pawn()] {
			return ErrConflict
		}
		if areaOnly && (!w.HasArea() || w.HasSchedule() || len(w.Settings()) != 0) {
			return ErrConflict
		}
		pawns[w.Pawn()] = true
	}
	return nil
}
