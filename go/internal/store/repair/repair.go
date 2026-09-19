// Package repair holds the repair action family's admission record,
// validation and load logic, split out of internal/store for independent
// build/test caching.
package repair

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

type Admission struct {
	Snapshot                                  domain.GenerationSnapshot
	Tick                                      domain.Tick
	Pawn                                      domain.PawnID
	Structure                                 string
	Cell                                      domain.Cell
	PawnSnapshotToken, StructureSnapshotToken string
}
type ActionAdmission struct {
	Action    domain.ActionID
	Admission Admission
}

func idShaped(id string) error { _, err := domain.NewPlan(domain.PlanID(id), 1, nil); return err }

func ValidateAdmission(a domain.Action, p domain.Progress, admission Admission) error {
	if err := admission.Snapshot.Validate(); err != nil {
		return err
	}
	v := p.View()
	if admission.Snapshot.Plan != v.Plan || admission.Snapshot.Revision != v.Revision || admission.Snapshot.Native == 0 || admission.Tick < 0 {
		return errors.New("admission plan or tick mismatch")
	}
	// An OpenCasket (#460) records the same pawn/thing/cell CAS evidence a
	// repair does, so it shares this admission the way a claim shares the
	// building-patch admission.
	pawn, structure, cell, ok := admissionTarget(a)
	if !ok || pawn != admission.Pawn || structure != admission.Structure || cell != admission.Cell || idShaped(admission.PawnSnapshotToken) != nil || idShaped(admission.StructureSnapshotToken) != nil {
		return errors.New("invalid repair admission")
	}
	return nil
}

func admissionTarget(a domain.Action) (domain.PawnID, string, domain.Cell, bool) {
	if r, ok := a.Repair(); ok {
		return r.Pawn(), r.Structure(), r.Cell(), true
	}
	if o, ok := a.OpenCasket(); ok {
		return o.Pawn(), o.Casket(), o.Cell(), true
	}
	return "", "", domain.Cell{}, false
}

func LoadAdmission(ctx context.Context, tx *sql.Tx, a domain.Action, p domain.Progress) (Admission, bool, error) {
	var data []byte
	if err := tx.QueryRowContext(ctx, "SELECT payload FROM repair_admissions WHERE action_id=?", a.ID()).Scan(&data); err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			return Admission{}, false, nil
		}
		return Admission{}, false, err
	}
	var admission Admission
	if len(data) > 32768 {
		return Admission{}, false, errors.New("repair admission exceeds bound")
	}
	decoder := json.NewDecoder(bytes.NewReader(data))
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(&admission); err != nil {
		return Admission{}, false, err
	}
	if err := decoder.Decode(new(json.RawMessage)); err != io.EOF {
		return Admission{}, false, errors.New("trailing admission data")
	}
	canonical, err := json.Marshal(admission)
	if err != nil {
		return Admission{}, false, err
	}
	if !bytes.Equal(data, canonical) {
		return Admission{}, false, errors.New("noncanonical admission record")
	}
	if err = ValidateAdmission(a, p, admission); err != nil {
		return Admission{}, false, fmt.Errorf("invalid action %q admission: %w", a.ID(), err)
	}
	v := p.View()
	if (v.Stage == domain.Prepared || v.Attempt > 0) && !admission.Snapshot.Matches(v.Snapshot) {
		return Admission{}, false, errors.New("admission and progress authority disagree")
	}
	if (v.Unresolved || v.Stage == domain.Completed || v.Stage == domain.Unsuccessful) && admission.Tick > v.Tick {
		return Admission{}, false, errors.New("admission is newer than dispatched progress")
	}
	return admission, true, nil
}

func GuardDispatch(admissions []ActionAdmission, action domain.ActionID, snapshot domain.GenerationSnapshot, tick domain.Tick) bool {
	for _, record := range admissions {
		if record.Action == action && record.Admission.Snapshot == snapshot && record.Admission.Tick <= tick {
			return true
		}
	}
	return false
}

func Insert(ctx context.Context, tx *sql.Tx, action domain.ActionID, admission Admission) error {
	data, err := json.Marshal(admission)
	if err != nil {
		return err
	}
	_, err = tx.ExecContext(ctx, "INSERT INTO repair_admissions(action_id,payload) VALUES(?,?) ON CONFLICT(action_id) DO UPDATE SET payload=excluded.payload", action, data)
	return err
}
