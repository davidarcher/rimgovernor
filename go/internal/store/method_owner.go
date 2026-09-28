package store

import (
	"context"
	"database/sql"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// methodOwner is what method admission reads of the goal or incident a
// goal_methods row binds to (#1019): the world it was last reviewed in,
// whether the autopilot owns it, the routine need it serves and its open
// plans. GoalState and IncidentState implement it.
type methodOwner interface {
	ownerSnapshot() domain.GenerationSnapshot
	ownerAutopilot() bool
	// ownerNeed is the routine need the owner serves under this review:
	// a goal's review binding, an incident's Kind.
	ownerNeed(RoutineReview) (domain.GoalID, bool)
	ownerPriority() int
	ownerPlans() []domain.PlanID
	ownerLabel() string
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
	if reason := review.vetoNeed(need, owner.ownerPriority()); reason != "" {
		return fmt.Errorf("%w: %s: %s", ErrNotAdmitted, owner.ownerLabel(), reason)
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

func (r RoutineReview) vetoNeed(need domain.GoalID, priority int) string {
	return policy.VetoProposal(policy.RuleContext{Enabled: r.Enabled, Emergency: r.Emergency}, policy.RuleProposal{Need: need, Priority: priority})
}
