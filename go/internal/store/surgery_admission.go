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

// SurgeryAdmission mirrors BedAssignAdmission's shape: the exact patient/
// recipe/part triple plus the two CAS tokens (the patient's exact pawn
// snapshot token and a separate health-signature token proving the exact
// eligible hediff/body-part state the recipe was accepted against) and the
// exact expected medical care policy bucket, both of which invalidate a
// stale admission the same way NativeRecoveryOperations.Token invalidates a
// stale building service order.
type SurgeryAdmission struct {
	Snapshot             domain.GenerationSnapshot
	Tick                 domain.Tick
	Patient              domain.PawnID
	Recipe               string
	Part                 int32
	PatientSnapshotToken string
	HealthToken          string
	Care                 domain.MedicalCare
}
type ActionSurgeryAdmission struct {
	Action    domain.ActionID
	Admission SurgeryAdmission
}

func validateSurgeryAdmission(a domain.Action, p domain.Progress, admission SurgeryAdmission) error {
	if err := admission.Snapshot.Validate(); err != nil {
		return err
	}
	v := p.View()
	if admission.Snapshot.Plan != v.Plan || admission.Snapshot.Revision != v.Revision || admission.Snapshot.Native == 0 || admission.Snapshot.Direction == 0 || admission.Tick < 0 {
		return errors.New("admission plan or tick mismatch")
	}
	surgery, ok := a.Surgery()
	if !ok || surgery.Patient() != admission.Patient || surgery.Recipe() != admission.Recipe || surgery.Part() != admission.Part {
		return errors.New("invalid surgery admission")
	}
	if submissionID(admission.PatientSnapshotToken) != nil || submissionID(admission.HealthToken) != nil || !domain.ValidMedicalCare(admission.Care) {
		return errors.New("invalid surgery admission tokens")
	}
	return nil
}

// PrepareSurgery atomically records the exact patient/recipe/part CAS evidence
// and prepares pending work. This record is evidence, not a lease; the
// executor must inspect and prepare again before dispatch after restart.
func (s *Store) PrepareSurgery(ctx context.Context, plan domain.PlanID, action domain.ActionID, admission SurgeryAdmission) (domain.Progress, error) {
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
	if err = validateSurgeryAdmission(a, p, admission); err != nil {
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
	for _, old := range state.SurgeryAdmissions {
		if old.Action == action && admission.Tick < old.Admission.Tick {
			return domain.Progress{}, errors.New("admission observation moved backwards")
		}
	}
	data, err := json.Marshal(admission)
	if err != nil {
		return domain.Progress{}, err
	}
	if _, err = tx.ExecContext(ctx, "INSERT INTO surgery_admissions(action_id,payload) VALUES(?,?) ON CONFLICT(action_id) DO UPDATE SET payload=excluded.payload", action, data); err != nil {
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

func loadSurgeryAdmission(ctx context.Context, tx *sql.Tx, a domain.Action, p domain.Progress) (SurgeryAdmission, bool, error) {
	var data []byte
	if err := tx.QueryRowContext(ctx, "SELECT payload FROM surgery_admissions WHERE action_id=?", a.ID()).Scan(&data); err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			return SurgeryAdmission{}, false, nil
		}
		return SurgeryAdmission{}, false, err
	}
	var admission SurgeryAdmission
	if len(data) > 32768 {
		return SurgeryAdmission{}, false, errors.New("surgery admission exceeds bound")
	}
	decoder := json.NewDecoder(bytes.NewReader(data))
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(&admission); err != nil {
		return SurgeryAdmission{}, false, err
	}
	if err := decoder.Decode(new(json.RawMessage)); err != io.EOF {
		return SurgeryAdmission{}, false, errors.New("trailing admission data")
	}
	canonical, err := json.Marshal(admission)
	if err != nil {
		return SurgeryAdmission{}, false, err
	}
	if !bytes.Equal(data, canonical) {
		return SurgeryAdmission{}, false, errors.New("noncanonical admission record")
	}
	if err = validateSurgeryAdmission(a, p, admission); err != nil {
		return SurgeryAdmission{}, false, fmt.Errorf("invalid action %q admission: %w", a.ID(), err)
	}
	v := p.View()
	if (v.Stage == domain.Prepared || v.Attempt > 0) && !admission.Snapshot.Matches(v.Snapshot) {
		return SurgeryAdmission{}, false, errors.New("admission and progress authority disagree")
	}
	if (v.Unresolved || v.Stage == domain.Completed || v.Stage == domain.Unsuccessful) && admission.Tick > v.Tick {
		return SurgeryAdmission{}, false, errors.New("admission is newer than dispatched progress")
	}
	return admission, true, nil
}
