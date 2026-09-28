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

// RoutineIncident binds a Response the review assessed to its open
// occurrence (#1020): the incident a planner commits methods to, and the
// need the review measured for it. A recovered occurrence stays bound, and
// open, while its dispatched work (a fight's drafts) settles.
type RoutineIncident struct {
	Kind     domain.GoalID
	Subject  domain.PawnID `json:",omitempty"`
	Incident domain.IncidentID
	Need     domain.NeedState
}

// Incident is the review's binding for kind's colony-wide occurrence.
func (r RoutineReview) Incident(kind domain.GoalID) (RoutineIncident, bool) {
	for _, b := range r.Incidents {
		if b.Kind == kind && b.Subject == "" {
			return b, true
		}
	}
	return RoutineIncident{}, false
}

// incidentBinding is the review's binding for an incident id.
func (r RoutineReview) incidentBinding(id domain.IncidentID) (RoutineIncident, bool) {
	for _, b := range r.Incidents {
		if b.Incident == id {
			return b, true
		}
	}
	return RoutineIncident{}, false
}

// VetoIncident asks the Rules whether this review admits a proposal for
// the incident, returning the veto's reason or "".
func (r RoutineReview) VetoIncident(i domain.Incident) string {
	return r.vetoNeed(i.Kind, i.Priority)
}

// goalAssessments are the assessments the review files goal rows for:
// every one but the incident kinds.
func goalAssessments(all []policy.RoutineAssessment) []policy.RoutineAssessment {
	return slices.DeleteFunc(slices.Clone(all), func(a policy.RoutineAssessment) bool { return policy.IsIncidentKind(a.ID) })
}

func openIncidentID(ctx context.Context, tx *sql.Tx, world domain.GenerationSnapshot, kind domain.GoalID, subject domain.PawnID) (domain.IncidentID, bool, error) {
	var id domain.IncidentID
	err := tx.QueryRowContext(ctx, "SELECT id FROM incidents WHERE colony=? AND load_token=? AND map_id=? AND kind=? AND subject=? AND ended_tick IS NULL", world.Colony, world.Load, world.Map, kind, subject).Scan(&id)
	if errors.Is(err, sql.ErrNoRows) {
		return "", false, nil
	}
	return id, err == nil, err
}

// reviewIncidents opens, refreshes and closes the incident kinds'
// occurrences from an enabled review's assessments: a deficit opens (or
// re-triggers) its occurrence; a recovered one closes once no work is
// open, as a goal satisfies; an unknown one keeps an open occurrence and
// opens none. An open occurrence the review no longer binds closes.
func reviewIncidents(ctx context.Context, tx *sql.Tx, assessments []policy.RoutineAssessment, current domain.GenerationSnapshot, tick domain.Tick) ([]RoutineIncident, []IncidentState, error) {
	var bindings []RoutineIncident
	var states []IncidentState
	for _, n := range assessments {
		if !policy.IsIncidentKind(n.ID) {
			continue
		}
		id, open, err := openIncidentID(ctx, tx, current, n.ID, "")
		if err != nil {
			return nil, nil, err
		}
		if n.Need != domain.NeedDeficit && !open {
			continue
		}
		if n.Need == domain.NeedRecovered {
			state, err := loadIncident(ctx, tx, id)
			if err != nil {
				return nil, nil, err
			}
			if err = cancelUndispatchedGoalMethods(ctx, tx, state); err != nil {
				return nil, nil, err
			}
			work, err := goalOpenWork(ctx, tx, state)
			if err != nil {
				return nil, nil, err
			}
			if !work {
				if _, err = closeIncident(ctx, tx, id, tick); err != nil {
					return nil, nil, err
				}
				continue
			}
		}
		state, err := openIncident(ctx, tx, IncidentAssessment{Kind: n.ID, Trigger: fmt.Sprintf("%s deficit", n.ID), Priority: n.Priority, Snapshot: current, Tick: tick})
		if err != nil {
			return nil, nil, err
		}
		bindings = append(bindings, RoutineIncident{Kind: n.ID, Incident: state.Incident.ID, Need: n.Need})
		states = append(states, state)
	}
	stale, err := openIncidentIDs(ctx, tx, World{Colony: current.Colony, Load: current.Load, Map: current.Map})
	if err != nil {
		return nil, nil, err
	}
	for _, id := range stale {
		if !slices.ContainsFunc(bindings, func(b RoutineIncident) bool { return b.Incident == id }) {
			if err = abandonIncident(ctx, tx, id, tick); err != nil {
				return nil, nil, err
			}
		}
	}
	return bindings, states, nil
}

// abandonIncident closes an occurrence whose world was replaced, rewound
// or stopped assessing it, cancelling all its work as an invalidated
// goal's is.
func abandonIncident(ctx context.Context, tx *sql.Tx, id domain.IncidentID, tick domain.Tick) error {
	state, err := loadIncident(ctx, tx, id)
	if err != nil || state.Incident.Closed {
		return err
	}
	if err = cancelGoalMethods(ctx, tx, state); err != nil {
		return err
	}
	_, err = closeIncident(ctx, tx, id, max(tick, state.Incident.Started))
	return err
}

func openIncidentIDs(ctx context.Context, tx *sql.Tx, world World) ([]domain.IncidentID, error) {
	rows, err := tx.QueryContext(ctx, "SELECT id FROM incidents WHERE colony=? AND load_token=? AND map_id=? AND ended_tick IS NULL ORDER BY started_tick,id LIMIT 257", world.Colony, world.Load, world.Map)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var ids []domain.IncidentID
	for rows.Next() {
		var id domain.IncidentID
		if err = rows.Scan(&id); err != nil {
			return nil, err
		}
		ids = append(ids, id)
	}
	if len(ids) > 256 {
		return nil, ErrCapacity
	}
	return ids, rows.Err()
}

// LatestIncident is kind's newest occurrence in world, open or closed.
func (s *Store) LatestIncident(ctx context.Context, world World, kind domain.GoalID) (IncidentState, bool, error) {
	tx, err := s.begin(ctx)
	if err != nil {
		return IncidentState{}, false, err
	}
	defer tx.Rollback()
	var id domain.IncidentID
	err = tx.QueryRowContext(ctx, "SELECT id FROM incidents WHERE colony=? AND load_token=? AND map_id=? AND kind=? ORDER BY started_tick DESC, rowid DESC LIMIT 1", world.Colony, world.Load, world.Map, kind).Scan(&id)
	if errors.Is(err, sql.ErrNoRows) {
		return IncidentState{}, false, nil
	}
	if err != nil {
		return IncidentState{}, false, err
	}
	state, err := loadIncident(ctx, tx, id)
	return state, err == nil, err
}

func validateRoutineIncidents(ctx context.Context, tx *sql.Tx, r RoutineReview) error {
	if len(r.Incidents) > 256 {
		return errors.New("invalid routine incident bindings")
	}
	seen := map[RoutineIncident]bool{}
	for _, b := range r.Incidents {
		key := RoutineIncident{Kind: b.Kind, Subject: b.Subject}
		if !policy.IsIncidentKind(b.Kind) || seen[key] {
			return errors.New("invalid routine incident binding")
		}
		seen[key] = true
		switch b.Need {
		case domain.NeedDeficit, domain.NeedRecovered, domain.NeedUnknown:
		default:
			return errors.New("invalid routine incident need")
		}
		state, err := loadIncident(ctx, tx, b.Incident)
		if err != nil {
			return err
		}
		if state.Incident.Closed || state.Incident.Kind != b.Kind || state.Incident.Subject != b.Subject || !state.Incident.Snapshot.SameWorld(r.Snapshot) {
			return errors.New("routine incident binding mismatch")
		}
	}
	return nil
}
