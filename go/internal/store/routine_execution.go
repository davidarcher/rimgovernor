package store

import (
	"context"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// AuthorizeRoutinePlan verifies a method under the existing player direction.
// It grants no lease and never changes the selected player plan.
func (s *Store) AuthorizeRoutinePlan(ctx context.Context, root, target domain.GenerationSnapshot) error {
	if root.Validate() != nil || target.Validate() != nil || root.Native == 0 || root.Direction == 0 || root.Plan == target.Plan {
		return ErrConflict
	}
	matching := target
	matching.Plan, matching.Revision = root.Plan, root.Revision
	if matching != root {
		return ErrConflict
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return err
	}
	defer tx.Rollback()
	review, err := loadRoutine(ctx, tx)
	if err != nil {
		return err
	}
	if !review.Enabled || review.Snapshot != root {
		return ErrConflict
	}
	var goalID domain.GoalID
	if err = tx.QueryRowContext(ctx, "SELECT goal_id FROM goal_methods WHERE plan_id=?", target.Plan).Scan(&goalID); err != nil {
		return err
	}
	bound := false
	for _, binding := range review.Goals {
		if binding.Goal == goalID {
			bound = true
		}
	}
	if !bound {
		return ErrConflict
	}
	g, err := loadGoal(ctx, tx, goalID)
	if err != nil {
		return err
	}
	if g.Retired || g.Goal.Source != domain.AutopilotGoal || g.Goal.Status != domain.GoalActive || g.Goal.Need != domain.NeedDeficit || g.Goal.Snapshot != root {
		return ErrConflict
	}
	bound = false
	for _, method := range g.Methods {
		if method.Plan == target.Plan && method.Epoch == g.Goal.Epoch {
			bound = true
		}
	}
	if !bound {
		return ErrConflict
	}
	p, err := load(ctx, tx, target.Plan)
	if err != nil {
		return err
	}
	if p.Retired || p.Spec.Revision() != target.Revision {
		return ErrConflict
	}
	for _, action := range p.Spec.Actions() {
		if action.Kind() != domain.BuildingAction && action.Kind() != domain.SupplyAllowAction && action.Kind() != domain.WorkAssignmentAction && action.Kind() != domain.AcquisitionAction {
			return errors.New("routine execution requires supported routine methods")
		}
	}
	return tx.Commit()
}
