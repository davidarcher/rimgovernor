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

// ShrineHold is one shrine's judgement as the review journals it: Reason
// is a ShrineHold* constant, ShrineHoldGuardsAlive after the breach, or
// ShrineReady; Wall names the chosen breach wall when one was chosen. A
// row with a Casket is that casket's CasketDecision (#459) instead.
type ShrineHold struct {
	Shrine, Reason, Wall string
	Casket               string `json:",omitempty"`
	// Occupant rows (#460) name a released humanlike and the colony decision
	// on it (OccupantDecision).
	Occupant string `json:",omitempty"`
}

const (
	ShrineReady           = "ready"
	ShrineHoldGuardsAlive = "guards_alive"

	// Casket decisions (#459): the default policy never opens a casket.
	CasketClaim       = "claim"
	CasketLeaveSealed = "leave_sealed"
	CasketClaimed     = "claimed"
	CasketHoldSealed  = "shrine_sealed"
)

// CasketDecision is the default casket policy: an empty casket the player
// does not own yet is claimed; a filled one is left sealed (the hostile
// ancients inside are a risk with no upside the colony needs); a casket
// already the player's is done. Both hold shrine_sealed while the room is
// sealed and guards_alive while a guard stands.
func CasketDecision(casket ShrineCasket, shrine AncientShrine) string {
	return CasketDecisionUnder(casket, shrine, ShrinePolicy{})
}

// CasketDecisionUnder is CasketDecision under an operator policy: with
// OpenCaskets on, a filled casket of an open, guard-free shrine is opened
// (#460) instead of left sealed.
func CasketDecisionUnder(casket ShrineCasket, shrine AncientShrine, policy ShrinePolicy) string {
	switch {
	case shrine.Sealed:
		return CasketHoldSealed
	case shrine.GuardsAlive():
		return ShrineHoldGuardsAlive
	case casket.HasContents && policy.OpenCaskets:
		return CasketOpen
	case casket.HasContents:
		return CasketLeaveSealed
	case casket.PlayerClaimed:
		return CasketClaimed
	}
	return CasketClaim
}

// ShrineClaimTargets are the empty caskets the breach goal owes a claim on
// (#459): those of an open, guard-free shrine touching Home, in casket
// identity order per shrine.
func ShrineClaimTargets(rows []AncientShrine) map[string][]ShrineCasket {
	out := map[string][]ShrineCasket{}
	for _, row := range rows {
		if !row.InHome || row.Sealed || !row.GuardsKnown || row.GuardsAlive() {
			continue
		}
		var caskets []ShrineCasket
		for _, casket := range row.Caskets {
			if CasketDecision(casket, row) == CasketClaim {
				caskets = append(caskets, casket)
			}
		}
		if len(caskets) == 0 {
			continue
		}
		sort.Slice(caskets, func(i, j int) bool { return caskets[i].EntityID < caskets[j].EntityID })
		out[row.ID] = caskets
	}
	return out
}

// Squad geometry behind the breach: defenders stand shrineStandDistance
// cells straight out from the wall's outside cell, never nearer than
// shrineStandMinimum along that line (the trap line lies between), never
// further than the trap radius the readiness gate counted.
const (
	shrineStandDistance = 8
	shrineStandMinimum  = 5
)

// ShrineClearanceTargets are the shrines the breach goal owes work on:
// those touching Home that are still sealed, open with a guard seen
// standing, open and guard-free with an empty casket still to claim
// (#459), or with a filled casket the policy opens (#460). A shrine open
// but fogged (nobody has looked in) is not a target: exploring is not
// this goal's, and ActiveCombat answers a guard the moment it is seen.
// Stable by identity.
func ShrineClearanceTargets(rows []AncientShrine, policy ShrinePolicy) []string {
	var out []string
	claims := ShrineClaimTargets(rows)
	opens := ShrineOpenTargets(rows, policy)
	for _, row := range rows {
		if !row.InHome {
			continue
		}
		if row.Sealed || row.GuardsKnown && row.GuardsAlive() || len(claims[row.ID]) > 0 || len(opens[row.ID]) > 0 {
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
