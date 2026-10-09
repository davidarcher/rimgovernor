package store

import (
	"context"
	"database/sql"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// buildingOpenWorkExempt admits a pure-construction method beside the owner's
// open work when that work is pure construction on other cells: building plans
// that touch disjoint cells cannot conflict, so there is nothing to observe
// first. Planners fund such plans against their own stock ledger; the native
// side validates each footprint when the intent is applied.
func buildingOpenWorkExempt(ctx context.Context, tx *sql.Tx, goal WorkOwner, plan domain.PlanSpec) (bool, error) {
	taken := map[domain.Cell]bool{}
	for _, m := range goal.OwnerMethods() {
		p, err := load(ctx, tx, m.Plan)
		if err != nil {
			return false, err
		}
		if !PlanOpen(p) {
			continue
		}
		for _, a := range p.Spec.Actions() {
			b, ok := a.Building()
			if !ok {
				return false, nil
			}
			taken[b.Cell()] = true
		}
	}
	if len(plan.Actions()) == 0 {
		return false, nil
	}
	for _, a := range plan.Actions() {
		b, ok := a.Building()
		if !ok || taken[b.Cell()] {
			return false, nil
		}
	}
	return true, nil
}
