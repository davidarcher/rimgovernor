package store

import (
	"context"
	"database/sql"
	"errors"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
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
	if _, bound := review.Need(goalID); !bound {
		return ErrConflict
	}
	g, err := loadGoal(ctx, tx, goalID)
	if err != nil {
		return err
	}
	if g.Retired || g.Goal.Source != domain.AutopilotGoal || g.Goal.Status != domain.GoalActive || g.Goal.Need == domain.NeedUnknown || g.Goal.Snapshot != root {
		return ErrConflict
	}
	// A prepared plan does not dispatch while a Rule vetoes its goal.
	if reason := review.Veto(g.Goal); reason != "" {
		return fmt.Errorf("%w: %w: goal %s: %s", ErrConflict, ErrNotAdmitted, g.Goal.ID, reason)
	}
	bound := false
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
		case domain.BuildingAction, domain.SupplyAllowAction, domain.SupplyForbidAction, domain.WorkAssignmentAction, domain.AcquisitionAction,
			domain.ZoneCreateAction, domain.ProductionBillAction, domain.OwnedDraftAction, domain.MeleeAttackAction,
			domain.RangedAttackAction, domain.TendAction, domain.RescueAction, domain.CaptureAction, domain.HaulAction, domain.EquipAction,
			domain.GearReplaceAction, domain.ApparelPolicyAction, domain.RecoveryServiceAction, domain.MovementAction, domain.HusbandryAction,
			domain.PrisonerInteractionAction, domain.RepairAction, domain.CleanAction, domain.MineAcquisitionAction, domain.OpenCasketAction,
			domain.BuildingTemperatureAction, domain.BedUseAction, domain.GrowerCropAction, domain.ClaimBuildingAction, domain.ZoneDeleteAction, domain.ZoneCellEditAction, domain.StockpilePatchAction, domain.BedAssignAction, domain.ExcavationAction, domain.DialogAnswerAction, domain.NamingConfirmationAction, domain.ResearchSelectAction, domain.TradeAction, domain.DeconstructionAction, domain.CutPlantAction, domain.MoveBuildingAction, domain.UninstallBuildingAction, domain.CoverClearanceAction, domain.FoundationRemovalAction, domain.HomeCoverageAction, domain.QuestAcceptAction, domain.WallRemovalAction, domain.CaravanDepartureAction:
		default:
			return errors.New("routine execution requires supported routine methods")
		}
	}
	return tx.Commit()
}

// Need returns the routine need the review binds the goal to.
func (r RoutineReview) Need(goal domain.GoalID) (domain.GoalID, bool) {
	for _, binding := range r.Goals {
		if binding.Goal == goal {
			return binding.Need, true
		}
	}
	return "", false
}

// Veto asks the policy Rules (#1017) whether this review admits a proposal
// for the goal, returning the veto's reason or "". A goal the review does
// not bind (a player goal) is outside the routine Rules.
func (r RoutineReview) Veto(g domain.Goal) string {
	need, bound := r.Need(g.ID)
	if !bound {
		return ""
	}
	return policy.VetoProposal(policy.RuleContext{Enabled: r.Enabled, Emergency: r.Emergency}, policy.RuleProposal{Need: need, Priority: g.Priority})
}

// admitRoutineRules is method admission's backstop for the Rules the
// planner already asked: a vetoed proposal is ErrNotAdmitted with the
// Rule's reason.
func admitRoutineRules(ctx context.Context, tx *sql.Tx, g domain.Goal) error {
	review, err := loadRoutine(ctx, tx)
	if err != nil {
		return err
	}
	if reason := review.Veto(g); reason != "" {
		return fmt.Errorf("%w: goal %s: %s", ErrNotAdmitted, g.ID, reason)
	}
	return nil
}
