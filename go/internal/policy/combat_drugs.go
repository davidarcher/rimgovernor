package policy

import (
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Combat drugs on draft (#1311): a drafted defender takes the combat drug
// it carries (go-juice, or yayo; #1540) once per fight, when a hostile
// comes within its weapon range plus doseMargin and the fight is worth the
// drug: the squad is outmatched, or a mech or a go-juiced raider is in it.
// A lone manhunter rat is fought sober. The order names the preferred
// combat drug (CombatView.Drug); native validates it and refuses a child, a
// pawn already high, or one addicted to, in withdrawal from or highly
// tolerant of its chemical.
const (
	OrderDrug  CombatOrderKind   = "drug"
	ReasonDrug CombatOrderReason = "drug"
	// doseMargin is the cells beyond weapon range a dose goes in, so the
	// high (about a day) starts before contact, not on a long approach.
	doseMargin = 10.0
)

// worthDosing reports a fight the squad is outmatched in, or one with a
// live mech or go-juiced hostile.
// Outmatched counts only with a known eligible armed defender: with none
// the comparison measures missing facts, not the fight.
func worthDosing(view CombatView, state map[domain.PawnID]CombatPawnState) bool {
	known := slices.ContainsFunc(view.Defenders, func(d SquadDefenderFacts) bool { return squadDefenderEligible(d) && positive(d.Armed) })
	if known && outmatched(view) {
		return true
	}
	for _, t := range view.Threats {
		if t.Building || positive(t.Dead) || positive(t.Downed) {
			continue
		}
		if s := state[domain.PawnID(t.ID)]; t.Mech || s.GoJuice && !s.Downed && !s.Dead {
			return true
		}
	}
	return false
}

// doseOrders are this stop's drug orders: each orderable, standing,
// undosed defender (not a rescuer, not mid-aim) with no other order this stop and a
// live hostile pawn in reach, so a dose never displaces a move or an
// attack; a fight sheltering or scattered doses no one. Each pawn is
// dosed at most once per fight, refused or not.
func doseOrders(view CombatView, m *CombatMemory, orders []CombatOrder, orderable map[domain.PawnID]bool, state map[domain.PawnID]CombatPawnState) []CombatOrder {
	if view.Drug == "" || m.Wait || m.PodWait || m.Scattered || m.Tactic == TacticShelter || !worthDosing(view, state) {
		return nil
	}
	var hostiles []CombatPawnState
	for _, t := range view.Threats {
		s, ok := state[domain.PawnID(t.ID)]
		if ok && !t.Building && !s.Dead && !s.Downed && !positive(t.Dead) && !positive(t.Downed) {
			hostiles = append(hostiles, s)
		}
	}
	var doses []CombatOrder
	for _, id := range view.Orderable {
		s, ok := state[id]
		if !ok || slices.ContainsFunc(orders, func(o CombatOrder) bool { return o.Pawn == id }) || !orderable[id] || s.Dead || s.Downed || s.GoJuice || interruptsAim(s) || slices.Contains(m.Dosed, id) || m.Rescue.carrying(id) {
			continue
		}
		if !slices.ContainsFunc(hostiles, func(h CombatPawnState) bool { return withinDose(s, h) }) {
			continue
		}
		m.Dosed = append(m.Dosed, id)
		doses = append(doses, CombatOrder{Pawn: id, Kind: OrderDrug, Drug: view.Drug, Reason: ReasonDrug})
	}
	return doses
}

// withinDose reports h within the defender's weapon range plus doseMargin;
// an unknown cell counts as out of reach (a dose waits for the facts).
func withinDose(d, h CombatPawnState) bool {
	if _, ok := d.Cell.Value(); !ok {
		return false
	}
	if _, ok := h.Cell.Value(); !ok {
		return false
	}
	return inRange(d.Cell, max(d.WeaponRange, 1)+doseMargin, h)
}
