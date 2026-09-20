package buildingruntime

import (
	"context"
	"crypto/sha256"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"sort"
	"time"
)

func breakMelee(row *n.PawnState) domain.Fact[bool] {
	e := row.GetEquipment()
	if e == nil || e.Armed == nil {
		return domain.Unknown[bool]()
	}
	if !e.GetArmed() {
		return domain.Known(false)
	}
	for _, item := range e.Equipped {
		if item.GetThing().GetId() == e.GetPrimaryId() && item.Melee != nil && item.Ranged != nil {
			return domain.Known(item.GetMelee() && !item.GetRanged())
		}
	}
	return domain.Unknown[bool]()
}
func hasAggressiveBreak(f policy.EmergencyFacts) bool {
	for _, p := range f.Colonists {
		if policy.AggressiveBreak(p) {
			return true
		}
	}
	return false
}

func (r *RoutineDefensePlanner) planBreak(call, epoch context.Context, goal store.GoalState, state ControlState, started time.Time, arbiter *stepArbiter, f policy.EmergencyFacts, rows map[string]*n.PawnState) (RoutineDefenseResult, error) {
	var targets []policy.EmergencyPawn
	for _, p := range f.Colonists {
		if policy.AggressiveBreak(p) {
			targets = append(targets, p)
		}
	}
	if len(targets) == 0 {
		return RoutineDefenseResult{}, nil
	}
	sort.Slice(targets, func(i, j int) bool { return targets[i].ID < targets[j].ID })
	var responders []policy.BreakResponder
	for _, p := range f.Colonists {
		row := rows[string(p.ID)]
		d := squadDefenderFacts(row)
		d.MeleeEquipped = breakMelee(row)
		responders = append(responders, policy.BreakResponder{SquadDefenderFacts: d, Cell: breakCell(row)})
	}
	var actions []domain.Action
	h := sha256.New()
	var chosen []domain.PawnID
	var victim policy.PawnID
	for _, target := range targets {
		chosen = policy.SelectBreakSquad(target, breakCell(rows[string(target.ID)]), responders)
		if len(chosen) > 0 {
			victim = target.ID
			break
		}
	}
	if len(chosen) == 0 {
		return RoutineDefenseResult{Reason: BuildingMethodNoSquad}, nil
	}
	if !arbiter.tryClaim(append(append([]domain.PawnID{}, chosen...), domain.PawnID(victim))) {
		return RoutineDefenseResult{Reason: BuildingMethodUsed}, nil
	}
	fmt.Fprintf(h, "%s/%v", victim, chosen)
	method, id := defenseMethodIDs("subdue", goal, h)
	for i, pawn := range chosen {
		draftID := domain.ActionID(fmt.Sprintf("%s-d%d", id, i))
		d, err := domain.NewOwnedDraft(pawn)
		if err != nil {
			return RoutineDefenseResult{}, err
		}
		da, err := domain.NewOwnedDraftAction(draftID, d)
		if err != nil {
			return RoutineDefenseResult{}, err
		}
		m, err := domain.NewSubdue(pawn, domain.PawnID(victim), draftID)
		if err != nil {
			return RoutineDefenseResult{}, err
		}
		ma, err := domain.NewMeleeAttackAction(domain.ActionID(fmt.Sprintf("%s-s%d", id, i)), m)
		if err != nil {
			return RoutineDefenseResult{}, err
		}
		actions = append(actions, da, ma)
	}
	plan, err := domain.NewPlan(id, 1, actions)
	if err != nil {
		return RoutineDefenseResult{}, err
	}
	p := r.reviewer.player
	if err = p.current(call, epoch); err != nil {
		return RoutineDefenseResult{}, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoutineDefenseResult{}, ErrControl
	}
	if _, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, method, plan); err != nil {
		return RoutineDefenseResult{}, err
	}
	return RoutineDefenseResult{Reason: BuildingMethodAdmitted, Plan: id}, nil
}
