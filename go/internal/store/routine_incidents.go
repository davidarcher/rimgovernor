package store

import (
	"context"
	"database/sql"
	"encoding/json"
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
	Kind     domain.ConcernID
	Subject  domain.PawnID `json:",omitempty"`
	Incident domain.IncidentID
	Need     domain.NeedState
}

// huntPayload is the payload of an ActiveCombat occurrence the food plan
// raised (#1617): the squad prey it opens on.
type huntPayload struct {
	Prey []domain.PawnID `json:"prey"`
}

// HuntPrey is the squad prey of an occurrence's latest assessment: non-empty
// while the food plan raises it with no hostile standing (its hunt origin).
func HuntPrey(i domain.Incident) []domain.PawnID {
	var p huntPayload
	if len(i.Payload) == 0 || json.Unmarshal(i.Payload, &p) != nil {
		return nil
	}
	return p.Prey
}

// Incident is the review's binding for kind's colony-wide occurrence.
func (r RoutineReview) Incident(kind domain.ConcernID) (RoutineIncident, bool) {
	for _, b := range r.Incidents {
		if b.Kind == kind && b.Subject == "" {
			return b, true
		}
	}
	return RoutineIncident{}, false
}

// SubjectIncidents are the review's bindings for kind's per-subject
// occurrences (EnsureMood: one per pawn), in assessment order.
func (r RoutineReview) SubjectIncidents(kind domain.ConcernID) []RoutineIncident {
	var out []RoutineIncident
	for _, b := range r.Incidents {
		if b.Kind == kind && b.Subject != "" {
			out = append(out, b)
		}
	}
	return out
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

// VetoIncident asks the Safeguards whether this review admits a proposal for
// the incident, returning the veto's reason or "".
func (r RoutineReview) VetoIncident(i domain.Incident) string {
	return r.vetoNeed(i.Kind, i.Priority)
}

func openIncidentID(ctx context.Context, tx *sql.Tx, world domain.GenerationSnapshot, kind domain.ConcernID, subject domain.PawnID) (domain.IncidentID, bool, error) {
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
		id, open, err := openIncidentID(ctx, tx, current, n.ID, n.Subject)
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
		assessment := IncidentAssessment{Kind: n.ID, Subject: n.Subject, Trigger: fmt.Sprintf("%s deficit", n.ID), Priority: n.Priority, Snapshot: current, Tick: tick}
		if len(n.Hunt) > 0 && n.Need == domain.NeedDeficit {
			// The food plan raised this occurrence: its hunt origin (#1617).
			if assessment.Payload, err = json.Marshal(huntPayload{Prey: n.Hunt}); err != nil {
				return nil, nil, err
			}
			assessment.Trigger = fmt.Sprintf("%s hunt", n.ID)
		}
		state, err := openIncident(ctx, tx, assessment)
		if err != nil {
			return nil, nil, err
		}
		bindings = append(bindings, RoutineIncident{Kind: n.ID, Subject: n.Subject, Incident: state.Incident.ID, Need: n.Need})
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
	rows, err := tx.QueryContext(ctx, "SELECT id FROM incidents WHERE colony=? AND load_token=? AND map_id=? AND ended_tick IS NULL ORDER BY started_tick,id LIMIT 513", world.Colony, world.Load, world.Map)
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
	if len(ids) > 512 {
		return nil, ErrCapacity
	}
	return ids, rows.Err()
}

// LatestIncident is kind's newest occurrence in world, open or closed.
func (s *Store) LatestIncident(ctx context.Context, world World, kind domain.ConcernID) (IncidentState, bool, error) {
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

// IncidentHistory is every occurrence of kind in world, open or closed,
// oldest first.
func (s *Store) IncidentHistory(ctx context.Context, world World, kind domain.ConcernID) ([]IncidentState, error) {
	tx, err := s.begin(ctx)
	if err != nil {
		return nil, err
	}
	defer tx.Rollback()
	rows, err := tx.QueryContext(ctx, "SELECT id FROM incidents WHERE colony=? AND load_token=? AND map_id=? AND kind=? ORDER BY started_tick, rowid", world.Colony, world.Load, world.Map, kind)
	if err != nil {
		return nil, err
	}
	var ids []domain.IncidentID
	for rows.Next() {
		var id domain.IncidentID
		if err = rows.Scan(&id); err != nil {
			rows.Close()
			return nil, err
		}
		ids = append(ids, id)
	}
	rows.Close()
	if err = rows.Err(); err != nil {
		return nil, err
	}
	out := make([]IncidentState, 0, len(ids))
	for _, id := range ids {
		state, err := loadIncident(ctx, tx, id)
		if err != nil {
			return nil, err
		}
		out = append(out, state)
	}
	return out, nil
}

func validateRoutineIncidents(ctx context.Context, tx *sql.Tx, r RoutineReview) error {
	if len(r.Incidents) > 512 {
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
