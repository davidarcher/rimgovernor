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

// HomeCoverageAdmission records the exact target/shape/revision CAS evidence
// immediately before dispatch, mirroring BedAssignAdmission's shape: it is
// evidence, not a lease, so the executor must inspect and prepare again
// before dispatch after restart.
type HomeCoverageAdmission struct {
	Snapshot domain.GenerationSnapshot
	Tick     domain.Tick
	Target   string
	Shape    string
	Revision int64
	Missing  int64
	Excluded int64
}
type ActionHomeCoverageAdmission struct {
	Action    domain.ActionID
	Admission HomeCoverageAdmission
}

func validateHomeCoverageAdmission(a domain.Action, p domain.Progress, admission HomeCoverageAdmission) error {
	if err := admission.Snapshot.Validate(); err != nil {
		return err
	}
	v := p.View()
	if admission.Snapshot.Plan != v.Plan || admission.Snapshot.Revision != v.Revision || admission.Snapshot.Native == 0 || admission.Tick < 0 {
		return errors.New("admission plan or tick mismatch")
	}
	coverage, ok := a.HomeCoverage()
	if !ok || coverage.Target() != admission.Target || coverage.Shape() != admission.Shape {
		return errors.New("invalid home coverage admission")
	}
	if admission.Revision < 0 || admission.Excluded < 0 || admission.Excluded > admission.Missing || admission.Missing < 0 {
		return errors.New("invalid home coverage admission counts")
	}
	return nil
}

// PrepareHomeCoverage atomically records the exact target/shape/revision CAS
// evidence and prepares pending work, mirroring PrepareBedAssign.
func (s *Store) PrepareHomeCoverage(ctx context.Context, plan domain.PlanID, action domain.ActionID, admission HomeCoverageAdmission) (domain.Progress, error) {
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
	if err = validateHomeCoverageAdmission(a, p, admission); err != nil {
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
	for _, old := range state.HomeCoverageAdmissions {
		if old.Action == action && admission.Tick < old.Admission.Tick {
			return domain.Progress{}, errors.New("admission observation moved backwards")
		}
	}
	data, err := json.Marshal(admission)
	if err != nil {
		return domain.Progress{}, err
	}
	if _, err = tx.ExecContext(ctx, "INSERT INTO home_coverage_admissions(action_id,payload) VALUES(?,?) ON CONFLICT(action_id) DO UPDATE SET payload=excluded.payload", action, data); err != nil {
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

func loadHomeCoverageAdmission(ctx context.Context, tx *sql.Tx, a domain.Action, p domain.Progress) (HomeCoverageAdmission, bool, error) {
	var data []byte
	if err := tx.QueryRowContext(ctx, "SELECT payload FROM home_coverage_admissions WHERE action_id=?", a.ID()).Scan(&data); err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			return HomeCoverageAdmission{}, false, nil
		}
		return HomeCoverageAdmission{}, false, err
	}
	var admission HomeCoverageAdmission
	if len(data) > 32768 {
		return HomeCoverageAdmission{}, false, errors.New("home coverage admission exceeds bound")
	}
	decoder := json.NewDecoder(bytes.NewReader(data))
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(&admission); err != nil {
		return HomeCoverageAdmission{}, false, err
	}
	if err := decoder.Decode(new(json.RawMessage)); err != io.EOF {
		return HomeCoverageAdmission{}, false, errors.New("trailing admission data")
	}
	canonical, err := json.Marshal(admission)
	if err != nil {
		return HomeCoverageAdmission{}, false, err
	}
	if !bytes.Equal(data, canonical) {
		return HomeCoverageAdmission{}, false, errors.New("noncanonical admission record")
	}
	if err = validateHomeCoverageAdmission(a, p, admission); err != nil {
		return HomeCoverageAdmission{}, false, fmt.Errorf("invalid action %q admission: %w", a.ID(), err)
	}
	v := p.View()
	if (v.Stage == domain.Prepared || v.Attempt > 0) && !admission.Snapshot.Matches(v.Snapshot) {
		return HomeCoverageAdmission{}, false, errors.New("admission and progress authority disagree")
	}
	if (v.Unresolved || v.Stage == domain.Completed || v.Stage == domain.Unsuccessful) && admission.Tick > v.Tick {
		return HomeCoverageAdmission{}, false, errors.New("admission is newer than dispatched progress")
	}
	return admission, true, nil
}
