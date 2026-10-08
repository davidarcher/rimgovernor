package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"slices"
)

const TacticSiteExit CombatTactic = "site_exit"

type CombatSiteExit struct {
	Crew  []domain.PawnID
	Cells []domain.Cell
}

func siteExitTurn(view CombatView, stop StopEvent, m *CombatMemory, state map[domain.PawnID]CombatPawnState, orderable map[domain.PawnID]bool) ([]CombatOrder, bool) {
	exit, known := view.SiteExit.Value()
	if !known || len(exit.Cells) == 0 {
		return nil, false
	}
	failed := m.Tactic == TacticSiteExit || m.Scattered
	if stop.Kind == "downed" && slices.Contains(exit.Crew, stop.Pawn) {
		failed = true
	}
	if !failed {
		return nil, false
	}
	m.Tactic = TacticSiteExit
	m.Roles = nil
	orders := []CombatOrder{}
	for _, id := range exit.Crew {
		pawn, found := state[id]
		if !found || pawn.Dead || pawn.Downed {
			continue
		}
		cell := exit.Cells[0]
		available := false
		for _, candidate := range exit.Cells {
			if slices.Contains(m.Unreachable, candidate) {
				continue
			}
			if !available {
				cell = candidate
				available = true
				continue
			}
			if at, known := pawn.Cell.Value(); known && distance2(at, candidate) < distance2(at, cell) {
				cell = candidate
			}
		}
		if !available {
			continue
		}
		at := cell
		m.Roles = append(m.Roles, CombatRole{Pawn: id, Cell: &at, Retreat: true})
		order := CombatOrder{Pawn: id, Kind: OrderMove, Cell: cell, Reason: ReasonRetreat}
		if orderable[id] && !m.doing(order, pawn) {
			orders = append(orders, order)
			m.issue(order, view.Tick)
		}
	}
	return orders, true
}
