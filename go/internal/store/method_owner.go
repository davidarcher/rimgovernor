package store

import (
	"context"
	"database/sql"
	"errors"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// methodOwner is what method admission reads of the goal or incident a
// goal_methods row binds to (#1019): the world it was last reviewed in,
// whether the autopilot owns it, the routine need it serves and its open
// plans. GoalState, ProjectState and IncidentState implement it.
type methodOwner interface {
	ownerSnapshot() domain.GenerationSnapshot
	ownerAutopilot() bool
	// ownerNeed is the routine need the owner serves under this review:
	// a goal's review binding, an incident's Kind.
	ownerNeed(RoutineReview) (domain.GoalID, bool)
	ownerPriority() int
	ownerPlans() []domain.PlanID
	ownerLabel() string
	// ownerKey is the goal_methods owner column and id, and the epoch the
	// row is keyed under ("0" for owners without epochs).
	ownerKey() (column, id, epoch string)
}

func (g GoalState) ownerKey() (string, string, string) {
	column, epoch := methodOwnerColumn(g.Goal.ID, g.Goal.Epoch)
	return column, string(g.Goal.ID), epoch
}

func (g GoalState) ownerSnapshot() domain.GenerationSnapshot { return g.Goal.Snapshot }
func (g GoalState) ownerAutopilot() bool                     { return g.Goal.Source == domain.AutopilotGoal }
func (g GoalState) ownerNeed(r RoutineReview) (domain.GoalID, bool) {
	return r.Need(g.Goal.ID)
}
func (g GoalState) ownerPriority() int { return g.Goal.Priority }
func (g GoalState) ownerPlans() []domain.PlanID {
	out := make([]domain.PlanID, len(g.Methods))
	for i, m := range g.Methods {
		out[i] = m.Plan
	}
	return out
}
func (g GoalState) ownerLabel() string { return "goal " + string(g.Goal.ID) }

// admitRoutineRules is method admission's backstop for the Rules the
// planner already asked: a vetoed proposal is ErrNotAdmitted with the
// Rule's reason. An owner the review does not bind (a player goal) is
// outside the routine Rules.
func admitRoutineRules(ctx context.Context, tx *sql.Tx, owner methodOwner) error {
	review, err := loadRoutine(ctx, tx)
	if err != nil {
		return err
	}
	need, bound := owner.ownerNeed(review)
	if !bound {
		return nil
	}
	if ref, vetoed := review.refuseNeed(need, owner.ownerPriority()); vetoed {
		return fmt.Errorf("%w: %s: %s: %s", ErrNotAdmitted, owner.ownerLabel(), ref.Rule, ref.Reason)
	}
	return nil
}

// admitOwnerMethod runs the per-family admission checks every owner shares
// and stores the plan; the caller then writes the goal_methods row.
func admitOwnerMethod(ctx context.Context, tx *sql.Tx, owner methodOwner, plan domain.PlanSpec) error {
	for _, admit := range []func(context.Context, *sql.Tx, methodOwner, domain.PlanSpec) error{admitBillMethod, admitZoneMethod, admitAcquisitionMethod, admitWorkMethod, admitSupplyMethod} {
		if err := admit(ctx, tx, owner, plan); err != nil {
			return err
		}
	}
	return createPlan(ctx, tx, plan)
}

// bindOwnerMethod is the tail every owner's commit shares: refuse a method the
// owner already binds in its epoch, bound the owner's methods, run the family
// admission and store the plan, then write the goal_methods row and name the
// plan's method.
func bindOwnerMethod(ctx context.Context, tx *sql.Tx, owner methodOwner, method domain.MethodID, reason string, plan domain.PlanSpec) error {
	column, id, epoch := owner.ownerKey()
	// Plan ids are minted (#985); the real double-admission key is the
	// goal_methods unique index (owner, epoch, method).
	var bound string
	switch err := tx.QueryRowContext(ctx, "SELECT plan_id FROM goal_methods WHERE "+column+"=? AND epoch=? AND method_id=?", id, epoch, method).Scan(&bound); {
	case err == nil:
		return fmt.Errorf("%w: %s already binds method %s to plan %s", ErrConflict, owner.ownerLabel(), method, bound)
	case !errors.Is(err, sql.ErrNoRows):
		return err
	}
	if len(owner.ownerPlans()) >= 256 {
		return ErrCapacity
	}
	if err := admitOwnerMethod(ctx, tx, owner, plan); err != nil {
		return err
	}
	if _, err := tx.ExecContext(ctx, "INSERT INTO goal_methods("+column+",epoch,method_id,plan_id,priority,reason) VALUES(?,?,?,?,?,?)", id, epoch, method, plan.ID(), owner.ownerPriority(), sql.NullString{String: reason, Valid: reason != ""}); err != nil {
		return conflict(err)
	}
	_, err := tx.ExecContext(ctx, "UPDATE plans SET method_id=? WHERE id=?", method, plan.ID())
	return err
}

func (r RoutineReview) refuseNeed(need domain.GoalID, priority int) (policy.RuleRefusal, bool) {
	return policy.RefuseProposal(policy.RuleContext{Enabled: r.Enabled, Emergency: r.Emergency, Unsafe: r.Unsafe}, policy.RuleProposal{Need: need, Priority: priority})
}

func (r RoutineReview) vetoNeed(need domain.GoalID, priority int) string {
	ref, _ := r.refuseNeed(need, priority)
	return ref.Reason
}
