// Package buildingtemperature holds the admission record, validation and
// load logic shared by the two building-patch action families (a building's
// target temperature and a bed's medical flag: one exact thing, one CAS
// token), split out of internal/store for independent build/test caching --
// mirroring internal/store/work.
package buildingtemperature

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
	Thing         string
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
	thing, before, ok := Patched(a)
	if !ok || thing != admission.Thing || idShaped(admission.SnapshotToken) != nil || admission.SnapshotToken != before || admission.Snapshot.Native == 0 {
		return errors.New("invalid building temperature admission")
	}
	return nil
}

// Patched returns the exact thing and CAS token of either building-patch
// action kind; ok is false for every other action.
func Patched(a domain.Action) (thing, before string, ok bool) {
	if bt, ok := a.BuildingTemperature(); ok {
		return bt.Thing(), bt.BeforeToken(), true
	}
	if bm, ok := a.BedMedical(); ok {
		return bm.Thing(), bm.BeforeToken(), true
	}
	return "", "", false
}

// Kind reports whether the action is one of the building-patch kinds this
// admission record guards.
func Kind(kind domain.ActionKind) bool {
	return kind == domain.BuildingTemperatureAction || kind == domain.BedMedicalAction
}

func LoadAdmission(ctx context.Context, tx *sql.Tx, a domain.Action, p domain.Progress) (Admission, bool, error) {
	var data []byte
	if err := tx.QueryRowContext(ctx, "SELECT payload FROM building_temperature_admissions WHERE action_id=?", a.ID()).Scan(&data); err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			return Admission{}, false, nil
		}
		return Admission{}, false, err
	}
	var admission Admission
	if len(data) > 32768 {
		return Admission{}, false, errors.New("building temperature admission exceeds bound")
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
	_, err = tx.ExecContext(ctx, "INSERT INTO building_temperature_admissions(action_id,payload) VALUES(?,?) ON CONFLICT(action_id) DO UPDATE SET payload=excluded.payload", action, data)
	return err
}
