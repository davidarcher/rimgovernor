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

// CoverClearanceAdmission is the cover-clearance vertical's exact-thing CAS
// evidence (#581): the cover thing identity and the read-time census token
// the native ClearCover designation compares at apply, in its own table
// since a CoverClearanceAction satisfies no other family's kind gate.
type CoverClearanceAdmission struct {
	Snapshot      domain.GenerationSnapshot
	Tick          domain.Tick
	Thing         string
	SnapshotToken string
}
type ActionCoverClearanceAdmission struct {
	Action    domain.ActionID
	Admission CoverClearanceAdmission
}

func validateCoverClearanceAdmission(a domain.Action, p domain.Progress, admission CoverClearanceAdmission) error {
	if err := admission.Snapshot.Validate(); err != nil {
		return err
	}
	v := p.View()
	if admission.Snapshot.Plan != v.Plan || admission.Snapshot.Revision != v.Revision || admission.Tick < 0 {
		return errors.New("admission plan or tick mismatch")
	}
	acquisition, ok := a.CoverClearance()
	if !ok || acquisition.Thing() != admission.Thing || submissionID(admission.SnapshotToken) != nil || admission.Snapshot.Native == 0 {
		return errors.New("invalid cover clearance admission")
	}
	return nil
}

// PrepareCoverClearance atomically records the exact cover thing CAS
// evidence and prepares pending work, mirroring PrepareCoverClearance against the
// cover-clearance table and kind gate. The record is evidence, not a lease: the
// executor inspects and prepares again before dispatch after a restart.
func (s *Store) PrepareCoverClearance(ctx context.Context, plan domain.PlanID, action domain.ActionID, admission CoverClearanceAdmission) (domain.Progress, error) {
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
	if err = validateCoverClearanceAdmission(a, p, admission); err != nil {
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
	for _, old := range state.CoverClearanceAdmissions {
		if old.Action == action && admission.Tick < old.Admission.Tick {
			return domain.Progress{}, errors.New("admission observation moved backwards")
		}
	}
	data, err := json.Marshal(admission)
	if err != nil {
		return domain.Progress{}, err
	}
	if _, err = tx.ExecContext(ctx, "INSERT INTO cover_clearance_admissions(action_id,payload) VALUES(?,?) ON CONFLICT(action_id) DO UPDATE SET payload=excluded.payload", action, data); err != nil {
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

func loadCoverClearanceAdmission(ctx context.Context, tx *sql.Tx, a domain.Action, p domain.Progress) (CoverClearanceAdmission, bool, error) {
	var data []byte
	if err := tx.QueryRowContext(ctx, "SELECT payload FROM cover_clearance_admissions WHERE action_id=?", a.ID()).Scan(&data); err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			return CoverClearanceAdmission{}, false, nil
		}
		return CoverClearanceAdmission{}, false, err
	}
	var admission CoverClearanceAdmission
	if len(data) > 32768 {
		return CoverClearanceAdmission{}, false, errors.New("cover clearance admission exceeds bound")
	}
	decoder := json.NewDecoder(bytes.NewReader(data))
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(&admission); err != nil {
		return CoverClearanceAdmission{}, false, err
	}
	if err := decoder.Decode(new(json.RawMessage)); err != io.EOF {
		return CoverClearanceAdmission{}, false, errors.New("trailing admission data")
	}
	canonical, err := json.Marshal(admission)
	if err != nil {
		return CoverClearanceAdmission{}, false, err
	}
	if !bytes.Equal(data, canonical) {
		return CoverClearanceAdmission{}, false, errors.New("noncanonical admission record")
	}
	if err = validateCoverClearanceAdmission(a, p, admission); err != nil {
		return CoverClearanceAdmission{}, false, fmt.Errorf("invalid action %q admission: %w", a.ID(), err)
	}
	v := p.View()
	if (v.Stage == domain.Prepared || v.Attempt > 0) && !admission.Snapshot.Matches(v.Snapshot) {
		return CoverClearanceAdmission{}, false, errors.New("admission and progress authority disagree")
	}
	if (v.Unresolved || v.Stage == domain.Completed || v.Stage == domain.Unsuccessful) && admission.Tick > v.Tick {
		return CoverClearanceAdmission{}, false, errors.New("admission is newer than dispatched progress")
	}
	return admission, true, nil
}

func coverClearanceGuardDispatch(admissions []ActionCoverClearanceAdmission, action domain.ActionID, snapshot domain.GenerationSnapshot, tick domain.Tick) bool {
	for _, record := range admissions {
		if record.Action == action && record.Admission.Snapshot == snapshot && record.Admission.Tick <= tick {
			return true
		}
	}
	return false
}
