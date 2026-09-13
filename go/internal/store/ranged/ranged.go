// Package ranged holds the ranged-attack action family's admission record,
// validation and load logic, split out of internal/store for independent
// build/test caching. Ranged attacks share their admission shape with melee
// (see internal/store/melee); the prerequisite-draft cross-check and dispatch
// guard stay in internal/store because they operate over the full plan
// aggregate (PlanState), which would otherwise create an import cycle.
package ranged

import (
	"bytes"
	"context"
	"database/sql"
	"encoding/json"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store/melee"
)

type Admission = melee.Admission
type ActionAdmission struct {
	Action    domain.ActionID
	Admission Admission
}

func idShaped(id string) error { _, err := domain.NewPlan(domain.PlanID(id), 1, nil); return err }

func ValidateAdmission(a domain.Action, p domain.Progress, v Admission) error {
	m, ok := a.RangedAttack()
	progress := p.View()
	if !ok || v.Snapshot.Validate() != nil || v.Snapshot.Plan != progress.Plan || v.Snapshot.Revision != progress.Revision || v.Snapshot.Native == 0 || v.Snapshot.Direction == 0 || v.Tick < 0 || v.Pawn != m.Pawn() || v.Target != m.Target() || idShaped(v.PawnSnapshotToken) != nil || idShaped(v.TargetSnapshotToken) != nil {
		return errors.New("invalid ranged attack admission")
	}
	claim := v.DraftClaim
	if claim.Action != m.DraftAction() || claim.Pawn != m.Pawn() || claim.Attempt == 0 || claim.Origin != v.Snapshot || idShaped(string(claim.Claim)) != nil || idShaped(string(claim.Session)) != nil {
		return errors.New("ranged attack admission draft mismatch")
	}
	return nil
}

func LoadAdmission(ctx context.Context, tx *sql.Tx, a domain.Action, p domain.Progress) (Admission, bool, error) {
	var data []byte
	if err := tx.QueryRowContext(ctx, "SELECT payload FROM ranged_admissions WHERE action_id=?", a.ID()).Scan(&data); err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			return Admission{}, false, nil
		}
		return Admission{}, false, err
	}
	var v Admission
	if len(data) > 32768 {
		return v, false, errors.New("ranged attack admission exceeds bound")
	}
	decoder := json.NewDecoder(bytes.NewReader(data))
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(&v); err != nil {
		return v, false, err
	}
	canonical, err := json.Marshal(v)
	if err != nil || !bytes.Equal(data, canonical) {
		return v, false, errors.New("noncanonical ranged attack admission")
	}
	if err = ValidateAdmission(a, p, v); err != nil {
		return v, false, err
	}
	progress := p.View()
	if (progress.Stage == domain.Prepared || progress.Attempt > 0) && progress.Snapshot != v.Snapshot {
		return v, false, errors.New("ranged attack admission authority mismatch")
	}
	if (progress.Unresolved || progress.Stage == domain.Completed || progress.Stage == domain.Unsuccessful) && v.Tick > progress.Tick {
		return v, false, errors.New("ranged attack admission is newer than dispatch")
	}
	return v, true, nil
}

func Insert(ctx context.Context, tx *sql.Tx, action domain.ActionID, v Admission) error {
	data, err := json.Marshal(v)
	if err != nil || len(data) > 32768 {
		return errors.New("ranged attack admission exceeds bound")
	}
	_, err = tx.ExecContext(ctx, "INSERT INTO ranged_admissions(action_id,payload) VALUES(?,?) ON CONFLICT(action_id) DO UPDATE SET payload=excluded.payload", action, data)
	return err
}
