package store

import (
	"context"
	"database/sql"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func admitWorkMethod(ctx context.Context, tx *sql.Tx, goal GoalState, plan domain.PlanSpec) error {
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
	if !review.Enabled || review.Snapshot != goal.Goal.Snapshot || goal.Goal.Source != domain.AutopilotGoal || len(plan.Actions()) > 8 {
		return ErrConflict
	}
	bound := false
	areaOnly := false
	need := policy.EnsureWorkAssignments
	medical := medicalCarePlan(plan)
	if medical {
		need = policy.MaintainMedicalCare
	}
	for _, binding := range review.Goals {
		bound = bound || binding.Need == need && binding.Goal == goal.Goal.ID
		areaOnly = areaOnly || binding.Need == policy.RecoverDisasterServices && binding.Goal == goal.Goal.ID
	}
	if !bound && !areaOnly {
		return ErrConflict
	}
	pawns := map[domain.PawnID]bool{}
	for _, action := range plan.Actions() {
		w, ok := action.WorkAssignment()
		if !ok || pawns[w.Pawn()] || (w.MedicalCare() != "") != medical {
			return ErrConflict
		}
		if areaOnly && (!w.HasArea() || w.HasSchedule() || len(w.Settings()) != 0) {
			return ErrConflict
		}
		pawns[w.Pawn()] = true
	}
	return nil
}

// Care settings may accompany hospital construction or bed-rest work in the
// same medical goal; their admission remains a care-only plan below that goal.
func medicalCarePlan(plan domain.PlanSpec) bool {
	if len(plan.Actions()) == 0 {
		return false
	}
	for _, action := range plan.Actions() {
		w, ok := action.WorkAssignment()
		if !ok || w.MedicalCare() == "" {
			return false
		}
	}
	return true
}
