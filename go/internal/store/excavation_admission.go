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

// ExcavationAdmission records the exact rock-cell CAS evidence for one
// domain.ExcavationAction: the cell, the expected rock definition and the
// native snapshot token observed while paused. Like the other admission
// tables this is evidence, not a lease: the executor re-inspects and
// prepares again before dispatch after restart.
type ExcavationAdmission struct {
	Snapshot      domain.GenerationSnapshot
	Tick          domain.Tick
	Cell          domain.Cell
	Definition    string
	SnapshotToken string
}
type ActionExcavationAdmission struct {
	Action    domain.ActionID
	Admission ExcavationAdmission
}

func validateExcavationAdmission(a domain.Action, p domain.Progress, admission ExcavationAdmission) error {
	if err := admission.Snapshot.Validate(); err != nil {
		return err
	}
	v := p.View()
	if admission.Snapshot.Plan != v.Plan || admission.Snapshot.Revision != v.Revision || admission.Tick < 0 {
		return errors.New("admission plan or tick mismatch")
	}
	excavation, ok := a.Excavation()
	if !ok || excavation.Cell() != admission.Cell || excavation.Definition() != admission.Definition || submissionID(admission.SnapshotToken) != nil || admission.Snapshot.Native == 0 || admission.Snapshot.Direction == 0 {
		return errors.New("invalid excavation admission")
	}
	return nil
}

// PrepareExcavation atomically records the exact rock-cell CAS evidence and
// prepares pending work, mirroring PrepareMineAcquisition against the
// excavation vertical's own table and kind gate.
func (s *Store) PrepareExcavation(ctx context.Context, plan domain.PlanID, action domain.ActionID, admission ExcavationAdmission) (domain.Progress, error) {
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
	if err = state.Spec.CheckDependencies(action, state.Progress, admission.Snapshot, admission.Tick); err != nil {
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
	if err = validateExcavationAdmission(a, p, admission); err != nil {
		return domain.Progress{}, err
	}
	v := p.View()
	if v.Unresolved || admission.Tick < v.Tick {
		return domain.Progress{}, errors.New("admission cannot replace unresolved or newer progress")
	}
	switch v.Stage {
	case domain.Pending:
		p, err = p.Prepare(admission.Snapshot, admission.Tick)
		if err != nil {
			return domain.Progress{}, err
		}
	case domain.Prepared:
		if !v.Snapshot.Matches(admission.Snapshot) {
			return domain.Progress{}, errors.New("prepared admission authority changed")
		}
	default:
		return domain.Progress{}, errors.New("admission replacement requires pending or prepared work")
	}
	for _, old := range state.ExcavationAdmissions {
		if old.Action == action && admission.Tick < old.Admission.Tick {
			return domain.Progress{}, errors.New("admission observation moved backwards")
		}
	}
	data, err := json.Marshal(admission)
	if err != nil {
		return domain.Progress{}, err
	}
	if _, err = tx.ExecContext(ctx, "INSERT INTO excavation_admissions(action_id,payload) VALUES(?,?) ON CONFLICT(action_id) DO UPDATE SET payload=excluded.payload", action, data); err != nil {
		return domain.Progress{}, err
	}
	if v.Stage == domain.Pending {
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

func loadExcavationAdmission(ctx context.Context, tx *sql.Tx, a domain.Action, p domain.Progress) (ExcavationAdmission, bool, error) {
	var data []byte
	if err := tx.QueryRowContext(ctx, "SELECT payload FROM excavation_admissions WHERE action_id=?", a.ID()).Scan(&data); err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			return ExcavationAdmission{}, false, nil
		}
		return ExcavationAdmission{}, false, err
	}
	var admission ExcavationAdmission
	if len(data) > 32768 {
		return ExcavationAdmission{}, false, errors.New("excavation admission exceeds bound")
	}
	decoder := json.NewDecoder(bytes.NewReader(data))
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(&admission); err != nil {
		return ExcavationAdmission{}, false, err
	}
	if err := decoder.Decode(new(json.RawMessage)); err != io.EOF {
		return ExcavationAdmission{}, false, errors.New("trailing admission data")
	}
	canonical, err := json.Marshal(admission)
	if err != nil {
		return ExcavationAdmission{}, false, err
	}
	if !bytes.Equal(data, canonical) {
		return ExcavationAdmission{}, false, errors.New("noncanonical admission record")
	}
	if err = validateExcavationAdmission(a, p, admission); err != nil {
		return ExcavationAdmission{}, false, fmt.Errorf("invalid action %q admission: %w", a.ID(), err)
	}
	v := p.View()
	if (v.Stage == domain.Prepared || v.Attempt > 0) && !admission.Snapshot.Matches(v.Snapshot) {
		return ExcavationAdmission{}, false, errors.New("admission and progress authority disagree")
	}
	if (v.Unresolved || v.Stage == domain.Completed || v.Stage == domain.Unsuccessful) && admission.Tick > v.Tick {
		return ExcavationAdmission{}, false, errors.New("admission is newer than dispatched progress")
	}
	return admission, true, nil
}

func excavationGuardDispatch(admissions []ActionExcavationAdmission, action domain.ActionID, snapshot domain.GenerationSnapshot, tick domain.Tick) bool {
	for _, record := range admissions {
		if record.Action == action && record.Admission.Snapshot == snapshot && record.Admission.Tick <= tick {
			return true
		}
	}
	return false
}
