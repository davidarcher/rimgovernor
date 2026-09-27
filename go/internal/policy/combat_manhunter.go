package policy

import (
	"math"
	"slices"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// TacticManhunter is a manhunter pack's own formation (#898): a fight whose
// live hostiles are all manhunter animals picks it instead of the generic
// squad fallback.
const TacticManhunter CombatTactic = "manhunter"

// Manhunter classification constants (#898).
const (
	// colonistMoveSpeed is a baseline colonist's MoveSpeed, cells/s; an
	// animal slower than it is kitable.
	colonistMoveSpeed = 4.6
	// exploderRadius is how close to a colonist an exploder is never shot,
	// in cells: a boomalope's death blast and fire.
	exploderRadius = 6.0
)

// AnimalClass is one manhunter's classification (#898).
type AnimalClass struct {
	// Slow is an animal slower than a colonist: kitable (#901). Unknown
	// speed is not slow.
	Slow bool
	// Exploder explodes on death (boomalope, boomrat): never melee-blocked,
	// shot only away from our pawns.
	Exploder bool
}

// classifyAnimal sorts one animal by speed and by its death explosion.
func classifyAnimal(s CombatPawnState) AnimalClass {
	return AnimalClass{
		Slow:     s.MoveSpeed > 0 && s.MoveSpeed < colonistMoveSpeed,
		Exploder: strings.Contains(s.Kind, "Boomalope") || strings.Contains(s.Kind, "Boomrat"),
	}
}

// ManhunterPack reports a fight whose live hostile pawns (at least one) are
// all known manhunter animals.
func ManhunterPack(view CombatView) bool {
	down := downPawns(view)
	n := 0
	for _, t := range view.Threats {
		if t.Building || positive(t.Dead) || positive(t.Downed) || down[domain.PawnID(t.ID)] {
			continue
		}
		if !positive(t.Animal) || !positive(t.Manhunter) {
			return false
		}
		n++
	}
	return n > 0
}

// downPawns are the pawns the live state shows dead or downed.
func downPawns(view CombatView) map[domain.PawnID]bool {
	down := map[domain.PawnID]bool{}
	for _, p := range view.Pawns {
		if p.Dead || p.Downed {
			down[p.ID] = true
		}
	}
	return down
}

// liveExploders are the live hostile exploders, by id.
func liveExploders(view CombatView) map[domain.PawnID]CombatPawnState {
	out := map[domain.PawnID]CombatPawnState{}
	for _, h := range rankThreats(view) {
		if classifyAnimal(h).Exploder {
			out[h.ID] = h
		}
	}
	return out
}

// nearExploders are the live exploders within exploderRadius of a live
// colonist, or at an unknown cell: no one shoots them this stop.
func nearExploders(view CombatView) map[domain.PawnID]bool {
	out := map[domain.PawnID]bool{}
	exploders := liveExploders(view)
	if len(exploders) == 0 {
		return out
	}
	colonists := map[domain.PawnID]bool{}
	for _, d := range view.Defenders {
		colonists[d.ID] = true
	}
	down := downPawns(view)
	for id, h := range exploders {
		at, ok := h.Cell.Value()
		if !ok {
			out[id] = true
			continue
		}
		for _, p := range view.Pawns {
			c, known := p.Cell.Value()
			if colonists[p.ID] && !down[p.ID] && known && math.Hypot(float64(c.X-at.X), float64(c.Z-at.Z)) <= exploderRadius {
				out[id] = true
			}
		}
	}
	return out
}

// manhunterFormation is the manhunter tactic's roles (#898). The hold
// refuses an animal and squad defense mirrors its weapon (none, so it
// would send riflemen into melee), so the pack gets its own: every armed
// gunner shoots, from the layout's firing cells (and the game's covered
// cells) when there is a layout; brawlers take #864's blocking duties when
// the choke is blocked, and otherwise engage in melee. No melee role holds
// an exploder as its target.
func manhunterFormation(view CombatView, geometry GeometryReply, relieved []domain.PawnID) []CombatRole {
	var gunners, rest []SquadDefenderFacts
	for _, d := range view.Defenders {
		switch {
		case !squadDefenderEligible(d) || !positive(d.Armed):
		case positive(d.RangedEquipped):
			gunners = append(gunners, d)
		default:
			rest = append(rest, d)
		}
	}
	exploders := liveExploders(view)
	var top, melee domain.PawnID
	for _, h := range rankThreats(view) {
		if top == "" {
			top = h.ID
		}
		if _, boom := exploders[h.ID]; !boom && melee == "" {
			melee = h.ID
		}
	}
	_, _, blocking := blockingChoke(view)
	var cells []domain.Cell
	if layout, ok := view.Layout.Value(); ok {
		cells = slices.Clone(layout.Firing)
		candidates := geometry.Proposals
		if blocking {
			candidates = coveredAround(layout.Firing, geometry)
		}
		for _, c := range candidates {
			if !slices.Contains(cells, c) {
				cells = append(cells, c)
			}
		}
		cells = spaceCells(RankByCover(cells, geometry.Scored))
	}
	var roles []CombatRole
	for i, g := range gunners {
		role := CombatRole{Pawn: g.ID, Target: top, Ranged: true}
		if i < len(cells) {
			c := cells[i]
			role.Cell = &c
		}
		roles = append(roles, role)
	}
	var duties []CombatRole
	if chokes := waveChokes(view); len(chokes) > 0 {
		// A psychic wave blocks every door it approaches (#899).
		duties = waveBlockers(chokes, rotated(brawlers(rest), relieved))
	} else {
		duties = brawlerRoles(view, rest, blocking, geometry, relieved)
	}
	roles = append(roles, duties...)
	for _, b := range brawlers(rest) {
		if !slices.ContainsFunc(duties, func(r CombatRole) bool { return r.Pawn == b.ID }) {
			roles = append(roles, CombatRole{Pawn: b.ID, Target: melee})
		}
	}
	return sortRoles(roles)
}

// reformManhunter is the manhunter tactic's re-formation row: a role's
// target is down. Gunners retarget every stop by focus fire.
func reformManhunter(view CombatView, m CombatMemory) bool {
	return squadTargetDown(view, m)
}
