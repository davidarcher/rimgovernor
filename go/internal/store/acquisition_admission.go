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

type AcquisitionAdmission struct {
	Snapshot      domain.GenerationSnapshot
	Tick          domain.Tick
	Thing         string
	SnapshotToken string
}
type ActionAcquisitionAdmission struct {
	Action    domain.ActionID
	Admission AcquisitionAdmission
}

func validateAcquisitionAdmission(a domain.Action, p domain.Progress, admission AcquisitionAdmission) error {
	if err := admission.Snapshot.Validate(); err != nil {
		return err
	}
	v := p.View()
	if admission.Snapshot.Plan != v.Plan || admission.Snapshot.Revision != v.Revision || admission.Tick < 0 {
		return errors.New("admission plan or tick mismatch")
	}
	acquisition, ok := a.Acquisition()
	if !ok || acquisition.Thing() != admission.Thing || submissionID(admission.SnapshotToken) != nil || admission.Snapshot.Native == 0 || admission.Snapshot.Direction == 0 {
		return errors.New("invalid acquisition admission")
	}

	return nil
}

// PrepareAcquisition atomically records the exact item CAS evidence and prepares
// pending work. An unissued Prepared action may replace its record under exactly
// the same authority; its durable preparation tick remains unchanged. A refused
// failed receipt may prepare again, while unknown, completed and cancelled work
// can never lose its accounting record through this API.
// This record is evidence, not a lease. The executor must inspect and prepare
// again before dispatch after restart, even when Prepared was persisted.
func (s *Store) PrepareAcquisition(ctx context.Context, plan domain.PlanID, action domain.ActionID, admission AcquisitionAdmission) (domain.Progress, error) {
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
			a = candidate
			p = state.Progress[i]
			found = true
			break
		}
	}
	if !found {
		return domain.Progress{}, ErrNotFound
	}
	if err = validateAcquisitionAdmission(a, p, admission); err != nil {
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
	// Retain the newest validated observation even when preparation predates it.
	for _, old := range state.AcquisitionAdmissions {
		if old.Action == action && admission.Tick < old.Admission.Tick {
			return domain.Progress{}, errors.New("admission observation moved backwards")
		}
	}
	data, err := json.Marshal(admission)
	if err != nil {
		return domain.Progress{}, err
	}
	if _, err = tx.ExecContext(ctx, "INSERT INTO acquisition_admissions(action_id,payload) VALUES(?,?) ON CONFLICT(action_id) DO UPDATE SET payload=excluded.payload", action, data); err != nil {
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

func loadAcquisitionAdmission(ctx context.Context, tx *sql.Tx, a domain.Action, p domain.Progress) (AcquisitionAdmission, bool, error) {
	var data []byte
	if err := tx.QueryRowContext(ctx, "SELECT payload FROM acquisition_admissions WHERE action_id=?", a.ID()).Scan(&data); err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			return AcquisitionAdmission{}, false, nil
		}
		return AcquisitionAdmission{}, false, err
	}
	var admission AcquisitionAdmission
	if len(data) > 32768 {
		return AcquisitionAdmission{}, false, errors.New("acquisition admission exceeds bound")
	}
	decoder := json.NewDecoder(bytes.NewReader(data))
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(&admission); err != nil {
		return AcquisitionAdmission{}, false, err
	}
	if err := decoder.Decode(new(json.RawMessage)); err != io.EOF {
		return AcquisitionAdmission{}, false, errors.New("trailing admission data")
	}
	canonical, err := json.Marshal(admission)
	if err != nil {
		return AcquisitionAdmission{}, false, err
	}
	if !bytes.Equal(data, canonical) {
		return AcquisitionAdmission{}, false, errors.New("noncanonical admission record")
	}
	if err = validateAcquisitionAdmission(a, p, admission); err != nil {
		return AcquisitionAdmission{}, false, fmt.Errorf("invalid action %q admission: %w", a.ID(), err)
	}
	v := p.View()
	if (v.Stage == domain.Prepared || v.Attempt > 0) && !admission.Snapshot.Matches(v.Snapshot) {
		return AcquisitionAdmission{}, false, errors.New("admission and progress authority disagree")
	}
	if v.Unresolved && admission.Tick > v.Tick {
		return AcquisitionAdmission{}, false, errors.New("admission is newer than dispatched progress")
	}
	return admission, true, nil
}
