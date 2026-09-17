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

// MineAcquisitionAdmission is AcquisitionAdmission's counterpart for the
// second, independently-registered mine-acquisition vertical (see
// domain.MineAcquisitionAction): the same exact-item CAS evidence shape
// (Thing/SnapshotToken), but validated against action.MineAcquisition()
// rather than action.Acquisition() and persisted to its own table, since a
// MineAcquisitionAction can never satisfy the acquisition subpackage's kind
// gate.
type MineAcquisitionAdmission struct {
	Snapshot      domain.GenerationSnapshot
	Tick          domain.Tick
	Thing         string
	SnapshotToken string
}
type ActionMineAcquisitionAdmission struct {
	Action    domain.ActionID
	Admission MineAcquisitionAdmission
}

func validateMineAcquisitionAdmission(a domain.Action, p domain.Progress, admission MineAcquisitionAdmission) error {
	if err := admission.Snapshot.Validate(); err != nil {
		return err
	}
	v := p.View()
	if admission.Snapshot.Plan != v.Plan || admission.Snapshot.Revision != v.Revision || admission.Tick < 0 {
		return errors.New("admission plan or tick mismatch")
	}
	acquisition, ok := a.MineAcquisition()
	if !ok || acquisition.Thing() != admission.Thing || submissionID(admission.SnapshotToken) != nil || admission.Snapshot.Native == 0 {
		return errors.New("invalid mine acquisition admission")
	}
	return nil
}

// PrepareMineAcquisition atomically records the exact mine-source CAS
// evidence and prepares pending work, mirroring PrepareAcquisition exactly
// but against the mine-acquisition vertical's own table and kind gate. This
// record is evidence, not a lease: the executor must inspect and prepare
// again before dispatch after restart, even when Prepared was persisted.
func (s *Store) PrepareMineAcquisition(ctx context.Context, plan domain.PlanID, action domain.ActionID, admission MineAcquisitionAdmission) (domain.Progress, error) {
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
	if err = validateMineAcquisitionAdmission(a, p, admission); err != nil {
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
	for _, old := range state.MineAcquisitionAdmissions {
		if old.Action == action && admission.Tick < old.Admission.Tick {
			return domain.Progress{}, errors.New("admission observation moved backwards")
		}
	}
	data, err := json.Marshal(admission)
	if err != nil {
		return domain.Progress{}, err
	}
	if _, err = tx.ExecContext(ctx, "INSERT INTO mine_acquisition_admissions(action_id,payload) VALUES(?,?) ON CONFLICT(action_id) DO UPDATE SET payload=excluded.payload", action, data); err != nil {
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

func loadMineAcquisitionAdmission(ctx context.Context, tx *sql.Tx, a domain.Action, p domain.Progress) (MineAcquisitionAdmission, bool, error) {
	var data []byte
	if err := tx.QueryRowContext(ctx, "SELECT payload FROM mine_acquisition_admissions WHERE action_id=?", a.ID()).Scan(&data); err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			return MineAcquisitionAdmission{}, false, nil
		}
		return MineAcquisitionAdmission{}, false, err
	}
	var admission MineAcquisitionAdmission
	if len(data) > 32768 {
		return MineAcquisitionAdmission{}, false, errors.New("mine acquisition admission exceeds bound")
	}
	decoder := json.NewDecoder(bytes.NewReader(data))
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(&admission); err != nil {
		return MineAcquisitionAdmission{}, false, err
	}
	if err := decoder.Decode(new(json.RawMessage)); err != io.EOF {
		return MineAcquisitionAdmission{}, false, errors.New("trailing admission data")
	}
	canonical, err := json.Marshal(admission)
	if err != nil {
		return MineAcquisitionAdmission{}, false, err
	}
	if !bytes.Equal(data, canonical) {
		return MineAcquisitionAdmission{}, false, errors.New("noncanonical admission record")
	}
	if err = validateMineAcquisitionAdmission(a, p, admission); err != nil {
		return MineAcquisitionAdmission{}, false, fmt.Errorf("invalid action %q admission: %w", a.ID(), err)
	}
	v := p.View()
	if (v.Stage == domain.Prepared || v.Attempt > 0) && !admission.Snapshot.Matches(v.Snapshot) {
		return MineAcquisitionAdmission{}, false, errors.New("admission and progress authority disagree")
	}
	if (v.Unresolved || v.Stage == domain.Completed || v.Stage == domain.Unsuccessful) && admission.Tick > v.Tick {
		return MineAcquisitionAdmission{}, false, errors.New("admission is newer than dispatched progress")
	}
	return admission, true, nil
}

func mineAcquisitionGuardDispatch(admissions []ActionMineAcquisitionAdmission, action domain.ActionID, snapshot domain.GenerationSnapshot, tick domain.Tick) bool {
	for _, record := range admissions {
		if record.Action == action && record.Admission.Snapshot == snapshot && record.Admission.Tick <= tick {
			return true
		}
	}
	return false
}
