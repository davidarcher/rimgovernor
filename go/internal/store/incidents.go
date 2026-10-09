package store

import (
	"context"
	"database/sql"
	"encoding/json"
	"errors"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// IncidentState is one Response occurrence and the methods bound to it
// (#1019). Its methods live in methods beside the goals' ones, with
// incident_id set instead of standard_id, and go through the same admission.
type IncidentState struct {
	Incident domain.Incident
	Methods  []IncidentMethod // every method ever committed, retired plans included
}

type IncidentMethod struct {
	Method domain.MethodID
	Plan   domain.PlanID
}

// IncidentAssessment is what a review asserts about one occurrence: its key
// (Snapshot's world, Kind, Subject), the Trigger it opens with and the
// Priority the assessment ranks it at.
type IncidentAssessment struct {
	Kind     domain.ConcernID
	Subject  domain.PawnID
	Trigger  string
	Priority int
	Snapshot domain.GenerationSnapshot
	Tick     domain.Tick
	Payload  json.RawMessage
}

func (i IncidentState) ownerSnapshot() domain.GenerationSnapshot { return i.Incident.Snapshot }
func (i IncidentState) ownerNeed(Rounds) (domain.ConcernID, bool) {
	return i.Incident.Kind, true
}
func (i IncidentState) ownerPriority() int { return i.Incident.Priority }
func (i IncidentState) ownerPlans() []domain.PlanID {
	out := make([]domain.PlanID, len(i.Methods))
	for n, m := range i.Methods {
		out[n] = m.Plan
	}
	return out
}
func (i IncidentState) ownerLabel() string { return "incident " + string(i.Incident.ID) }

// OpenIncident opens the occurrence the assessment asserts, or, when one is
// already open under its key, refreshes that row's Priority, Snapshot and
// Payload from the assessment and keeps its ID, start and Trigger: a
// re-trigger inside an occurrence is idempotent.
func (s *Store) OpenIncident(ctx context.Context, a IncidentAssessment) (IncidentState, error) {
	tx, err := s.begin(ctx)
	if err != nil {
		return IncidentState{}, err
	}
	defer tx.Rollback()
	out, err := openIncident(ctx, tx, a)
	if err != nil {
		return IncidentState{}, err
	}
	return out, tx.Commit()
}

func openIncident(ctx context.Context, tx *sql.Tx, a IncidentAssessment) (IncidentState, error) {
	next := domain.Incident{Kind: a.Kind, Subject: a.Subject, Trigger: a.Trigger, Priority: a.Priority, Snapshot: a.Snapshot, Started: a.Tick, Payload: a.Payload}
	var id domain.IncidentID
	err := tx.QueryRowContext(ctx, "SELECT id FROM incidents WHERE colony=? AND load_token=? AND map_id=? AND kind=? AND subject=? AND ended_tick IS NULL", a.Snapshot.Colony, a.Snapshot.Load, a.Snapshot.Map, a.Kind, a.Subject).Scan(&id)
	switch {
	case errors.Is(err, sql.ErrNoRows):
		next.ID = domain.MintIncidentID()
		data, err := canonicalIncident(next)
		if err != nil {
			return IncidentState{}, err
		}
		if _, err = tx.ExecContext(ctx, "INSERT INTO incidents(id,colony,load_token,map_id,kind,subject,started_tick,payload) VALUES(?,?,?,?,?,?,?,?)", next.ID, a.Snapshot.Colony, a.Snapshot.Load, a.Snapshot.Map, a.Kind, a.Subject, a.Tick, data); err != nil {
			return IncidentState{}, conflict(err)
		}
	case err != nil:
		return IncidentState{}, err
	default:
		state, err := loadIncident(ctx, tx, id)
		if err != nil {
			return IncidentState{}, err
		}
		if a.Tick < state.Incident.Started {
			return IncidentState{}, fmt.Errorf("%w: incident %s assessed before it started", ErrConflict, id)
		}
		refreshed := state.Incident
		refreshed.Priority, refreshed.Snapshot, refreshed.Payload = a.Priority, a.Snapshot, a.Payload
		data, err := canonicalIncident(refreshed)
		if err != nil {
			return IncidentState{}, err
		}
		if _, err = tx.ExecContext(ctx, "UPDATE incidents SET payload=? WHERE id=?", data, id); err != nil {
			return IncidentState{}, err
		}
		next.ID = id
	}
	return loadIncident(ctx, tx, next.ID)
}

// CloseIncident ends the occurrence at tick; the next assessment under its
// key opens a new row. Methods no step dispatched are cancelled, as a
// recovered goal's are (#290); dispatched work settles through its journal.
func (s *Store) CloseIncident(ctx context.Context, id domain.IncidentID, tick domain.Tick) (IncidentState, error) {
	tx, err := s.begin(ctx)
	if err != nil {
		return IncidentState{}, err
	}
	defer tx.Rollback()
	state, err := closeIncident(ctx, tx, id, tick)
	if err != nil {
		return IncidentState{}, err
	}
	return state, tx.Commit()
}

func closeIncident(ctx context.Context, tx *sql.Tx, id domain.IncidentID, tick domain.Tick) (IncidentState, error) {
	state, err := loadIncident(ctx, tx, id)
	if err != nil {
		return IncidentState{}, err
	}
	if state.Incident.Closed {
		return IncidentState{}, fmt.Errorf("%w: incident %s already closed", ErrConflict, id)
	}
	closed := state.Incident
	closed.Closed, closed.Ended = true, tick
	data, err := canonicalIncident(closed)
	if err != nil {
		return IncidentState{}, err
	}
	if err = cancelUndispatchedMethods(ctx, tx, state); err != nil {
		return IncidentState{}, err
	}
	if _, err = tx.ExecContext(ctx, "UPDATE incidents SET ended_tick=?,payload=? WHERE id=?", tick, data, id); err != nil {
		return IncidentState{}, err
	}
	return loadIncident(ctx, tx, id)
}

func (s *Store) LoadIncident(ctx context.Context, id domain.IncidentID) (IncidentState, error) {
	tx, err := s.begin(ctx)
	if err != nil {
		return IncidentState{}, err
	}
	defer tx.Rollback()
	return loadIncident(ctx, tx, id)
}

// OpenIncidents lists the world's open occurrences, oldest first.
func (s *Store) OpenIncidents(ctx context.Context, world World) ([]IncidentState, error) {
	tx, err := s.begin(ctx)
	if err != nil {
		return nil, err
	}
	defer tx.Rollback()
	rows, err := tx.QueryContext(ctx, "SELECT id FROM incidents WHERE colony=? AND load_token=? AND map_id=? AND ended_tick IS NULL ORDER BY started_tick,id LIMIT 257", world.Colony, world.Load, world.Map)
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
	err = rows.Err()
	rows.Close()
	if err != nil {
		return nil, err
	}
	if len(ids) > 256 {
		return nil, ErrCapacity
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

func canonicalIncident(i domain.Incident) ([]byte, error) {
	if err := i.Validate(); err != nil {
		return nil, err
	}
	return json.Marshal(i)
}

func loadIncident(ctx context.Context, tx *sql.Tx, id domain.IncidentID) (IncidentState, error) {
	var out IncidentState
	var data []byte
	if err := tx.QueryRowContext(ctx, "SELECT payload FROM incidents WHERE id=?", id).Scan(&data); err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			err = ErrNotFound
		}
		return out, err
	}
	if err := json.Unmarshal(data, &out.Incident); err != nil {
		return IncidentState{}, err
	}
	if err := out.Incident.Validate(); err != nil {
		return IncidentState{}, err
	}
	if out.Incident.ID != id {
		return IncidentState{}, errors.New("incident identity mismatch")
	}
	rows, err := tx.QueryContext(ctx, "SELECT m.method_id,m.plan_id FROM incident_methods m JOIN plans p ON p.id=m.plan_id WHERE m.incident_id=? AND (p.retired=0 OR (m.method_id NOT LIKE 'batch-%' AND m.method_id NOT LIKE 'restore-%')) ORDER BY m.method_id LIMIT 257", id)
	if err != nil {
		return IncidentState{}, err
	}
	defer rows.Close()
	for rows.Next() {
		var m IncidentMethod
		if err = rows.Scan(&m.Method, &m.Plan); err != nil {
			return IncidentState{}, err
		}
		out.Methods = append(out.Methods, m)
	}
	return out, rows.Err()
}

// CommitIncidentMethod stores a method for an open incident and its shared
// plan atomically, through the Safeguards and per-family admission a goal's
// method takes. Like CommitMethod it grants no authority to dispatch.
func (s *Store) CommitIncidentMethod(ctx context.Context, id domain.IncidentID, method domain.MethodID, reason string, plan domain.PlanSpec) (IncidentState, error) {
	if err := plan.Validate(); err != nil {
		return IncidentState{}, err
	}
	if len(plan.Actions()) == 0 {
		return IncidentState{}, errors.New("empty incident method")
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return IncidentState{}, err
	}
	defer tx.Rollback()
	state, err := commitIncidentMethod(ctx, tx, id, method, reason, plan, false)
	if err != nil {
		return IncidentState{}, err
	}
	return state, tx.Commit()
}

// fightWork admits the method beside the incident's open fight (#1079):
// only a plan still open refuses it.
func commitIncidentMethod(ctx context.Context, tx *sql.Tx, id domain.IncidentID, method domain.MethodID, reason string, plan domain.PlanSpec, fightWork bool) (IncidentState, error) {
	state, err := loadIncident(ctx, tx, id)
	if err != nil {
		return IncidentState{}, err
	}
	if state.Incident.Closed {
		return IncidentState{}, fmt.Errorf("%w: incident %s is closed", ErrConflict, id)
	}
	if err = admitRoundsSafeguards(ctx, tx, state); err != nil {
		return IncidentState{}, err
	}
	open, err := standardOpenWork(ctx, tx, state)
	if err == nil && open && fightWork {
		open, err = planOpenWork(ctx, tx, state)
	}
	if err != nil {
		return IncidentState{}, err
	}
	if open && state.Incident.Kind == domain.ConcernID(policy.RecoverDisasterServices) && recoveryAreaOnly(plan) {
		open, err = recoveryAreaConflictingWork(ctx, tx, state)
		if err != nil {
			return IncidentState{}, err
		}
	}
	if open {
		return IncidentState{}, ErrOpenMethod
	}
	if err = (domain.Method{Owner: domain.ConcernID(id), Method: method, Plan: plan.ID()}).Validate(); err != nil {
		return IncidentState{}, err
	}
	if err = bindOwnerMethod(ctx, tx, state, method, reason, plan); err != nil {
		return IncidentState{}, err
	}
	return loadIncident(ctx, tx, id)
}

// guardIncidentWork is guardGoalWork for an incident's plan: the occurrence
// is open in the current world and no Safeguard vetoes it.
func guardIncidentWork(ctx context.Context, tx *sql.Tx, id domain.IncidentID, current domain.GenerationSnapshot, tick domain.Tick) error {
	state, err := loadIncident(ctx, tx, id)
	if err != nil {
		return err
	}
	i := state.Incident
	if i.Closed || !i.Snapshot.SameWorld(current) || tick < i.Started {
		return errors.New("incident does not admit current work")
	}
	return admitRoundsSafeguards(ctx, tx, state)
}

// A protective area setting may coexist with ordinary service jobs on the
// recovery Incident. All other open methods retain exclusive admission.
func recoveryAreaOnly(plan domain.PlanSpec) bool {
	if len(plan.Actions()) == 0 {
		return false
	}
	for _, action := range plan.Actions() {
		work, assignment := action.WorkAssignment()
		animal, husbandry := action.Husbandry()
		if !(assignment && work.HasArea() && len(work.Settings()) == 0 && !work.HasSchedule() || husbandry && animal.Method() == domain.HusbandryAllowedArea) {
			return false
		}
	}
	return true
}

func recoveryAreaConflictingWork(ctx context.Context, tx *sql.Tx, state IncidentState) (bool, error) {
	for _, id := range state.ownerPlans() {
		plan, err := load(ctx, tx, id)
		if err != nil {
			return false, err
		}
		if held, err := combatFightHolds(ctx, tx, id); err != nil || held {
			return held, err
		}
		if !PlanOpen(plan) {
			continue
		}
		for _, progress := range plan.Progress {
			if domain.StandardWorkOpen([]domain.Progress{progress}) && progress.Action().Kind() != domain.RecoveryServiceAction {
				return true, nil
			}
		}
	}
	return false, nil
}
