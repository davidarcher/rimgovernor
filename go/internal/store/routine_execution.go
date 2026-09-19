package store

import (
	"context"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// AuthorizeRoutinePlan verifies a method under the existing player direction.
// It grants no lease and never changes the selected player plan.
func (s *Store) AuthorizeRoutinePlan(ctx context.Context, root, target domain.GenerationSnapshot) error {
	if root.Validate() != nil || target.Validate() != nil || root.Native == 0 || root.Plan == target.Plan {
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
	if g.Retired || g.Goal.Source != domain.AutopilotGoal || g.Goal.Status != domain.GoalActive || g.Goal.Need == domain.NeedUnknown || g.Goal.Snapshot != root {
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
	if g.Goal.Need == domain.NeedRecovered {
		// A configured bill has recovered setup, but its already-issued first output
		// still needs ordinary pawn time. This never permits another setup write.
		waiting := false
		for _, progress := range p.Progress {
			v := progress.View()
			if progress.Action().Kind() != domain.ProductionBillAction || v.Attempt == 0 {
				return ErrConflict
			}
			waiting = waiting || v.Unresolved
		}
		if !waiting {
			return ErrConflict
		}
	}
	for _, action := range p.Spec.Actions() {
		switch action.Kind() {
		case domain.BuildingAction, domain.SupplyAllowAction, domain.WorkAssignmentAction, domain.AcquisitionAction,
			domain.ZoneCreateAction, domain.ProductionBillAction, domain.OwnedDraftAction, domain.MeleeAttackAction,
			domain.RangedAttackAction, domain.TendAction, domain.RescueAction, domain.CaptureAction, domain.HaulAction, domain.EquipAction,
			domain.GearReplaceAction, domain.RecoveryServiceAction, domain.MovementAction, domain.HusbandryAction,
			domain.PrisonerInteractionAction, domain.RepairAction, domain.CleanAction, domain.MineAcquisitionAction,
			domain.ProductionPolicyAction, domain.BuildingTemperatureAction, domain.BedMedicalAction, domain.GrowerCropAction, domain.BedAssignAction, domain.ExcavationAction, domain.DialogAnswerAction, domain.NamingConfirmationAction, domain.ResearchSelectAction, domain.TradeAction, domain.DeconstructionAction, domain.CutPlantAction, domain.HomeCoverageAction, domain.QuestAcceptAction:
		default:
			return errors.New("routine execution requires supported routine methods")
		}
	}
	return tx.Commit()
}
