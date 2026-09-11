package store

import (
	"bytes"
	"context"
	"database/sql"
	"encoding/json"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

type ActionMeleeAdmission struct {
	Action    domain.ActionID
	Admission MeleeAdmission
}

func validateMeleeAdmission(a domain.Action, p domain.Progress, v MeleeAdmission) error {
	m, ok := a.MeleeAttack()
	progress := p.View()
	if !ok || v.Snapshot.Validate() != nil || v.Snapshot.Plan != progress.Plan || v.Snapshot.Revision != progress.Revision || v.Snapshot.Native == 0 || v.Snapshot.Direction == 0 || v.Tick < 0 || v.Pawn != m.Pawn() || v.Target != m.Target() || submissionID(v.PawnSnapshotToken) != nil || submissionID(v.TargetSnapshotToken) != nil {
		return errors.New("invalid melee admission")
	}
	claim := v.DraftClaim
	if claim.Action != m.DraftAction() || claim.Pawn != m.Pawn() || claim.Attempt == 0 || claim.Origin != v.Snapshot || submissionID(string(claim.Claim)) != nil || submissionID(string(claim.Session)) != nil {
		return errors.New("melee admission draft mismatch")
	}
	return nil
}

func validateMeleePrerequisite(ctx context.Context, tx *sql.Tx, state PlanState, action domain.ActionID, v MeleeAdmission, live bool) error {
	if err := checkClaimSession(ctx, tx, v.DraftClaim); err != nil {
		return err
	}
	var attack domain.Action
	for _, a := range state.Spec.Actions() {
		if a.ID() == action {
			attack = a
			break
		}
	}
	m, ok := attack.MeleeAttack()
	if !ok {
		return errors.New("melee action required")
	}
	for _, p := range state.Progress {
		if p.View().Action != m.DraftAction() {
			continue
		}
		cleanup, known := p.View().DraftCleanup.Value()
		claim, claimed := cleanup.Claim.Value()
		if !known || !claimed || claim != v.DraftClaim {
			return errors.New("melee prerequisite claim mismatch")
		}
		if live && (p.View().Stage != domain.Completed || p.View().Unresolved || cleanup.Stage != domain.DraftCleanupRequired || p.View().Tick > v.Tick || p.View().Snapshot != v.Snapshot) {
			return errors.New("melee prerequisite is not currently owned and completed")
		}
		return nil
	}
	return errors.New("melee prerequisite missing")
}

func loadMeleeAdmission(ctx context.Context, tx *sql.Tx, a domain.Action, p domain.Progress) (MeleeAdmission, bool, error) {
	var data []byte
	if err := tx.QueryRowContext(ctx, "SELECT payload FROM melee_admissions WHERE action_id=?", a.ID()).Scan(&data); err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			return MeleeAdmission{}, false, nil
		}
		return MeleeAdmission{}, false, err
	}
	var v MeleeAdmission
	if len(data) > 32768 {
		return v, false, errors.New("melee admission exceeds bound")
	}
	decoder := json.NewDecoder(bytes.NewReader(data))
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(&v); err != nil {
		return v, false, err
	}
	canonical, err := json.Marshal(v)
	if err != nil || !bytes.Equal(data, canonical) {
		return v, false, errors.New("noncanonical melee admission")
	}
	if err = validateMeleeAdmission(a, p, v); err != nil {
		return v, false, err
	}
	progress := p.View()
	if (progress.Stage == domain.Prepared || progress.Attempt > 0) && progress.Snapshot != v.Snapshot {
		return v, false, errors.New("melee admission authority mismatch")
	}
	if progress.Unresolved && v.Tick > progress.Tick {
		return v, false, errors.New("melee admission is newer than dispatch")
	}
	return v, true, nil
}

// PrepareMelee records both exact pawn snapshots and the verified prerequisite in
// the same transaction as preparation. Every dispatch rechecks the retained claim.
func (s *Store) PrepareMelee(ctx context.Context, plan domain.PlanID, action domain.ActionID, v MeleeAdmission) (domain.Progress, error) {
	tx, err := s.begin(ctx)
	if err != nil {
		return domain.Progress{}, err
	}
	defer tx.Rollback()
	state, err := load(ctx, tx, plan)
	if err != nil {
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
	if err = validateMeleeAdmission(a, p, v); err != nil {
		return domain.Progress{}, err
	}
	if err = validateMeleePrerequisite(ctx, tx, state, action, v, true); err != nil {
		return domain.Progress{}, err
	}
	before := p.View()
	if before.Unresolved || v.Tick < before.Tick {
		return domain.Progress{}, errors.New("melee admission cannot replace unresolved or newer progress")
	}
	switch before.Stage {
	case domain.Pending:
		p, err = p.Prepare(v.Snapshot, v.Tick)
		if err != nil {
			return domain.Progress{}, err
		}
	case domain.Prepared:
		if before.Snapshot != v.Snapshot {
			return domain.Progress{}, errors.New("prepared melee authority changed")
		}
	default:
		return domain.Progress{}, errors.New("melee admission requires pending or prepared work")
	}
	for _, old := range state.MeleeAdmissions {
		if old.Action == action && v.Tick < old.Admission.Tick {
			return domain.Progress{}, errors.New("melee admission moved backwards")
		}
	}
	data, err := json.Marshal(v)
	if err != nil || len(data) > 32768 {
		return domain.Progress{}, errors.New("melee admission exceeds bound")
	}
	if _, err = tx.ExecContext(ctx, "INSERT INTO melee_admissions(action_id,payload) VALUES(?,?) ON CONFLICT(action_id) DO UPDATE SET payload=excluded.payload", action, data); err != nil {
		return domain.Progress{}, err
	}
	if before.Stage == domain.Pending {
		event, err := json.Marshal(transition{Kind: "prepare", Snapshot: v.Snapshot, Tick: v.Tick})
		if err != nil {
			return domain.Progress{}, err
		}
		if _, err = tx.ExecContext(ctx, "INSERT INTO transitions(action_id,payload) VALUES(?,?)", action, event); err != nil {
			return domain.Progress{}, err
		}
	}
	return p, tx.Commit()
}

func guardMeleeAdvance(ctx context.Context, tx *sql.Tx, state PlanState, action domain.ActionID, event transition) error {
	var melee bool
	for _, a := range state.Spec.Actions() {
		if a.ID() == action {
			_, melee = a.MeleeAttack()
			break
		}
	}
	if !melee {
		return nil
	}
	if event.Kind == "prepare" {
		return errors.New("melee action requires PrepareMelee")
	}
	if event.Kind != "dispatch" {
		return nil
	}
	for _, record := range state.MeleeAdmissions {
		if record.Action != action {
			continue
		}
		if event.Snapshot != record.Admission.Snapshot || event.Tick < record.Admission.Tick {
			return errors.New("melee dispatch admission mismatch")
		}
		return validateMeleePrerequisite(ctx, tx, state, action, record.Admission, true)
	}
	return errors.New("melee dispatch lacks admission")
}
