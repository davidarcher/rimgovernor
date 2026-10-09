package policy

import (
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// grenadeScatterClear is how far (Chebyshev cells) a ground target stays
// from every colonist: a grenade scatters one tile.
const grenadeScatterClear = 2

// GrenadeTarget is the grenade decision: (carrier, hostiles,
// colonist cells) -> the cell to attack_ground, or none. Candidates are the
// live hostiles' cells in the carrier's range; each scores the hostiles
// inside the blast (EMP: only shielded or mech hostiles), and a cell within
// grenadeScatterClear of a colonist is rejected. Most hits wins, then the
// nearer cell, then the lower x, z. A carrier whose primary is not an area
// weapon, or with no cell, gets none.
func GrenadeTarget(carrier CombatPawnState, hostiles []CombatPawnState, colonists []domain.Cell) (domain.Cell, bool) {
	blast := carrier.WeaponFacts.Blast
	from, known := carrier.Cell.Value()
	if blast <= 0 || !known {
		return domain.Cell{}, false
	}
	reach := grenadeReach(carrier)
	emp, fire := carrier.WeaponFacts.EMP, carrier.WeaponFacts.Incendiary
	var cells []domain.Cell
	var worth []domain.Cell
	for _, h := range hostiles {
		c, ok := h.Cell.Value()
		if !ok || h.Dead || h.Downed {
			continue
		}
		cells = append(cells, c)
		// Mechs are immune to fire: an incendiary never counts one.
		if (!emp || empWorth(h)) && !(fire && isMech(h)) {
			worth = append(worth, c)
		}
	}
	return bestGround(from, reach, blast, cells, worth, colonists)
}

// grenadeReach is the carrier's range: its weapon range, else the def's.
func grenadeReach(carrier CombatPawnState) float64 {
	if carrier.WeaponRange > 0 {
		return carrier.WeaponRange
	}
	return carrier.WeaponFacts.Range
}

// bestGround is the candidate cell in reach, clear of colonists, whose
// blast covers the most worth cells: then the nearer, then the lower x, z.
func bestGround(from domain.Cell, reach, blast float64, cells, worth, colonists []domain.Cell) (domain.Cell, bool) {
	var best domain.Cell
	bestHits, bestDist := 0, math.MaxFloat64
	for _, c := range cells {
		d := dist(from, c)
		if d > reach || nearColonist(c, colonists) {
			continue
		}
		hits := 0
		for _, w := range worth {
			if dist(c, w) <= blast {
				hits++
			}
		}
		if hits == 0 {
			continue
		}
		if hits > bestHits || hits == bestHits && (d < bestDist || d == bestDist && (c.X < best.X || c.X == best.X && c.Z < best.Z)) {
			best, bestHits, bestDist = c, hits, d
		}
	}
	return best, bestHits > 0
}

// empWorth is a hostile an EMP disables: a worn shield or a mechanoid.
func empWorth(h CombatPawnState) bool {
	if _, shielded := h.Shield.Value(); shielded {
		return true
	}
	return isMech(h)
}

// isMech is a mechanoid pawn.
func isMech(h CombatPawnState) bool { return h.Mech }

func nearColonist(c domain.Cell, colonists []domain.Cell) bool {
	for _, p := range colonists {
		if max(abs(c.X-p.X), abs(c.Z-p.Z)) <= grenadeScatterClear {
			return true
		}
	}
	return false
}

func abs(v int32) int32 {
	if v < 0 {
		return -v
	}
	return v
}

// grenade aims each hold or squad role whose pawn holds an area primary:
// Ground is the cell GrenadeTarget picks, nil when it picks none,
// and the pawn keeps its cell and target for then. It runs after
// rocketClumps, which clears Ground and shares its order.
func grenade(view CombatView, m *CombatMemory) {
	if m.Tactic != TacticHold && m.Tactic != TacticSquad {
		return
	}
	state := map[domain.PawnID]CombatPawnState{}
	for _, p := range view.Pawns {
		state[p.ID] = p
	}
	hostiles := rankThreats(view)
	var colonists []domain.Cell
	for _, d := range view.Defenders {
		if s, ok := state[d.ID]; ok && !s.Dead {
			if c, known := s.Cell.Value(); known {
				colonists = append(colonists, c)
			}
		}
	}
	for i := range m.Roles {
		r := &m.Roles[i]
		if r.Mortar != nil || m.Rescue.carrying(r.Pawn) {
			continue
		}
		carrier := state[r.Pawn]
		if r.Cell != nil {
			// Aim from the held cell: the throw waits until the pawn is there.
			if at, ok := carrier.Cell.Value(); !ok || at != *r.Cell {
				continue
			}
		}
		if c, ok := grenadeAim(view, *m, carrier, hostiles, colonists); ok {
			r.Ground = &c
		}
	}
}
