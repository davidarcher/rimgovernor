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

// ProductionCommitment and ProductionDrillTarget are the exact Commitments
// and Drills rows this vertical must resend verbatim on every
// SetProductionPolicy write, read fresh immediately before dispatch (see
// domain.ProductionPolicyAction's doc comment): another system may own them
// independently, and the native write replaces the whole map-scoped state in
// one call, so a stale or narrower value here would silently clobber them.
type ProductionCommitment struct {
	Resource string
	Count    int64
}
type ProductionDrillTarget struct {
	BuildingDef, ResourceDef string
	X, Z                     int32
	StockTarget              int64
}

type ProductionPolicyAdmission struct {
	Snapshot      domain.GenerationSnapshot
	Tick          domain.Tick
	Floors        []domain.ResourceFloor
	Stopped       []string
	Commitments   []ProductionCommitment
	Drills        []ProductionDrillTarget
	SnapshotToken string
}
type ActionProductionPolicyAdmission struct {
	Action    domain.ActionID
	Admission ProductionPolicyAdmission
}

func validateProductionPolicyAdmission(a domain.Action, p domain.Progress, admission ProductionPolicyAdmission) error {
	if err := admission.Snapshot.Validate(); err != nil {
		return err
	}
	v := p.View()
	if admission.Snapshot.Plan != v.Plan || admission.Snapshot.Revision != v.Revision || admission.Snapshot.Native == 0 || admission.Tick < 0 {
		return errors.New("admission plan or tick mismatch")
	}
	value, ok := a.ProductionPolicy()
	if !ok {
		return errors.New("invalid production policy admission")
	}
	canonical, err := domain.NewProductionPolicy(admission.Floors, admission.Stopped)
	if err != nil || canonical != value {
		return errors.New("invalid production policy admission")
	}
	if len(admission.Commitments) > 256 || len(admission.Drills) > 32 {
		return errors.New("production policy admission exceeds bound")
	}
	if submissionID(admission.SnapshotToken) != nil {
		return errors.New("invalid production policy admission token")
	}
	return nil
}

// PrepareProductionPolicy atomically records the exact floors/stopped
// replacement (plus the Commitments/Drills rows to resend verbatim and the
// CAS snapshot token the write must match) and prepares pending work. This
// record is evidence, not a lease: the executor must inspect and prepare
// again before dispatch after restart, even when Prepared was persisted.
func (s *Store) PrepareProductionPolicy(ctx context.Context, plan domain.PlanID, action domain.ActionID, admission ProductionPolicyAdmission) (domain.Progress, error) {
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
	if err = validateProductionPolicyAdmission(a, p, admission); err != nil {
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
	for _, old := range state.ProductionPolicyAdmissions {
		if old.Action == action && admission.Tick < old.Admission.Tick {
			return domain.Progress{}, errors.New("admission observation moved backwards")
		}
	}
	data, err := json.Marshal(admission)
	if err != nil {
		return domain.Progress{}, err
	}
	if _, err = tx.ExecContext(ctx, "INSERT INTO production_policy_admissions(action_id,payload) VALUES(?,?) ON CONFLICT(action_id) DO UPDATE SET payload=excluded.payload", action, data); err != nil {
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

func loadProductionPolicyAdmission(ctx context.Context, tx *sql.Tx, a domain.Action, p domain.Progress) (ProductionPolicyAdmission, bool, error) {
	var data []byte
	if err := tx.QueryRowContext(ctx, "SELECT payload FROM production_policy_admissions WHERE action_id=?", a.ID()).Scan(&data); err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			return ProductionPolicyAdmission{}, false, nil
		}
		return ProductionPolicyAdmission{}, false, err
	}
	var admission ProductionPolicyAdmission
	if len(data) > 32768 {
		return ProductionPolicyAdmission{}, false, errors.New("production policy admission exceeds bound")
	}
	decoder := json.NewDecoder(bytes.NewReader(data))
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(&admission); err != nil {
		return ProductionPolicyAdmission{}, false, err
	}
	if err := decoder.Decode(new(json.RawMessage)); err != io.EOF {
		return ProductionPolicyAdmission{}, false, errors.New("trailing admission data")
	}
	canonical, err := json.Marshal(admission)
	if err != nil {
		return ProductionPolicyAdmission{}, false, err
	}
	if !bytes.Equal(data, canonical) {
		return ProductionPolicyAdmission{}, false, errors.New("noncanonical admission record")
	}
	if err = validateProductionPolicyAdmission(a, p, admission); err != nil {
		return ProductionPolicyAdmission{}, false, fmt.Errorf("invalid action %q admission: %w", a.ID(), err)
	}
	v := p.View()
	if (v.Stage == domain.Prepared || v.Attempt > 0) && !admission.Snapshot.Matches(v.Snapshot) {
		return ProductionPolicyAdmission{}, false, errors.New("admission and progress authority disagree")
	}
	if (v.Unresolved || v.Stage == domain.Completed || v.Stage == domain.Unsuccessful) && admission.Tick > v.Tick {
		return ProductionPolicyAdmission{}, false, errors.New("admission is newer than dispatched progress")
	}
	return admission, true, nil
}
