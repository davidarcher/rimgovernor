package store

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// Independent pawn dressing may share a review; a bill remains exclusive.
func gearOpenWorkExempt(ctx context.Context, tx *Tx, goal WorkOwner, plan domain.PlanSpec) (bool, error) {
	review, err := loadRounds(ctx, tx)
	if err != nil {
		return false, err
	}
	bound := false
	for _, b := range review.Standards {
		bound = bound || string(b.Standard) == goal.OwnerID() && b.Concern == policy.MaintainEquipment
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
	// No slot limit: open work only has to stay off the pawns and items this
	// plan touches.
	for _, m := range goal.OwnerMethods() {
		p, err := load(ctx, tx, m.Plan)
		if err != nil {
			return false, err
		}
		if domain.StandardWorkOpen(p.Progress) && !add(p.Spec) {
			return false, nil
		}
	}
	return true, nil
}

// craftingSpotAction reports an action placing the crafting spot or removing it
// once a weapon bench stands.
func craftingSpotAction(action domain.Action) bool {
	if building, ok := action.Building(); ok {
		return building.Definition() == policy.CraftingSpotDefinition
	}
	cut, ok := action.Deconstruction()
	return ok && cut.Definition() == policy.CraftingSpotDefinition
}

// craftingSpotOpenWorkExempt lets MaintainEquipment's crafting spot (its
// placement in the planned workshop, its deconstruction) be committed beside the
// goal's bills and dressing, which run for days and do not depend on it. A plan
// made only of spot actions is blocked only by an open spot action.
func craftingSpotOpenWorkExempt(ctx context.Context, tx *Tx, goal WorkOwner, plan domain.PlanSpec) (bool, error) {
	if len(plan.Actions()) == 0 {
		return false, nil
	}
	for _, action := range plan.Actions() {
		if !craftingSpotAction(action) {
			return false, nil
		}
	}
	review, err := loadRounds(ctx, tx)
	if err != nil {
		return false, err
	}
	bound := false
	for _, b := range review.Standards {
		bound = bound || string(b.Standard) == goal.OwnerID() && b.Concern == policy.MaintainEquipment
	}
	if !bound {
		return false, nil
	}
	for _, m := range goal.OwnerMethods() {
		p, err := load(ctx, tx, m.Plan)
		if err != nil {
			return false, err
		}
		for _, progress := range p.Progress {
			if craftingSpotAction(progress.Action()) && domain.StandardWorkOpen([]domain.Progress{progress}) {
				return false, nil
			}
		}
	}
	return true, nil
}
