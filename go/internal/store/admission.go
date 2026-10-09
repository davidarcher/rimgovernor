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

// Admission is a zone's footprint record, written once at method admission
// (AdmitBuildingMethod); the zone_create intent prepares only under it.
// Building intents carry none: native validates a placement when it applies
// the intent.
type Admission struct {
	Snapshot  domain.GenerationSnapshot
	Tick      domain.Tick
	Footprint []domain.Cell
}

type ActionAdmission struct {
	Action    domain.ActionID
	Admission Admission
}

func validateAdmission(a domain.Action, p domain.Progress, admission Admission) error {
	if err := admission.Snapshot.Validate(); err != nil {
		return err
	}
	v := p.View()
	if admission.Snapshot.Plan != v.Plan || admission.Snapshot.Revision != v.Revision || admission.Tick < 0 {
		return errors.New("admission plan or tick mismatch")
	}
	zone, isZone := a.ZoneCreate()
	if !isZone {
		return errors.New("unsupported admission action")
	}
	if len(admission.Footprint) != len(zone.Cells()) || len(admission.Footprint) > 4096 {
		return errors.New("zone admission footprint mismatch")
	}
	seen := map[domain.Cell]bool{}
	for _, cell := range admission.Footprint {
		if cell.X < 0 || cell.Z < 0 || seen[cell] {
			return errors.New("invalid or duplicate admission cell")
		}
		seen[cell] = true
	}
	for _, cell := range zone.Cells() {
		if !seen[cell] {
			return errors.New("zone admission footprint differs")
		}
	}
	return nil
}

func loadAdmission(ctx context.Context, tx *sql.Tx, a domain.Action, p domain.Progress) (Admission, bool, error) {
	if a.Kind() != domain.ZoneCreateAction {
		return Admission{}, false, nil
	}
	var data []byte
	if err := tx.QueryRowContext(ctx, "SELECT payload FROM admissions WHERE action_id=?", a.ID()).Scan(&data); err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			return Admission{}, false, nil
		}
		return Admission{}, false, err
	}
	var admission Admission
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
	if err = validateAdmission(a, p, admission); err != nil {
		return Admission{}, false, fmt.Errorf("invalid action %q admission: %w", a.ID(), err)
	}
	v := p.View()
	// The footprint agrees with progress on the world, plan and revision
	// rather than on the native generation, which moves under it.
	recorded, progressed := admission.Snapshot, v.Snapshot
	recorded.Native, progressed.Native = 0, 0
	if (v.Stage == domain.Prepared || v.Attempt > 0) && !recorded.Matches(progressed) {
		return Admission{}, false, errors.New("admission and progress authority disagree")
	}
	if v.Unresolved && admission.Tick > v.Tick {
		return Admission{}, false, errors.New("admission is newer than dispatched progress")
	}
	return admission, true, nil
}
