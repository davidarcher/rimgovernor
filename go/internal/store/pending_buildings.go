package store

import (
	"context"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// PendingBuildingAnchors are the anchor cells of building intents not yet
// applied in current's world on plans other than own (#943): nothing on the
// map shows them, so siting keeps off them. The store knows no definition
// sizes; native validation refuses a placement over the rest of a
// multi-cell footprint. An applied, refused or cancelled intent holds
// nothing (the census shows what it placed). Unprepared work carries no
// world of its own; a world change cancels it.
func (s *Store) PendingBuildingAnchors(ctx context.Context, current domain.GenerationSnapshot, own []domain.PlanID) ([]domain.Cell, error) {
	if err := current.Validate(); err != nil {
		return nil, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return nil, err
	}
	defer tx.Rollback()
	plans, err := loadPlans(ctx, tx, 256)
	if err != nil {
		return nil, err
	}
	methods := map[domain.PlanID]bool{}
	rows, err := tx.QueryContext(ctx, "SELECT plan_id FROM methods")
	if err != nil {
		return nil, err
	}
	for rows.Next() {
		var plan domain.PlanID
		if err = rows.Scan(&plan); err != nil {
			rows.Close()
			return nil, err
		}
		methods[plan] = true
	}
	if err = rows.Close(); err != nil {
		return nil, err
	}
	var cells []domain.Cell
	for _, p := range plans {
		if slices.Contains(own, p.Spec.ID()) {
			continue
		}
		for _, progress := range p.Progress {
			building, ok := progress.Action().Building()
			v := progress.View()
			if !ok || !v.Unresolved && (v.Stage == domain.Completed || v.Stage == domain.Unsuccessful || v.Stage == domain.Cancelled) {
				continue
			}
			started := v.Stage != domain.Pending || v.Attempt > 0 || v.Unresolved
			// Unstarted work holds cells only once admitted as a goal's method.
			if !started && !methods[p.Spec.ID()] {
				continue
			}
			if w := v.Snapshot; started && (w.Colony != current.Colony || w.Load != current.Load || w.Map != current.Map) {
				continue
			}
			cells = append(cells, building.Cell())
		}
	}
	return cells, tx.Commit()
}
