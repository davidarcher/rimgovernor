package policy

import (
	"math"
	"slices"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Royal permits in the fight (#1608, epic #1598): an outmatched fight calls
// the aid and strike permits its colonists hold, once per fight each. A
// call is a permit_call order, not a pawn job: the holder keeps its other
// orders, and the defense planner sends the call as the generic Ability
// action (#1607), where native owns the permit's own guards.
const (
	OrderPermit  CombatOrderKind   = "permit_call"
	ReasonPermit CombatOrderReason = "permit"
)

// permitFightKind classifies an acting permit the fight may call: aid
// (reinforcements land at a cell) or a strike (a bombardment lands on the
// hostiles). Anything else, laborers and trade included, is not a fight call.
type permitFightKind int

const (
	permitNotFight permitFightKind = iota
	permitAidCall
	permitStrikeCall
)

func permitFightClass(name string) permitFightKind {
	switch {
	case strings.Contains(name, "Strike"), strings.Contains(name, "Bombard"):
		return permitStrikeCall
	case permitCategory(RoyalPermit{Name: name}) == PermitAid && !strings.Contains(name, "Laborer"):
		return permitAidCall
	}
	return permitNotFight
}

func permitKey(pawn domain.PawnID, faction, permit string) string {
	return string(pawn) + "/" + faction + "/" + permit
}

// permitReady reports a held acting permit that is off cooldown and
// affordable per the royalty read; every unread fact holds the call.
func permitReady(f RoyaltyFacts, h RoyalHolding, name string) bool {
	permit, ok := f.Permits[name]
	if !ok {
		return false
	}
	if acts, known := permit.Acts.Value(); !known || !acts {
		return false
	}
	cost, known := permit.FavorCost.Value()
	favor, favorKnown := h.Favor.Value()
	if !known || !favorKnown || favor < cost {
		return false
	}
	remaining, known := h.Cooldowns[name].RemainingTicks.Value()
	return known && remaining == 0
}

// permitCalls are this stop's permit calls: with a known armed squad
// outmatched, each held, ready aid or strike permit whose holder is a
// standing colonist and for which a target exists. A strike lands on the
// densest hostile clump no colonist stands near (the mortar rule, the
// strike scatters); aid lands on the defender nearest the squad's centre.
// Each holder-permit is called once per fight.
func permitCalls(view CombatView, m *CombatMemory) []CombatOrder {
	royalty, ok := view.Royalty.Value()
	if !ok || view.Hunt || !outmatchedKnown(view) {
		return nil
	}
	down := downPawns(view)
	var calls []CombatOrder
	for _, holder := range sortedHolders(royalty.Holders) {
		pawn := domain.PawnID(holder)
		if down[pawn] || !slices.ContainsFunc(view.Defenders, func(d SquadDefenderFacts) bool { return d.ID == pawn }) {
			continue
		}
		for _, h := range royalty.Holders[holder] {
			for _, name := range h.Permits {
				kind := permitFightClass(name)
				key := permitKey(pawn, h.FactionDef, name)
				if kind == permitNotFight || slices.Contains(m.Permitted, key) || !permitReady(royalty, h, name) {
					continue
				}
				var cell domain.Cell
				var found bool
				if kind == permitStrikeCall {
					cell, found = strikeCell(view)
				} else {
					cell, found = aidCell(view)
				}
				if !found {
					continue
				}
				m.Permitted = append(m.Permitted, key)
				calls = append(calls, CombatOrder{Pawn: pawn, Kind: OrderPermit, Cell: cell, Faction: h.FactionDef, Permit: name, Reason: ReasonPermit})
			}
		}
	}
	return calls
}

// outmatchedKnown is outmatched with a known eligible armed defender: with
// none the comparison measures missing facts, not the fight.
func outmatchedKnown(view CombatView) bool {
	return slices.ContainsFunc(view.Defenders, func(d SquadDefenderFacts) bool { return squadDefenderEligible(d) && positive(d.Armed) }) && outmatched(view)
}

// liveHostileCells are the cells of the live hostile pawns, in view order.
func liveHostileCells(view CombatView) []domain.Cell {
	var cells []domain.Cell
	for _, p := range liveHostiles(view) {
		c, _ := p.Cell.Value()
		cells = append(cells, c)
	}
	return cells
}

// strikeCell is the hostile cell with the most hostiles within
// campClumpRadius, skipping cells near a colonist (first on a tie).
func strikeCell(view CombatView) (domain.Cell, bool) {
	cells := liveHostileCells(view)
	ours := colonistCells(view)
	var best domain.Cell
	most := 0
	for _, c := range cells {
		if nearAny(ours, c) {
			continue
		}
		n := 0
		for _, o := range cells {
			if dist(c, o) <= campClumpRadius {
				n++
			}
		}
		if n > most {
			best, most = c, n
		}
	}
	return best, most > 0
}

// aidCell is the known defender cell nearest the squad's mean cell.
func aidCell(view CombatView) (domain.Cell, bool) {
	ours := colonistCells(view)
	if len(ours) == 0 {
		return domain.Cell{}, false
	}
	var x, z float64
	for _, c := range ours {
		x += float64(c.X)
		z += float64(c.Z)
	}
	x, z = x/float64(len(ours)), z/float64(len(ours))
	var best domain.Cell
	nearest := math.MaxFloat64
	for _, c := range ours {
		if d := math.Hypot(float64(c.X)-x, float64(c.Z)-z); d < nearest {
			best, nearest = c, d
		}
	}
	return best, true
}
