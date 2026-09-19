package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// ClearAncientShrine is the breach goal (#458): a sealed shrine whose room
// touches Home is a deficit until it is open and its guards are down. The
// goal drafts the readiness squad behind the trap line, deconstructs the
// chosen wall in place and lets ActiveCombat fight what pops.
const ClearAncientShrine GoalID = "ClearAncientShrine"

// ShrineHold is one shrine's breach judgement as the review journals it:
// Reason is a ShrineHold* constant, ShrineHoldGuardsAlive after the breach,
// or ShrineReady; Wall names the chosen breach wall when one was chosen.
type ShrineHold struct {
	Shrine, Reason, Wall string
}

const (
	ShrineReady           = "ready"
	ShrineHoldGuardsAlive = "guards_alive"
)

// Squad geometry behind the breach: defenders stand shrineStandDistance
// cells straight out from the wall's outside cell, never nearer than
// shrineStandMinimum along that line (the trap line lies between), never
// further than the trap radius the readiness gate counted.
const (
	shrineStandDistance = 8
	shrineStandMinimum  = 5
)

// ShrineClearanceTargets are the shrines the breach goal owes work on:
// those touching Home that are still sealed, or open with a guard seen
// standing. A shrine open but fogged (nobody has looked in) is not a
// target: exploring is not this goal's, and ActiveCombat answers a guard
// the moment it is seen. Stable by identity.
func ShrineClearanceTargets(rows []AncientShrine) []string {
	var out []string
	for _, row := range rows {
		if !row.InHome {
			continue
		}
		if row.Sealed || row.GuardsKnown && row.GuardsAlive() {
			out = append(out, row.ID)
		}
	}
	sort.Strings(out)
	return out
}

// ShrineHoldReason names why one shrine is not breached now: the readiness
// gate's reason while sealed, guards_alive once open, ready otherwise.
func ShrineHoldReason(shrine AncientShrine, readiness ShrineReadiness) string {
	switch {
	case !shrine.Sealed && shrine.GuardsAlive():
		return ShrineHoldGuardsAlive
	case readiness.Ready:
		return ShrineReady
	}
	return readiness.Reason
}

// ShrineBreachDrafts is the part of the readiness squad the breach drafts:
// all of it while another colonist is free to do the deconstruct job,
// otherwise all but the last (the least useful, a melee pawn when any
// shooter stands). Readiness guarantees at least two, so one always stands.
func ShrineBreachDrafts(squad []domain.PawnID, colonists int) []domain.PawnID {
	out := append([]domain.PawnID(nil), squad...)
	if colonists <= len(out) && len(out) > 1 {
		out = out[:len(out)-1]
	}
	return out
}

// ShrineBreachPositions picks one standing cell per drafted defender behind
// the trap line: cells at least shrineStandMinimum along the outward line
// from the wall's outside cell and within the trap radius, never a trap
// cell, nearest the ideal point shrineStandDistance out first. A defender
// with no cell left stands where it is (drafted, not moved).
func ShrineBreachPositions(wall ShrineBreachWall, squad []domain.PawnID, standing, traps []domain.Cell) map[domain.PawnID]domain.Cell {
	dx, dz := sign(wall.Outside.X-wall.Cell.X), sign(wall.Outside.Z-wall.Cell.Z)
	if dx == 0 && dz == 0 || dx != 0 && dz != 0 {
		return map[domain.PawnID]domain.Cell{}
	}
	trapped := map[domain.Cell]bool{}
	for _, trap := range traps {
		trapped[trap] = true
	}
	ideal := domain.Cell{X: wall.Outside.X + dx*shrineStandDistance, Z: wall.Outside.Z + dz*shrineStandDistance}
	var cells []domain.Cell
	seen := map[domain.Cell]bool{}
	for _, cell := range standing {
		if seen[cell] || trapped[cell] {
			continue
		}
		along := (cell.X-wall.Outside.X)*dx + (cell.Z-wall.Outside.Z)*dz
		if along < shrineStandMinimum || squaredDistance(cell, wall.Outside) > int64(shrineTrapRadius*shrineTrapRadius) {
			continue
		}
		seen[cell] = true
		cells = append(cells, cell)
	}
	sort.Slice(cells, func(i, j int) bool {
		a, b := squaredDistance(cells[i], ideal), squaredDistance(cells[j], ideal)
		if a != b {
			return a < b
		}
		if cells[i].X != cells[j].X {
			return cells[i].X < cells[j].X
		}
		return cells[i].Z < cells[j].Z
	})
	out := map[domain.PawnID]domain.Cell{}
	for i, id := range squad {
		if i >= len(cells) {
			break
		}
		out[id] = cells[i]
	}
	return out
}

func sign(v int32) int32 {
	switch {
	case v < 0:
		return -1
	case v > 0:
		return 1
	}
	return 0
}
