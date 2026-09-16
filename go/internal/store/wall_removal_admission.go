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

// WallRemovalAdmission records the exact native occupant/site CAS evidence
// immediately before dispatch, mirroring HomeCoverageAdmission's shape: it is
// evidence, not a lease, so the executor must inspect and prepare again
// before dispatch after restart. TargetIdentity is the exact native thing
// this step clears: for the original demolition it must equal Original; for
// a backup removal it must equal the same-plan backup wall's own proven
// construction identity, cross-checked against that action's Progress below.
type WallRemovalAdmission struct {
	Snapshot       domain.GenerationSnapshot
	Tick           domain.Tick
	Original       string
	BackupOf       domain.ActionID
	TargetIdentity string
	SiteEligible   bool
}
type ActionWallRemovalAdmission struct {
	Action    domain.ActionID
	Admission WallRemovalAdmission
}

func validateWallRemovalAdmission(a domain.Action, p domain.Progress, admission WallRemovalAdmission) error {
	if err := admission.Snapshot.Validate(); err != nil {
		return err
	}
	v := p.View()
	if admission.Snapshot.Plan != v.Plan || admission.Snapshot.Revision != v.Revision || admission.Snapshot.Native == 0 || admission.Tick < 0 {
		return errors.New("admission plan or tick mismatch")
	}
	removal, ok := a.WallRemoval()
	if !ok || removal.Original() != admission.Original || removal.BackupOf() != admission.BackupOf {
		return errors.New("invalid wall removal admission")
	}
	if admission.BackupOf == "" {
		if admission.Original == "" || admission.TargetIdentity != admission.Original {
			return errors.New("original wall removal admission must target its proven original identity")
		}
	} else if admission.TargetIdentity == "" {
		return errors.New("backup wall removal admission requires a proven target identity")
	}
	return nil
}

// PrepareWallRemoval atomically records the exact occupant/site CAS evidence
// and prepares pending work, mirroring PrepareHomeCoverage. For a backup
// removal it additionally requires the same-plan backup wall's own Progress
// to already carry a completed, matching construction identity: dependency
// completion alone (CheckDependencies) proves ordering, not that the built
// object is the one this step is about to clear.
func (s *Store) PrepareWallRemoval(ctx context.Context, plan domain.PlanID, action domain.ActionID, admission WallRemovalAdmission) (domain.Progress, error) {
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
	if err = validateWallRemovalAdmission(a, p, admission); err != nil {
		return domain.Progress{}, err
	}
	if admission.BackupOf != "" {
		var backup domain.Progress
		backupFound := false
		for i, candidate := range state.Spec.Actions() {
			if candidate.ID() == admission.BackupOf {
				backup, backupFound = state.Progress[i], true
				break
			}
		}
		if !backupFound {
			return domain.Progress{}, ErrNotFound
		}
		bv := backup.View()
		identity, known := bv.Construction.Value()
		effect, ek := bv.Effect.Value()
		if bv.Stage != domain.Completed || !known || !ek || effect != domain.EffectCompleted || identity.Current != admission.TargetIdentity {
			return domain.Progress{}, errors.New("backup wall removal admission does not match its completed backup construction")
		}
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
	for _, old := range state.WallRemovalAdmissions {
		if old.Action == action && admission.Tick < old.Admission.Tick {
			return domain.Progress{}, errors.New("admission observation moved backwards")
		}
	}
	data, err := json.Marshal(admission)
	if err != nil {
		return domain.Progress{}, err
	}
	if _, err = tx.ExecContext(ctx, "INSERT INTO wall_removal_admissions(action_id,payload) VALUES(?,?) ON CONFLICT(action_id) DO UPDATE SET payload=excluded.payload", action, data); err != nil {
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

func loadWallRemovalAdmission(ctx context.Context, tx *sql.Tx, a domain.Action, p domain.Progress) (WallRemovalAdmission, bool, error) {
	var data []byte
	if err := tx.QueryRowContext(ctx, "SELECT payload FROM wall_removal_admissions WHERE action_id=?", a.ID()).Scan(&data); err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			return WallRemovalAdmission{}, false, nil
		}
		return WallRemovalAdmission{}, false, err
	}
	var admission WallRemovalAdmission
	if len(data) > 32768 {
		return WallRemovalAdmission{}, false, errors.New("wall removal admission exceeds bound")
	}
	decoder := json.NewDecoder(bytes.NewReader(data))
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(&admission); err != nil {
		return WallRemovalAdmission{}, false, err
	}
	if err := decoder.Decode(new(json.RawMessage)); err != io.EOF {
		return WallRemovalAdmission{}, false, errors.New("trailing admission data")
	}
	canonical, err := json.Marshal(admission)
	if err != nil {
		return WallRemovalAdmission{}, false, err
	}
	if !bytes.Equal(data, canonical) {
		return WallRemovalAdmission{}, false, errors.New("noncanonical admission record")
	}
	if err = validateWallRemovalAdmission(a, p, admission); err != nil {
		return WallRemovalAdmission{}, false, fmt.Errorf("invalid action %q admission: %w", a.ID(), err)
	}
	v := p.View()
	if (v.Stage == domain.Prepared || v.Attempt > 0) && !admission.Snapshot.Matches(v.Snapshot) {
		return WallRemovalAdmission{}, false, errors.New("admission and progress authority disagree")
	}
	if (v.Unresolved || v.Stage == domain.Completed || v.Stage == domain.Unsuccessful) && admission.Tick > v.Tick {
		return WallRemovalAdmission{}, false, errors.New("admission is newer than dispatched progress")
	}
	return admission, true, nil
}
