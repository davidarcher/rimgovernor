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
	// Incident is the occurrence that admitted the method, empty for a goal's.
	Incident domain.IncidentID
}

// PlanGoalMethod names the goal method a plan executes, retired or not; ok is
// false for a plan no goal or incident admitted (a player building
// submission). An incident's method names its Response kind as Goal and
// the incident as Incident (#1020).
func (s *Store) PlanGoalMethod(ctx context.Context, plan domain.PlanID) (method PlanMethod, ok bool, err error) {
	var reason, goal sql.NullString
	var incident, kind sql.NullString
	err = s.db.QueryRowContext(ctx, "SELECT m.goal_id, m.incident_id, i.kind, m.epoch, m.method_id, m.reason FROM goal_methods m LEFT JOIN incidents i ON i.id=m.incident_id WHERE m.plan_id=?", plan).Scan(&goal, &incident, &kind, &method.Epoch, &method.Method, &reason)
	if errors.Is(err, sql.ErrNoRows) {
		return PlanMethod{}, false, nil
	}
	if err != nil {
		return PlanMethod{}, false, err
	}
	method.Goal, method.Incident = domain.GoalID(goal.String), domain.IncidentID(incident.String)
	if incident.Valid {
		method.Goal = domain.GoalID(kind.String)
	}
	method.Plan, method.Reason = plan, reason.String
	return method, true, nil
}
