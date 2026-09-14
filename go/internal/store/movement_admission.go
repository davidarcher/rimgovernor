package store

import (
	"context"
	"database/sql"
	"encoding/json"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store/movement"
)

type ActionMovementAdmission = movement.ActionAdmission

func validateMovementPrerequisite(ctx context.Context, tx *sql.Tx, state PlanState, action domain.ActionID, v MovementAdmission, live bool) error {
	if err := checkClaimSession(ctx, tx, v.DraftClaim); err != nil {
		return err
	}
	var move domain.Action
	for _, a := range state.Spec.Actions() {
		if a.ID() == action {
			move = a
			break
		}
	}
	m, ok := move.Movement()
	if !ok {
		return errors.New("movement action required")
	}
	for _, p := range state.Progress {
		if p.View().Action != m.DraftAction() {
			continue
		}
		cleanup, known := p.View().DraftCleanup.Value()
		claim, claimed := cleanup.Claim.Value()
		if !known || !claimed || claim != v.DraftClaim {
			return errors.New("movement prerequisite claim mismatch")
		}
		if live && (p.View().Stage != domain.Completed || p.View().Unresolved || cleanup.Stage != domain.DraftCleanupRequired || p.View().Tick > v.Tick || p.View().Snapshot != v.Snapshot) {
			return errors.New("movement prerequisite is not currently owned and completed")
		}
		return nil
	}
	return errors.New("movement prerequisite missing")
}

// PrepareMovement records both exact pawn snapshot and the verified
// prerequisite in the same transaction as preparation, mirroring PrepareMelee
// and PrepareRangedAttack.
func (s *Store) PrepareMovement(ctx context.Context, plan domain.PlanID, action domain.ActionID, v MovementAdmission) (domain.Progress, error) {
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
	if err = movement.ValidateAdmission(a, p, v); err != nil {
		return domain.Progress{}, err
	}
	if err = validateMovementPrerequisite(ctx, tx, state, action, v, true); err != nil {
		return domain.Progress{}, err
	}
	before := p.View()
	if before.Unresolved || v.Tick < before.Tick {
		return domain.Progress{}, errors.New("movement admission cannot replace unresolved or newer progress")
	}
	switch before.Stage {
	case domain.Pending:
		p, err = p.Prepare(v.Snapshot, v.Tick)
		if err != nil {
			return domain.Progress{}, err
		}
	case domain.Prepared:
		if before.Snapshot != v.Snapshot {
			return domain.Progress{}, errors.New("prepared movement authority changed")
		}
	default:
		return domain.Progress{}, errors.New("movement admission requires pending or prepared work")
	}
	for _, old := range state.MovementAdmissions {
		if old.Action == action && v.Tick < old.Admission.Tick {
			return domain.Progress{}, errors.New("movement admission moved backwards")
		}
	}
	if err = movement.Insert(ctx, tx, action, v); err != nil {
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

func guardMovementAdvance(ctx context.Context, tx *sql.Tx, state PlanState, action domain.ActionID, event transition) error {
	var isMovement bool
	for _, a := range state.Spec.Actions() {
		if a.ID() == action {
			_, isMovement = a.Movement()
			break
		}
	}
	if !isMovement {
		return nil
	}
	if event.Kind == "prepare" {
		return errors.New("movement action requires PrepareMovement")
	}
	if event.Kind != "dispatch" {
		return nil
	}
	for _, record := range state.MovementAdmissions {
		if record.Action != action {
			continue
		}
		if event.Snapshot != record.Admission.Snapshot || event.Tick < record.Admission.Tick {
			return errors.New("movement dispatch admission mismatch")
		}
		return validateMovementPrerequisite(ctx, tx, state, action, record.Admission, true)
	}
	return errors.New("movement dispatch lacks admission")
}
