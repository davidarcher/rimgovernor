package store

import (
	"context"
	"database/sql"
	"encoding/json"
	"errors"
	"strconv"

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
	var reason, episode, incidentKind sql.NullString
	var kind, owner string
	err = s.db.QueryRowContext(ctx, "SELECT m.kind, m.owner_id, i.kind, m.episode, m.method_id, m.reason FROM plan_methods m LEFT JOIN incidents i ON m.kind='incident' AND i.id=m.owner_id WHERE m.plan_id=?", plan).Scan(&kind, &owner, &incidentKind, &episode, &method.Method, &reason)
	if errors.Is(err, sql.ErrNoRows) {
		return PlanMethod{}, false, nil
	}
	if err != nil {
		return PlanMethod{}, false, err
	}
	if episode.Valid {
		if method.Episode, err = strconv.ParseUint(episode.String, 10, 64); err != nil {
			return PlanMethod{}, false, err
		}
	}
	switch kind {
	case "standard":
		method.Concern = domain.ConcernID(owner)
	case "incident":
		method.Incident, method.Concern = domain.IncidentID(owner), domain.ConcernID(incidentKind.String)
	case "project":
		method.Project = domain.ProjectID(owner)
	}
	if kind == "project" {
		var raw []byte
		if err = s.db.QueryRowContext(ctx, "SELECT payload FROM projects WHERE id=?", owner).Scan(&raw); err != nil {
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
