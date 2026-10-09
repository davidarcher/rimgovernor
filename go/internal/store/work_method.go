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
	review, err := loadRounds(ctx, tx)
	if err != nil {
		return err
	}
	if !review.Enabled || review.Snapshot != owner.ownerSnapshot() {
		return ErrConflict
	}
	bound := false
	areaOnly := false
	// A creepjoiner's isolation is an area move among its drops and
	// inspections: the other actions pass, each assignment is
	// area-only.
	isolation := false
	need := policy.EnsureWorkAssignments
	if served, ok := owner.ownerNeed(review); ok {
		bound = served == need
		areaOnly = served == policy.RecoverDisasterServices
		isolation = served == policy.ManageCreepJoiners
	}
	if !bound && !areaOnly && !isolation {
		return ErrConflict
	}
	// The EnsureWorkAssignments plan also carries the pawn settings and
	// per-pawn policies that ride its goal;
	// the eight-pawn cap and one-assignment-per-pawn rule bind only the
	// work assignments. A disaster area plan carries assignments alone.
	pawns := map[domain.PawnID]bool{}
	for _, action := range plan.Actions() {
		if !areaOnly && roundsSettingsKinds[action.Kind()] || isolation && action.Kind() != domain.WorkAssignmentAction {
			continue
		}
		w, ok := action.WorkAssignment()
		if !ok || pawns[w.Pawn()] || len(pawns) == 8 {
			return ErrConflict
		}
		if (areaOnly || isolation) && (!w.HasArea() || w.HasSchedule() || len(w.Settings()) != 0) {
			return ErrConflict
		}
		pawns[w.Pawn()] = true
	}
	return nil
}

var roundsSettingsKinds = map[domain.ActionKind]bool{
	domain.PawnSettingsAction:  true,
	domain.ReadingPolicyAction: true,
	domain.DrugPolicyAction:    true,
	domain.FoodPolicyAction:    true,
}
