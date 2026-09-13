package store

import (
	"context"
	"database/sql"
	"encoding/json"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store/ranged"
)

type ActionRangedAdmission = ranged.ActionAdmission

func validateRangedPrerequisite(ctx context.Context, tx *sql.Tx, state PlanState, action domain.ActionID, v MeleeAdmission, live bool) error {
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
	m, ok := attack.RangedAttack()
	if !ok {
		return errors.New("ranged attack action required")
	}
	for _, p := range state.Progress {
		if p.View().Action != m.DraftAction() {
			continue
		}
		cleanup, known := p.View().DraftCleanup.Value()
		claim, claimed := cleanup.Claim.Value()
		if !known || !claimed || claim != v.DraftClaim {
			return errors.New("ranged attack prerequisite claim mismatch")
		}
		if live && (p.View().Stage != domain.Completed || p.View().Unresolved || cleanup.Stage != domain.DraftCleanupRequired || p.View().Tick > v.Tick || p.View().Snapshot != v.Snapshot) {
			return errors.New("ranged attack prerequisite is not currently owned and completed")
		}
		return nil
	}
	return errors.New("ranged attack prerequisite missing")
}

// PrepareRangedAttack records both exact pawn snapshots and the verified
// prerequisite in the same transaction as preparation, mirroring PrepareMelee.
func (s *Store) PrepareRangedAttack(ctx context.Context, plan domain.PlanID, action domain.ActionID, v MeleeAdmission) (domain.Progress, error) {
	tx, err := s.begin(ctx)
	if err != nil {
		return domain.Progress{}, err
	}
	defer tx.Rollback()
	if err = guardGoalWork(ctx, tx, plan, v.Snapshot, v.Tick); err != nil {
		return domain.Progress{}, err
	}
	state, err := load(ctx, tx, plan)
	if err != nil {
		return domain.Progress{}, err
	}
	if err = state.Spec.CheckDependencies(action, state.Progress, v.Snapshot, v.Tick); err != nil {
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
	if err = ranged.ValidateAdmission(a, p, v); err != nil {
		return domain.Progress{}, err
	}
	if err = validateRangedPrerequisite(ctx, tx, state, action, v, true); err != nil {
		return domain.Progress{}, err
	}
	before := p.View()
	if before.Unresolved || v.Tick < before.Tick {
		return domain.Progress{}, errors.New("ranged attack admission cannot replace unresolved or newer progress")
	}
	switch before.Stage {
	case domain.Pending:
		p, err = p.Prepare(v.Snapshot, v.Tick)
		if err != nil {
			return domain.Progress{}, err
		}
	case domain.Prepared:
		if before.Snapshot != v.Snapshot {
			return domain.Progress{}, errors.New("prepared ranged attack authority changed")
		}
	default:
		return domain.Progress{}, errors.New("ranged attack admission requires pending or prepared work")
	}
	for _, old := range state.RangedAdmissions {
		if old.Action == action && v.Tick < old.Admission.Tick {
			return domain.Progress{}, errors.New("ranged attack admission moved backwards")
		}
	}
	if err = ranged.Insert(ctx, tx, action, v); err != nil {
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

func guardRangedAdvance(ctx context.Context, tx *sql.Tx, state PlanState, action domain.ActionID, event transition) error {
	var isRanged bool
	for _, a := range state.Spec.Actions() {
		if a.ID() == action {
			_, isRanged = a.RangedAttack()
			break
		}
	}
	if !isRanged {
		return nil
	}
	if event.Kind == "prepare" {
		return errors.New("ranged attack action requires PrepareRangedAttack")
	}
	if event.Kind != "dispatch" {
		return nil
	}
	for _, record := range state.RangedAdmissions {
		if record.Action != action {
			continue
		}
		if event.Snapshot != record.Admission.Snapshot || event.Tick < record.Admission.Tick {
			return errors.New("ranged attack dispatch admission mismatch")
		}
		return validateRangedPrerequisite(ctx, tx, state, action, record.Admission, true)
	}
	return errors.New("ranged attack dispatch lacks admission")
}
