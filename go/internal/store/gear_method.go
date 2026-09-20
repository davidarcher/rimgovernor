package store

import (
	"context"
	"database/sql"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// Independent pawn dressing may share a review; a bill remains exclusive.
func gearOpenWorkExempt(ctx context.Context, tx *sql.Tx, goal GoalState, plan domain.PlanSpec) (bool, error) {
	review, err := loadRoutine(ctx, tx)
	if err != nil {
		return false, err
	}
	bound := false
	for _, b := range review.Goals {
		bound = bound || b.Goal == goal.Goal.ID && b.Need == policy.MaintainEquipment
	}
	if !bound {
		return false, nil
	}
	pawns := map[domain.PawnID]bool{}
	items := map[string]bool{}
	add := func(spec domain.PlanSpec) bool {
		for _, a := range spec.Actions() {
			var pawn domain.PawnID
			if v, ok := a.GearReplace(); ok {
				pawn = v.Pawn()
				if items[v.Thing()] {
					return false
				}
				items[v.Thing()] = true
			} else if v, ok := a.ApparelPolicy(); ok {
				pawn = v.Pawn()
			} else {
				return false
			}
			if pawns[pawn] {
				return false
			}
			pawns[pawn] = true
		}
		return true
	}
	if !add(plan) {
		return false, nil
	}
	open := 0
	for _, m := range goal.Methods {
		p, err := load(ctx, tx, m.Plan)
		if err != nil {
			return false, err
		}
		if domain.GoalWorkOpen(p.Progress) {
			open++
			if !add(p.Spec) {
				return false, nil
			}
		}
	}
	occupied := len(review.Development.Committed)
	for _, id := range review.Development.Committed {
		if id == policy.MaintainEquipment {
			occupied--
		}
	}
	return open+occupied < review.Development.Capacity, nil
}
