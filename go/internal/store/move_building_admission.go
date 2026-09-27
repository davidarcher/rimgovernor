package store

import (
	"bytes"
	"context"
	"database/sql"
	"encoding/json"
	"errors"
	"fmt"
	"io"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// MoveBuildingAdmission is the move vertical's exact-building evidence
// (#808): the building identity the native Reinstall preview accepted at
// Tick, in its own table since a MoveBuildingAction satisfies no other
// family's kind gate. Native re-evaluates every rule at apply; no token.
type MoveBuildingAdmission struct {
	Snapshot domain.GenerationSnapshot
	Tick     domain.Tick
	Thing    string
}
type ActionMoveBuildingAdmission struct {
	Action    domain.ActionID
	Admission MoveBuildingAdmission
}

func validateMoveBuildingAdmission(a domain.Action, p domain.Progress, admission MoveBuildingAdmission) error {
	if err := admission.Snapshot.Validate(); err != nil {
		return err
	}
	v := p.View()
	if admission.Snapshot.Plan != v.Plan || admission.Snapshot.Revision != v.Revision || admission.Tick < 0 {
		return errors.New("admission plan or tick mismatch")
	}
	move, _, ok := a.Relocation()
	if !ok || move.Thing() != admission.Thing || admission.Snapshot.Native == 0 {
		return errors.New("invalid move building admission")
	}
	return nil
}

// PrepareMoveBuilding atomically records the exact building evidence and
// prepares pending work, mirroring PrepareMoveBuilding against the move table
// and kind gate. The record is evidence, not a lease: the
// executor inspects and prepares again before dispatch after a restart.
func (s *Store) PrepareMoveBuilding(ctx context.Context, plan domain.PlanID, action domain.ActionID, admission MoveBuildingAdmission) (domain.Progress, error) {
	tx, err := s.begin(ctx)
	if err != nil {
		return domain.Progress{}, err
	}
	defer tx.Rollback()
	if err = guardGoalWork(ctx, tx, plan, admission.Snapshot, admission.Tick); err != nil {
		return domain.Progress{}, err
	}
	state, err := load(ctx, tx, plan)
	if err != nil {
		return domain.Progress{}, err
	}
	if err = checkDependencies(ctx, tx, state, action, admission.Snapshot, admission.Tick); err != nil {
		return domain.Progress{}, err
	}
	var a domain.Action
	var p domain.Progress
	found := false
	for i, candidate := range state.Spec.Actions() {
		if candidate.ID() == action {
			a, p, found = candidate, state.Progress[i], true
			break
		}
	}
	if !found {
		return domain.Progress{}, ErrNotFound
	}
	if err = validateMoveBuildingAdmission(a, p, admission); err != nil {
		return domain.Progress{}, err
	}
	v := p.View()
	if v.Unresolved || admission.Tick < v.Tick {
		return domain.Progress{}, errors.New("admission cannot replace unresolved or newer progress")
	}
	// A prepared action has no write outstanding (dispatch is recorded before
	// any native write), so authority that moved since its preparation
	// re-prepares it under the current snapshot instead of stranding it.
	prepare := v.Stage == domain.Pending || v.Stage == domain.Prepared && !v.Snapshot.Matches(admission.Snapshot)
	switch v.Stage {
	case domain.Pending, domain.Prepared:
		if prepare {
			p, err = p.Prepare(admission.Snapshot, admission.Tick)
			if err != nil {
				return domain.Progress{}, err
			}
		}
	default:
		return domain.Progress{}, errors.New("admission replacement requires pending or prepared work")
	}
	for _, old := range state.MoveBuildingAdmissions {
		if old.Action == action && admission.Tick < old.Admission.Tick {
			return domain.Progress{}, errors.New("admission observation moved backwards")
		}
	}
	data, err := json.Marshal(admission)
	if err != nil {
		return domain.Progress{}, err
	}
	if _, err = tx.ExecContext(ctx, "INSERT INTO move_building_admissions(action_id,payload) VALUES(?,?) ON CONFLICT(action_id) DO UPDATE SET payload=excluded.payload", action, data); err != nil {
		return domain.Progress{}, err
	}
	if prepare {
		event, err := json.Marshal(transition{Kind: "prepare", Snapshot: admission.Snapshot, Tick: admission.Tick})
		if err != nil {
			return domain.Progress{}, err
		}
		if _, err = tx.ExecContext(ctx, "INSERT INTO transitions(action_id,payload) VALUES(?,?)", action, event); err != nil {
			return domain.Progress{}, err
		}
	}
	if err = tx.Commit(); err != nil {
		return domain.Progress{}, err
	}
	return p, nil
}

func loadMoveBuildingAdmission(ctx context.Context, tx *sql.Tx, a domain.Action, p domain.Progress) (MoveBuildingAdmission, bool, error) {
	var data []byte
	if err := tx.QueryRowContext(ctx, "SELECT payload FROM move_building_admissions WHERE action_id=?", a.ID()).Scan(&data); err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			return MoveBuildingAdmission{}, false, nil
		}
		return MoveBuildingAdmission{}, false, err
	}
	var admission MoveBuildingAdmission
	if len(data) > 32768 {
		return MoveBuildingAdmission{}, false, errors.New("move building admission exceeds bound")
	}
	decoder := json.NewDecoder(bytes.NewReader(data))
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(&admission); err != nil {
		return MoveBuildingAdmission{}, false, err
	}
	if err := decoder.Decode(new(json.RawMessage)); err != io.EOF {
		return MoveBuildingAdmission{}, false, errors.New("trailing admission data")
	}
	canonical, err := json.Marshal(admission)
	if err != nil {
		return MoveBuildingAdmission{}, false, err
	}
	if !bytes.Equal(data, canonical) {
		return MoveBuildingAdmission{}, false, errors.New("noncanonical admission record")
	}
	if err = validateMoveBuildingAdmission(a, p, admission); err != nil {
		return MoveBuildingAdmission{}, false, fmt.Errorf("invalid action %q admission: %w", a.ID(), err)
	}
	v := p.View()
	if (v.Stage == domain.Prepared || v.Attempt > 0) && !admission.Snapshot.Matches(v.Snapshot) {
		return MoveBuildingAdmission{}, false, errors.New("admission and progress authority disagree")
	}
	if (v.Unresolved || v.Stage == domain.Completed || v.Stage == domain.Unsuccessful) && admission.Tick > v.Tick {
		return MoveBuildingAdmission{}, false, errors.New("admission is newer than dispatched progress")
	}
	return admission, true, nil
}

func moveBuildingGuardDispatch(admissions []ActionMoveBuildingAdmission, action domain.ActionID, snapshot domain.GenerationSnapshot, tick domain.Tick) bool {
	for _, record := range admissions {
		if record.Action == action && record.Admission.Snapshot == snapshot && record.Admission.Tick <= tick {
			return true
		}
	}
	return false
}
