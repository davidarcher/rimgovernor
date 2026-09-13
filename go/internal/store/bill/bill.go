// Package bill holds the production-bill action family's admission record,
// validation and load logic, split out of internal/store for independent
// build/test caching.
package bill

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
	Snapshot      domain.GenerationSnapshot
	Tick          domain.Tick
	Bench         string
	SnapshotToken string
}
type ActionAdmission struct {
	Action    domain.ActionID
	Admission Admission
}

// idShaped reuses the plan-ID format rules to validate an opaque token's
// character set and length, mirroring store.submissionID.
func idShaped(id string) error { _, err := domain.NewPlan(domain.PlanID(id), 1, nil); return err }

func ValidateAdmission(a domain.Action, p domain.Progress, admission Admission) error {
	if err := admission.Snapshot.Validate(); err != nil {
		return err
	}
	v := p.View()
	if admission.Snapshot.Plan != v.Plan || admission.Snapshot.Revision != v.Revision || admission.Tick < 0 {
		return errors.New("admission plan or tick mismatch")
	}
	bill, ok := a.ProductionBill()
	if !ok || string(bill.Bench()) != admission.Bench || idShaped(admission.SnapshotToken) != nil || admission.SnapshotToken != bill.BeforeToken() || admission.Snapshot.Native == 0 || admission.Snapshot.Direction == 0 {
		return errors.New("invalid bill admission")
	}
	return nil
}

func LoadAdmission(ctx context.Context, tx *sql.Tx, a domain.Action, p domain.Progress) (Admission, bool, error) {
	var data []byte
	if err := tx.QueryRowContext(ctx, "SELECT payload FROM bill_admissions WHERE action_id=?", a.ID()).Scan(&data); err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			return Admission{}, false, nil
		}
		return Admission{}, false, err
	}
	var admission Admission
	if len(data) > 32768 {
		return Admission{}, false, errors.New("bill admission exceeds bound")
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

// GuardDispatch reports whether a dispatch at snapshot/tick is covered by an
// existing admission record for action, mirroring the per-family matched-loop
// that store.advanceInTransaction runs for every action kind.
func GuardDispatch(admissions []ActionAdmission, action domain.ActionID, snapshot domain.GenerationSnapshot, tick domain.Tick) bool {
	for _, record := range admissions {
		if record.Action == action && record.Admission.Snapshot == snapshot && record.Admission.Tick <= tick {
			return true
		}
	}
	return false
}

// Insert records the canonical admission payload, used by Store.PrepareBill
// inside its own transaction.
func Insert(ctx context.Context, tx *sql.Tx, action domain.ActionID, admission Admission) error {
	data, err := json.Marshal(admission)
	if err != nil {
		return err
	}
	_, err = tx.ExecContext(ctx, "INSERT INTO bill_admissions(action_id,payload) VALUES(?,?) ON CONFLICT(action_id) DO UPDATE SET payload=excluded.payload", action, data)
	return err
}
