package store

import (
	"context"
	"database/sql"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// PlanMethod is the goal method a plan executes and the planner's reason
// for admitting it, empty when it gave none (#846).
type PlanMethod struct {
	domain.GoalMethod
	Reason string
}

// PlanGoalMethod names the goal method a plan executes, retired or not; ok is
// false for a plan no goal admitted (a player building submission).
func (s *Store) PlanGoalMethod(ctx context.Context, plan domain.PlanID) (method PlanMethod, ok bool, err error) {
	var reason sql.NullString
	err = s.db.QueryRowContext(ctx, "SELECT goal_id, epoch, method_id, reason FROM goal_methods WHERE plan_id=?", plan).Scan(&method.Goal, &method.Epoch, &method.Method, &reason)
	if errors.Is(err, sql.ErrNoRows) {
		return PlanMethod{}, false, nil
	}
	if err != nil {
		return PlanMethod{}, false, err
	}
	method.Plan, method.Reason = plan, reason.String
	return method, true, nil
}
