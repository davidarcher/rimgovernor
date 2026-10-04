package buildingruntime

import (
	"context"
	"crypto/sha256"
	"fmt"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// splitPermitCalls separates a stop's permit calls (#1608) and psycast casts
// (#1611) from its combat orders: each is a generic Ability action, not a
// combat.orders entry.
func splitPermitCalls(orders []policy.CombatOrder) (rest, calls []policy.CombatOrder) {
	for _, order := range orders {
		if order.Kind == policy.OrderPermit || order.Kind == policy.OrderCast {
			calls = append(calls, order)
		} else {
			rest = append(rest, order)
		}
	}
	return rest, calls
}

// permitCallActions are the Ability actions of a stop's permit calls, one
// per call, native owning the permit's own guards (cooldown, favor, target).
func permitCallActions(plan domain.PlanID, calls []policy.CombatOrder) ([]domain.Action, error) {
	actions := make([]domain.Action, 0, len(calls))
	for i, call := range calls {
		source, target, err := callAbility(call)
		if err != nil {
			return nil, err
		}
		ability, err := domain.NewAbility(call.Pawn, source, target)
		if err != nil {
			return nil, err
		}
		action, err := domain.NewAbilityAction(domain.ActionID(fmt.Sprintf("%s-p%d", plan, i)), ability)
		if err != nil {
			return nil, err
		}
		actions = append(actions, action)
	}
	return actions, nil
}

// callAbility is the ability source and target of a permit call (a permit at
// a cell) or a psycast cast (the arm the psycast takes).
func callAbility(call policy.CombatOrder) (domain.AbilitySource, domain.AbilityTarget, error) {
	if call.Kind != policy.OrderCast {
		source, err := domain.PermitSource(call.Faction, call.Permit)
		if err != nil {
			return domain.AbilitySource{}, domain.AbilityTarget{}, err
		}
		target, err := domain.AbilityCellTarget(call.Cell)
		return source, target, err
	}
	source, err := domain.PsycastSource(call.Permit)
	if err != nil {
		return domain.AbilitySource{}, domain.AbilityTarget{}, err
	}
	switch call.Arm {
	case policy.PsycastTargetSelf:
		return source, domain.NoAbilityTarget(), nil
	case policy.PsycastTargetPawn:
		target, err := domain.AbilityPawnTarget(call.Target)
		return source, target, err
	case policy.PsycastTargetCell:
		target, err := domain.AbilityCellTarget(call.Cell)
		return source, target, err
	}
	return domain.AbilitySource{}, domain.AbilityTarget{}, fmt.Errorf("psycast cast of %s has no target arm", call.Permit)
}

// commitPermitCalls commits the stop's permit calls as one incident method
// beside the fight's, which the executor dispatches; the fight's own stops
// wait while it is open, as for any other open work.
func (r *RoundsDefensePlanner) commitPermitCalls(call, epoch context.Context, incident store.IncidentState, calls []policy.CombatOrder) error {
	if len(calls) == 0 {
		return nil
	}
	id := domain.MintPlanID()
	actions, err := permitCallActions(id, calls)
	if err != nil {
		return err
	}
	plan, err := domain.NewPlan(id, 1, actions)
	if err != nil {
		return err
	}
	hash := sha256.New()
	for _, c := range calls {
		fmt.Fprintf(hash, "%s/%s/%s/%s/%d,%d\n", c.Pawn, c.Faction, c.Permit, c.Target, c.Cell.X, c.Cell.Z)
	}
	method := defenseMethodID("permit", len(incident.Methods), hash)
	p := r.reviewer.player
	if err = p.current(call, epoch); err != nil {
		return err
	}
	_, err = p.journal.CommitIncidentMethod(call, incident.Incident.ID, method, "", plan)
	return err
}

// withoutPermitMarks is next with the permit and cast marks of memory: an
// admission stop drops its calls, so it does not mark them called.
func withoutPermitMarks(next, memory policy.CombatMemory) policy.CombatMemory {
	next.Permitted = slices.Clone(memory.Permitted)
	next.Casts = slices.Clone(memory.Casts)
	return next
}
