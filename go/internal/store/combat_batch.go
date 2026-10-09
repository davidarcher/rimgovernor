package store

import (
	"context"
	"database/sql"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// CommitCombatBatch binds an immutable ordered batch beside its fight. Pending
// tactical work does not prevent a new protective batch; the executor still
// checks the Incident and current authority before dispatch.
func (s *Store) CommitCombatBatch(ctx context.Context, plan domain.PlanSpec) (PlanState, error) {
	if plan.Validate() != nil || len(plan.Actions()) != 1 {
		return PlanState{}, ErrConflict
	}
	batch, ok := plan.Actions()[0].CombatBatch()
	if !ok {
		return PlanState{}, ErrConflict
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return PlanState{}, err
	}
	defer tx.Rollback()
	var exists int
	if err = tx.QueryRowContext(ctx, "SELECT count(*) FROM plans WHERE id=?", plan.ID()).Scan(&exists); err != nil {
		return PlanState{}, err
	}
	if exists != 0 {
		got, err := load(ctx, tx, plan.ID())
		if err != nil {
			return PlanState{}, err
		}
		if len(got.Spec.Actions()) != 1 || got.Spec.Actions()[0] != plan.Actions()[0] {
			return PlanState{}, ErrConflict
		}
		return got, tx.Commit()
	}
	var incident domain.IncidentID
	if err = tx.QueryRowContext(ctx, "SELECT i.incident_id FROM incident_methods i JOIN combat_fights f ON f.plan_id=i.plan_id WHERE f.plan_id=? AND f.open=1", batch.Fight()).Scan(&incident); err != nil {
		return PlanState{}, err
	}
	state, err := loadIncident(ctx, tx, incident)
	if err != nil {
		return PlanState{}, err
	}
	if state.Incident.Closed {
		return PlanState{}, ErrConflict
	}
	if err = admitRoundsSafeguards(ctx, tx, state); err != nil {
		return PlanState{}, err
	}
	if err = bindOwnerMethod(ctx, tx, state, domain.MethodID("batch-"+string(plan.ID())), "", plan); err != nil {
		return PlanState{}, err
	}
	got, err := load(ctx, tx, plan.ID())
	if err != nil {
		return PlanState{}, err
	}
	return got, tx.Commit()
}

// RetireCombatBatch retains exact commands and outcomes for postmortem and
// duplicate prevention, removing settled execution from the bounded active set.
func (s *Store) RetireCombatBatch(ctx context.Context, id domain.PlanID) error {
	tx, err := s.begin(ctx)
	if err != nil {
		return err
	}
	defer tx.Rollback()
	p, err := load(ctx, tx, id)
	if err != nil {
		return err
	}
	if len(p.Progress) != 1 || p.Progress[0].Action().Kind() != domain.CombatBatchAction {
		return ErrConflict
	}
	v := p.Progress[0].View()
	if v.Unresolved || v.Stage != domain.Completed && v.Stage != domain.Unsuccessful {
		return nil
	}
	_, err = tx.ExecContext(ctx, "UPDATE plans SET retired=1 WHERE id=?", id)
	if err != nil {
		return err
	}
	return tx.Commit()
}

// supersedeCombatBatches transfers execution responsibility to a later recorded
// decision or completed restoration, while historical receipts stay untouched.
func supersedeCombatBatches(ctx context.Context, tx *sql.Tx, fight, next domain.PlanID, restored bool) error {
	if next != fight {
		successor, err := load(ctx, tx, next)
		if err != nil {
			return err
		}
		if len(successor.Progress) == 0 {
			return ErrConflict
		}
		b, ok := successor.Progress[0].Action().CombatBatch()
		if !ok || b.Fight() != fight || successor.Progress[0].View().Attempt == 0 {
			return ErrConflict
		}
	}
	rows, err := tx.QueryContext(ctx, "SELECT p.id FROM plans p JOIN actions a ON a.plan_id=p.id WHERE p.retired=0 AND a.kind='combat_batch' AND a.target=? AND p.id<>?", fight, next)
	if err != nil {
		return err
	}
	var ids []domain.PlanID
	for rows.Next() {
		var id domain.PlanID
		if err = rows.Scan(&id); err != nil {
			rows.Close()
			return err
		}
		ids = append(ids, id)
	}
	err = rows.Err()
	rows.Close()
	if err != nil {
		return err
	}
	kept, hasKept, err := loadCombatRestoration(ctx, tx)
	if err != nil {
		return err
	}
	for _, id := range ids {
		p, err := load(ctx, tx, id)
		if err != nil {
			return err
		}
		if len(p.Progress) != 1 {
			continue
		}
		progress := p.Progress[0]
		if progress.View().Attempt == 0 {
			if _, err = advanceInTransaction(ctx, tx, id, progress.Action().ID(), transition{Kind: "cancel"}); err != nil {
				return err
			}
			if _, err = tx.ExecContext(ctx, "UPDATE plans SET retired=1,superseded_by=? WHERE id=?", next, id); err != nil {
				return err
			}
			continue
		}
		batch, _ := progress.Action().CombatBatch()
		transferred := true
		if !restored {
			for _, o := range batch.Orders() {
				switch o.Kind {
				case "door":
					if !hasKept || kept.Owner != fight || !slices.ContainsFunc(kept.Doors, func(d CombatDoorRestoration) bool { return d.Cell == o.Cell }) {
						transferred = false
					}
				case "animal_area":
					if !hasKept || kept.Owner != fight || !slices.ContainsFunc(kept.Animals, func(a CombatAnimalRestoration) bool { return a.Pawn == o.Pawn }) {
						transferred = false
					}
				}
			}
		}
		if !transferred {
			continue
		}
		if _, err = tx.ExecContext(ctx, "UPDATE plans SET retired=1,superseded_by=? WHERE id=?", next, id); err != nil {
			return err
		}
	}
	return nil
}
