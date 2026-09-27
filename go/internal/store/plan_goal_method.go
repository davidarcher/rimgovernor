package store

import (
	"context"
	"database/sql"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// PlanGoalMethod names the goal method a plan executes, retired or not; ok is
// false for a plan no goal admitted (a player building submission).
func (s *Store) PlanGoalMethod(ctx context.Context, plan domain.PlanID) (method domain.GoalMethod, ok bool, err error) {
	err = s.db.QueryRowContext(ctx, "SELECT goal_id, epoch, method_id FROM goal_methods WHERE plan_id=?", plan).Scan(&method.Goal, &method.Epoch, &method.Method)
	if errors.Is(err, sql.ErrNoRows) {
		return domain.GoalMethod{}, false, nil
	}
	if err != nil {
		return domain.GoalMethod{}, false, err
	}
	method.Plan = plan
	return method, true, nil
}
