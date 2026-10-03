package store

import (
	"context"
	"database/sql"
	"errors"
	"fmt"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// sameRoot compares the root a review, goal or incident recorded with the
// root a worker authorizes under: same world and root plan revision. The
// native generation is left out (#1141, as #259): authority toggles bump it
// with the world unchanged, and the session refuses a disabled or changed
// grant at dispatch on its own.
func sameRoot(recorded, root domain.GenerationSnapshot) bool {
	return recorded.SameWorld(root) && recorded.Plan == root.Plan && recorded.Revision == root.Revision
}

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
	if !review.Enabled || !sameRoot(review.Snapshot, root) {
		return ErrConflict
	}
	var goalID, incidentID sql.NullString
	if err = tx.QueryRowContext(ctx, "SELECT goal_id,incident_id FROM goal_methods WHERE plan_id=?", target.Plan).Scan(&goalID, &incidentID); err != nil {
		return err
	}
	if incidentID.Valid {
		if err = authorizeIncidentPlan(ctx, tx, review, domain.IncidentID(incidentID.String), root, target); err != nil {
			return err
		}
		return tx.Commit()
	}
	if err = authorizeGoalPlan(ctx, tx, review, domain.GoalID(goalID.String), root, target); err != nil {
		return err
	}
	return tx.Commit()
}

// authorizeIncidentPlan is AuthorizeRoutinePlan for an incident's method
// (#1020): the review binds the open occurrence in deficit, no Rule vetoes
// it and the plan is its unretired method.
func authorizeIncidentPlan(ctx context.Context, tx *sql.Tx, review RoutineReview, id domain.IncidentID, root, target domain.GenerationSnapshot) error {
	binding, bound := review.incidentBinding(id)
	if !bound || binding.Need != domain.NeedDeficit {
		return ErrConflict
	}
	state, err := loadIncident(ctx, tx, id)
	if err != nil {
		return err
	}
	if state.Incident.Closed || !sameRoot(state.Incident.Snapshot, root) {
		return ErrConflict
	}
	if reason := review.VetoIncident(state.Incident); reason != "" {
		return fmt.Errorf("%w: %w: incident %s: %s", ErrConflict, ErrNotAdmitted, id, reason)
	}
	if !slices.ContainsFunc(state.Methods, func(m IncidentMethod) bool { return m.Plan == target.Plan }) {
		return ErrConflict
	}
	p, err := load(ctx, tx, target.Plan)
	if err != nil {
		return err
	}
	if p.Retired || p.Spec.Revision() != target.Revision {
		return ErrConflict
	}
	return routineActionsSupported(p.Spec)
}

func authorizeGoalPlan(ctx context.Context, tx *sql.Tx, review RoutineReview, goalID domain.GoalID, root, target domain.GenerationSnapshot) error {
	if _, bound := review.Need(goalID); !bound {
		return ErrConflict
	}
	g, err := loadGoal(ctx, tx, goalID)
	if err != nil {
		return err
	}
	if g.Retired || g.Goal.Source != domain.AutopilotGoal || g.Goal.Status != domain.GoalActive || g.Goal.Need == domain.NeedUnknown || !sameRoot(g.Goal.Snapshot, root) {
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
	return routineActionsSupported(p.Spec)
}

// routineActionsSupported refuses a plan with an action kind routine
// execution does not dispatch.
func routineActionsSupported(spec domain.PlanSpec) error {
	for _, action := range spec.Actions() {
		switch action.Kind() {
		case domain.BuildingAction, domain.SupplyAllowAction, domain.SupplyForbidAction, domain.WorkAssignmentAction, domain.AcquisitionAction, domain.AcquisitionWithdrawAction,
			domain.ZoneCreateAction, domain.ProductionBillAction, domain.OwnedDraftAction, domain.SubdueAction,
			domain.TendAction, domain.RescueAction, domain.CaptureAction, domain.UseItemAction, domain.HaulAction, domain.EquipAction,
			domain.GearReplaceAction, domain.ApparelPolicyAction, domain.RecoveryServiceAction, domain.MovementAction, domain.HusbandryAction,
			domain.PrisonerInteractionAction, domain.RepairAction, domain.CleanAction, domain.MineAcquisitionAction, domain.OpenCasketAction,
			domain.BuildingTemperatureAction, domain.BedUseAction, domain.GrowerCropAction, domain.ClaimBuildingAction, domain.AutoRefuelAction, domain.SurgeryAction, domain.AutoHomeAreaAction, domain.PawnSettingsAction, domain.AreaAction, domain.PolicyPruneAction, domain.ReadingPolicyAction, domain.DrugPolicyAction, domain.FoodPolicyAction, domain.ZoneDeleteAction, domain.ZoneCellEditAction, domain.StockpilePatchAction, domain.AssignAction, domain.ExcavationAction, domain.DialogAnswerAction, domain.NamingConfirmationAction, domain.ResearchSelectAction, domain.TradeAction, domain.DeconstructionAction, domain.RemoveRoofAction, domain.AreaPlantCutAction, domain.CutPlantAction, domain.StripAction, domain.MoveBuildingAction, domain.UninstallBuildingAction, domain.CoverClearanceAction, domain.FoundationRemovalAction, domain.FloorRemovalAction, domain.QuestAcceptAction, domain.RitualAction, domain.WallRemovalAction, domain.CaravanDepartureAction:
		default:
			return errors.New("routine execution requires supported routine methods")
		}
	}
	return nil
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
	return r.vetoNeed(need, g.Priority)
}

// Workable loads the goal the review binds to need and reports whether a
// planner may work it: an active deficit the Rules admit (#1121). The goal
// is returned whenever the review binds one, workable or not.
func (s *Store) Workable(ctx context.Context, r RoutineReview, need policy.GoalID) (GoalState, bool, error) {
	for _, binding := range r.Goals {
		if binding.Need != need {
			continue
		}
		goal, err := s.LoadGoal(ctx, binding.Goal)
		if err != nil {
			return GoalState{}, false, err
		}
		return goal, goal.Goal.Status == domain.GoalActive && goal.Goal.Need == domain.NeedDeficit && r.Veto(goal.Goal) == "", nil
	}
	return GoalState{}, false, nil
}

// vetoAction asks the action Rules (#1018) at dispatch: a vetoed action is
// ErrActionVetoed with the Rule's reason and only that action is refused.
func vetoAction(ctx context.Context, tx *sql.Tx, a domain.Action) error {
	review, err := loadRoutine(ctx, tx)
	if err != nil {
		return err
	}
	if reason := policy.VetoAction(policy.ActionContext{Unsafe: review.Unsafe}, a); reason != "" {
		return fmt.Errorf("%w: action %s: %s", ErrActionVetoed, a.ID(), reason)
	}
	return nil
}
