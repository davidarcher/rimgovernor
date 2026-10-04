package store

import (
	"context"
	"database/sql"
	"encoding/json"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// PlanMethod is the method a plan executes and the planner's reason
// for admitting it, empty when it gave none (#846).
type PlanMethod struct {
	// Concern is the Concern the method serves: a Standard's own id, an
	// Incident's or Project's kind.
	Concern domain.ConcernID
	Episode uint64
	Method  domain.MethodID
	Plan    domain.PlanID
	Reason  string
	// Incident is the occurrence that admitted the method, empty for a goal's.
	Incident domain.IncidentID
	// Project is the Project that admitted the method, empty otherwise; Concern
	// is then the Project's kind.
	Project domain.ProjectID
}

// PlanMethod names the method a plan executes, retired or not; ok is
// false for a plan no goal or incident admitted (a player building
// submission). An incident's method names its Response kind as Concern and
// the incident as Incident (#1020).
func (s *Store) PlanMethod(ctx context.Context, plan domain.PlanID) (method PlanMethod, ok bool, err error) {
	var reason, goal sql.NullString
	var incident, project, kind sql.NullString
	err = s.db.QueryRowContext(ctx, "SELECT m.standard_id, m.incident_id, m.project_id, i.kind, m.episode, m.method_id, m.reason FROM methods m LEFT JOIN incidents i ON i.id=m.incident_id WHERE m.plan_id=?", plan).Scan(&goal, &incident, &project, &kind, &method.Episode, &method.Method, &reason)
	if errors.Is(err, sql.ErrNoRows) {
		return PlanMethod{}, false, nil
	}
	if err != nil {
		return PlanMethod{}, false, err
	}
	method.Concern, method.Incident, method.Project = domain.ConcernID(goal.String), domain.IncidentID(incident.String), domain.ProjectID(project.String)
	if incident.Valid {
		method.Concern = domain.ConcernID(kind.String)
	}
	if project.Valid {
		var raw []byte
		if err = s.db.QueryRowContext(ctx, "SELECT payload FROM projects WHERE id=?", project.String).Scan(&raw); err != nil {
			return PlanMethod{}, false, err
		}
		var p domain.Project
		if err = json.Unmarshal(raw, &p); err != nil {
			return PlanMethod{}, false, err
		}
		method.Concern = p.Kind
	}
	method.Plan, method.Reason = plan, reason.String
	return method, true, nil
}
