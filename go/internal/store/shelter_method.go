package store

import (
	"context"
	"database/sql"
	"path"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// shelterBunkMethods name the initial shelter's bunk rung methods
// (buildingruntime.ShelterSpotsMethod, ShelterBedsMethod).
var shelterBunkMethods = map[domain.MethodID]bool{"shelter-spots": true, "shelter-bedrolls": true, "shelter-beds": true, "shelter-clear-bedrolls": true, "shelter-clear-beds": true}

// shelterOpenWorkExempt admits a method under the initial shelter while its
// only open work is bunk rungs (#641): the ring is sited around the spots
// and beds, so walls and door proceed while a bed stalls. The new method
// must be pure construction on no cell an open bunk stands on (a bed goes on
// a spot's slot only after the spot is deleted, #2080); any other open method
// (the shell itself) still holds the goal.
func shelterOpenWorkExempt(ctx context.Context, tx *sql.Tx, goal WorkOwner, plan domain.PlanSpec) (bool, error) {
	review, err := loadRounds(ctx, tx)
	if err != nil {
		return false, err
	}
	bound := false
	for _, b := range review.Standards {
		bound = bound || string(b.Standard) == goal.OwnerID() && b.Concern == policy.MaintainHousing
	}
	if !bound || len(plan.Actions()) == 0 {
		return false, nil
	}
	taken := map[domain.Cell]bool{}
	for _, m := range goal.OwnerMethods() {
		p, err := load(ctx, tx, m.Plan)
		if err != nil {
			return false, err
		}
		if !PlanOpen(p) {
			continue
		}
		if !shelterBunkMethods[m.Method] {
			return false, nil
		}
		for _, a := range p.Spec.Actions() {
			if b, ok := a.Building(); ok {
				for _, c := range policy.BunkCells(b.Cell(), b.Rotation()) {
					taken[c] = true
				}
			}
		}
	}
	for _, a := range plan.Actions() {
		b, ok := a.Building()
		if !ok {
			return false, nil
		}
		if taken[b.Cell()] {
			return false, nil
		}
	}
	return true, nil
}

// IsRoomShellMethod reports a planned room's ring method: a build wave of a
// "<role>-shell-<x>-<z>" reconcile.
func IsRoomShellMethod(method domain.MethodID) bool {
	ok, _ := path.Match("*-shell-*-build-*", string(method))
	return ok
}

// roomShellOpenWorkExempt admits furniture under an owner whose only open work
// is planned rooms' ring waves (#2303): a ring runs for days, and the owner's
// slot on the room's interior (the shelter's campfire or cooler) is admitted
// with it, not after it. The new method must be pure construction that is not
// itself a ring wave, on no cell an open wave builds on; any other open method
// still holds the owner.
func roomShellOpenWorkExempt(ctx context.Context, tx *sql.Tx, goal WorkOwner, method domain.MethodID, plan domain.PlanSpec) (bool, error) {
	if len(plan.Actions()) == 0 || IsRoomShellMethod(method) {
		return false, nil
	}
	taken := map[domain.Cell]bool{}
	for _, m := range goal.OwnerMethods() {
		p, err := load(ctx, tx, m.Plan)
		if err != nil {
			return false, err
		}
		if !PlanOpen(p) {
			continue
		}
		if !IsRoomShellMethod(m.Method) {
			return false, nil
		}
		for _, a := range p.Spec.Actions() {
			if b, ok := a.Building(); ok {
				taken[b.Cell()] = true
			}
		}
	}
	for _, a := range plan.Actions() {
		b, ok := a.Building()
		if !ok || taken[b.Cell()] {
			return false, nil
		}
	}
	return true, nil
}
