package store

import (
	"context"
	"database/sql"
	"encoding/json"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store/melee"
)

type ActionMeleeAdmission = melee.ActionAdmission

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

// PrepareMelee records both exact pawn snapshots and the verified prerequisite in
// the same transaction as preparation. Every dispatch rechecks the retained claim.
func (s *Store) PrepareMelee(ctx context.Context, plan domain.PlanID, action domain.ActionID, v MeleeAdmission) (domain.Progress, error) {
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
	if err = melee.ValidateAdmission(a, p, v); err != nil {
		return domain.Progress{}, err
	}
	if err = validateMeleePrerequisite(ctx, tx, state, action, v, true); err != nil {
		return domain.Progress{}, err
	}
	before := p.View()
	if before.Unresolved || v.Tick < before.Tick {
		return domain.Progress{}, errors.New("melee admission cannot replace unresolved or newer progress")
	}
	// A prepared action has no write outstanding (dispatch is recorded before
	// any native write), so authority that moved since its preparation
	// re-prepares it under the current snapshot instead of stranding it.
	prepare := before.Stage == domain.Pending || before.Stage == domain.Prepared && !before.Snapshot.Matches(v.Snapshot)
	switch before.Stage {
	case domain.Pending, domain.Prepared:
		if prepare {
			p, err = p.Prepare(v.Snapshot, v.Tick)
			if err != nil {
				return domain.Progress{}, err
			}
		}
	default:
		return domain.Progress{}, errors.New("melee admission requires pending or prepared work")
	}
	for _, old := range state.MeleeAdmissions {
		if old.Action == action && v.Tick < old.Admission.Tick {
			return domain.Progress{}, errors.New("melee admission moved backwards")
		}
	}
	if err = melee.Insert(ctx, tx, action, v); err != nil {
		return domain.Progress{}, err
	}
	if prepare {
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
