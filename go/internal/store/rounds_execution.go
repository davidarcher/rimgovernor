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

// AuthorizeRoundsPlan verifies a method under the existing player direction.
// It grants no lease and never changes the selected player plan.
func (s *Store) AuthorizeRoundsPlan(ctx context.Context, root, target domain.GenerationSnapshot) error {
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
	review, err := loadRounds(ctx, tx)
	if err != nil {
		return err
	}
	if !review.Enabled || !sameRoot(review.Snapshot, root) {
		return ErrConflict
	}
	var kind, ownerID string
	if err = tx.QueryRowContext(ctx, "SELECT kind,owner_id FROM plan_methods WHERE plan_id=?", target.Plan).Scan(&kind, &ownerID); err != nil {
		return err
	}
	if kind == "project" {
		project, err := loadProject(ctx, tx, domain.ProjectID(ownerID))
		if err != nil {
			return err
		}
		if err = authorizeStandardPlan(ctx, tx, review, project, root, target); err != nil {
			return err
		}
		return tx.Commit()
	}
	if kind == "incident" {
		if err = authorizeIncidentPlan(ctx, tx, review, domain.IncidentID(ownerID), root, target); err != nil {
			return err
		}
		return tx.Commit()
	}
	goal, err := loadStandard(ctx, tx, domain.ConcernID(ownerID))
	if err != nil {
		return err
	}
	if err = authorizeStandardPlan(ctx, tx, review, goal, root, target); err != nil {
		return err
	}
	return tx.Commit()
}

// authorizeIncidentPlan is AuthorizeRoundsPlan for an incident's method
// (#1020): the review binds the open occurrence in deficit, no Safeguard vetoes
// it and the plan is its unretired method.
func authorizeIncidentPlan(ctx context.Context, tx *sql.Tx, review Rounds, id domain.IncidentID, root, target domain.GenerationSnapshot) error {
	binding, bound := review.incidentBinding(id)
	if !bound || binding.Situation != domain.SituationActive {
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
	return roundsActionsSupported(p.Spec)
}

func authorizeStandardPlan(ctx context.Context, tx *sql.Tx, review Rounds, owner WorkOwner, root, target domain.GenerationSnapshot) error {
	if _, bound := owner.ownerNeed(review); !bound {
		return ErrConflict
	}
	g, _ := SummarizeOwner(owner)
	if g.Retired || g.Status != domain.StandardOpen || g.Finding == domain.FindingUnclear || !sameRoot(g.Snapshot, root) {
		return ErrConflict
	}
	// A prepared plan does not dispatch while a Safeguard vetoes its goal.
	if reason := review.VetoOwner(owner); reason != "" {
		return fmt.Errorf("%w: %w: standard %s: %s", ErrConflict, ErrNotAdmitted, g.ID, reason)
	}
	bound := false
	for _, method := range owner.OwnerMethods() {
		if method.Plan == target.Plan && method.Episode == g.Episode {
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
	if g.Finding == domain.FindingMet {
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
	return roundsActionsSupported(p.Spec)
}

// roundsActionsSupported refuses a plan with an action kind routine
// execution does not dispatch.
func roundsActionsSupported(spec domain.PlanSpec) error {
	for _, action := range spec.Actions() {
		switch action.Kind() {
		case domain.BuildingAction, domain.SupplyAllowAction, domain.SupplyForbidAction, domain.WorkAssignmentAction, domain.AcquisitionAction, domain.AcquisitionWithdrawAction,
			domain.ZoneCreateAction, domain.ProductionBillAction, domain.OwnedDraftAction, domain.SubdueAction,
			domain.TendAction, domain.RescueAction, domain.CaptureAction, domain.UseItemAction, domain.HaulAction, domain.EquipAction, domain.DropEquipmentAction,
			domain.GearReplaceAction, domain.ApparelPolicyAction, domain.RecoveryServiceAction, domain.MovementAction, domain.HusbandryAction,
			domain.PrisonerInteractionAction, domain.RepairAction, domain.CleanAction, domain.MineAcquisitionAction, domain.OpenCasketAction,
			domain.BuildingTemperatureAction, domain.BedUseAction, domain.GrowerCropAction, domain.ClaimBuildingAction, domain.AutoRefuelAction, domain.SurgeryAction, domain.AutoHomeAreaAction, domain.PawnSettingsAction, domain.AreaAction, domain.PolicyPruneAction, domain.ReadingPolicyAction, domain.DrugPolicyAction, domain.FoodPolicyAction, domain.ZoneDeleteAction, domain.ZoneCellEditAction, domain.StockpilePatchAction, domain.AssignAction, domain.ExcavationAction, domain.DialogAnswerAction, domain.ResearchSelectAction, domain.TradeAction, domain.DeconstructionAction, domain.RemoveRoofAction, domain.AreaPlantCutAction, domain.CutPlantAction, domain.StripAction, domain.RulesAttachAction, domain.MoveBuildingAction, domain.UninstallBuildingAction, domain.CoverClearanceAction, domain.WastepackHaulAction, domain.FoundationRemovalAction, domain.FloorRemovalAction, domain.QuestAcceptAction, domain.RitualAction, domain.AbilityAction, domain.IgniteAction, domain.CloseDoorAction, domain.WallRemovalAction, domain.CaravanDepartureAction:
		default:
			return errors.New("routine execution requires supported routine methods")
		}
	}
	return nil
}

// Need returns the routine need the review binds the goal to.
func (r Rounds) Need(goal domain.ConcernID) (domain.ConcernID, bool) {
	for _, binding := range r.Standards {
		if binding.Standard == goal {
			return binding.Concern, true
		}
	}
	return "", false
}

// projectNeed returns the routine need the review binds the Project to.
func (r Rounds) projectNeed(project domain.ProjectID) (domain.ConcernID, bool) {
	for _, binding := range r.Projects {
		if binding.Project == project {
			return binding.Concern, true
		}
	}
	return "", false
}

// Veto asks the policy Safeguards (#1017) whether this review admits a proposal
// for the goal, returning the veto's reason or "". A goal the review does
// not bind (a player goal) is outside the routine Safeguards.
func (r Rounds) Veto(g domain.Standard) string { return r.veto(g, false) }

func (r Rounds) veto(g domain.Standard, hunterWeapons bool) string {
	need, bound := r.Need(g.ID)
	if !bound {
		return ""
	}
	ref, _ := r.refuse(policy.SafeguardProposal{Need: need, Priority: g.Priority, HunterWeapons: hunterWeapons})
	return ref.Reason
}

// Workable loads the Standard goal the review binds to need and reports whether a
// planner may work it: an active deficit the Safeguards admit (#1121). The goal
// is returned whenever the review binds one, workable or not.
func (s *Store) Workable(ctx context.Context, r Rounds, need policy.ConcernID) (StandardState, bool, error) {
	return s.workable(ctx, r, need, false)
}

// WorkableHunterWeapons is Workable for the hunters' weapon craft
// (policy.HunterWeaponCraft), which an emergency does not suspend.
func (s *Store) WorkableHunterWeapons(ctx context.Context, r Rounds) (StandardState, bool, error) {
	return s.workable(ctx, r, policy.EnsureFoodSupply, true)
}

func (s *Store) workable(ctx context.Context, r Rounds, need policy.ConcernID, hunterWeapons bool) (StandardState, bool, error) {
	id := domain.ConcernID("")
	for _, binding := range r.Standards {
		if binding.Concern == need {
			id = binding.Standard
		}
	}
	if id == "" {
		return StandardState{}, false, nil
	}
	goal, err := s.LoadStandard(ctx, id)
	if err != nil {
		return StandardState{}, false, err
	}
	return goal, goal.Standard.Status == domain.StandardOpen && goal.Standard.Finding == domain.FindingUnmet && r.veto(goal.Standard, hunterWeapons) == "", nil
}

// vetoAction asks the action Safeguards (#1018) at dispatch: a vetoed action is
// ErrActionVetoed with the Safeguard's reason and only that action is refused.
func vetoAction(ctx context.Context, tx *sql.Tx, a domain.Action) error {
	review, err := loadRounds(ctx, tx)
	if err != nil {
		return err
	}
	if ref, vetoed := policy.RefuseAction(policy.SafeguardContext{Enabled: review.Enabled, Emergency: review.Emergency, Unsafe: review.Unsafe}, a); vetoed {
		return fmt.Errorf("%w: action %s: %s: %s", ErrActionVetoed, a.ID(), ref.Safeguard, ref.Reason)
	}
	return nil
}

// needOf is the need the review binds the goal or Project row id to.
func (r Rounds) needOf(id string) (domain.ConcernID, bool) {
	if isProjectID(id) {
		return r.projectNeed(domain.ProjectID(id))
	}
	return r.Need(domain.ConcernID(id))
}
