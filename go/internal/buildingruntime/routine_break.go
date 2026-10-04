package buildingruntime

import (
	"context"
	"crypto/sha256"
	"fmt"
	"sort"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// breakMelee is whether the pawn's primary is a melee weapon; unarmed is
// known false, an unresolved primary unknown.
func breakMelee(row *n.PawnState, arms armament) (domain.Fact[bool], error) {
	weapon, known, err := arms.primary(row.GetEquipment())
	if err != nil || !known {
		return domain.Unknown[bool](), err
	}
	return domain.Known(weapon.Melee), nil
}
func hasAggressiveBreak(f policy.EmergencyFacts) bool {
	for _, p := range f.Colonists {
		if policy.AggressiveBreak(p) {
			return true
		}
	}
	return false
}

func (r *RoutineDefensePlanner) planBreak(call, epoch context.Context, incident store.IncidentState, state ControlState, started time.Time, arbiter *stepArbiter, f policy.EmergencyFacts, rows map[string]*n.PawnState) (RoutineDefenseResult, error) {
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
	needed, err := plannedDrafts(call, r.reviewer.player.journal)
	if err != nil {
		return RoutineDefenseResult{}, err
	}
	var responders []policy.BreakResponder
	arms, err := readArmament(call, r.native, boundary.Identity(state.Snapshot))
	if err != nil {
		return RoutineDefenseResult{}, err
	}
	for _, p := range f.Colonists {
		row := rows[string(p.ID)]
		ranged, err := rangedWeaponEquipped(row.Equipment, arms)
		if err != nil {
			return RoutineDefenseResult{}, err
		}
		d := squadDefenderFacts(row, needed, ranged)
		if d.MeleeEquipped, err = breakMelee(row, arms); err != nil {
			return RoutineDefenseResult{}, err
		}
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
		return RoutineDefenseResult{Verdict: BuildingReasonNoSquad}, nil
	}
	if !arbiter.tryClaim(append(append([]domain.PawnID{}, chosen...), domain.PawnID(victim))) {
		return RoutineDefenseResult{Verdict: waitFor(WaitMethodUsed, "break_pawn_claim")}, nil
	}
	fmt.Fprintf(h, "%s/%v", victim, chosen)
	method, id := defenseMethodID("subdue", len(incident.Methods), h), domain.MintPlanID()
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
		ma, err := domain.NewSubdueAction(domain.ActionID(fmt.Sprintf("%s-s%d", id, i)), m)
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
		return RoutineDefenseResult{}, fmt.Errorf("%w: planBreak: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge", ErrControl)
	}
	if _, err = p.journal.CommitIncidentMethod(call, incident.Incident.ID, method, "", plan); err != nil {
		return RoutineDefenseResult{}, err
	}
	return RoutineDefenseResult{Verdict: BuildingReasonAdmitted, Plan: id}, nil
}
