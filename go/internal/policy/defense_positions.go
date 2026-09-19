package policy

import (
	"sort"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// DefensiveThreatFacts is what SelectDefensivePositions needs to know about
// one hostile: the native lord evidence that says how the raid intends to
// arrive, and how close it already stands to a colonist.
type DefensiveThreatFacts struct {
	ID                          PawnID
	Dead, Downed, Humanlike     domain.Fact[bool]
	LordJobClass, LordToilClass domain.Fact[string]
	NearestColonistDistance     domain.Fact[float64]
	// Position is the hostile's cell; against the layout's firing line and
	// corridor direction it says whether the raid is still in front of the
	// line or already past it.
	Position domain.Fact[domain.Cell]
}

// DefensivePosition sends one ranged defender to one firing cell and names
// the opponent it engages from there.
type DefensivePosition struct {
	Defender domain.PawnID
	Cell     domain.Cell
	Target   PawnID
}

// defensiveEngagedDistance is the nearest-colonist distance at or under which
// a hostile is already engaged inside the colony, so parking defenders on the
// firing line would leave whoever it reached alone. PawnState carries no
// position, so this native distance is the only inside/outside evidence.
const defensiveEngagedDistance = 12.0

// SelectDefensivePositions ports the M3 rule: only when the layout stands
// (the firing cells supplied, facing the corridor direction toward) and
// every live hostile is an ordinary edge-arriving assault still in front of
// the line does the colony hold the line. Sappers, breachers, sieges, drop
// pods, hostiles without a lord or with unknown lord evidence, hostiles
// already within engaged distance and hostiles standing at or behind the
// line's cover row all return false so the caller falls back to
// SelectSquadDefense. Ranged, healthy, idle defenders take firing cells in
// cell order, one each, and engage the lowest-ID live hostile; unarmed or
// melee-only colonists are never positioned.
func SelectDefensivePositions(firing []domain.Cell, toward domain.Rotation, threats []DefensiveThreatFacts, defenders []SquadDefenderFacts) ([]DefensivePosition, bool) {
	if len(firing) == 0 || len(threats) == 0 {
		return nil, false
	}
	var live []DefensiveThreatFacts
	for _, t := range threats {
		dead, dk := t.Dead.Value()
		downed, wk := t.Downed.Value()
		if !dk || !wk {
			return nil, false
		}
		if dead || downed {
			continue
		}
		humanlike, hk := t.Humanlike.Value()
		job, jk := t.LordJobClass.Value()
		toil, tk := t.LordToilClass.Value()
		distance, nk := t.NearestColonistDistance.Value()
		position, pk := t.Position.Value()
		if !hk || !humanlike || !jk || !tk || !nk || !pk || !edgeAssault(job, toil) || distance <= defensiveEngagedDistance || BehindFiringLine(firing, toward, position) {
			return nil, false
		}
		live = append(live, t)
	}
	if len(live) == 0 {
		return nil, false
	}
	sort.Slice(live, func(i, j int) bool { return live[i].ID < live[j].ID })
	var pool []SquadDefenderFacts
	for _, d := range defenders {
		if squadDefenderEligible(d) && positive(d.RangedEquipped) {
			pool = append(pool, d)
		}
	}
	if len(pool) == 0 {
		return nil, false
	}
	// Shooters take the firing cells before the line holders do.
	sort.Slice(pool, func(i, j int) bool {
		if pool[i].FrontLine != pool[j].FrontLine {
			return !pool[i].FrontLine
		}
		return pool[i].ID < pool[j].ID
	})
	seen := map[domain.Cell]bool{}
	var out []DefensivePosition
	for _, cell := range firing {
		if seen[cell] || len(out) == len(pool) || len(out) == maxSquadDefenders {
			continue
		}
		seen[cell] = true
		out = append(out, DefensivePosition{Defender: pool[len(out)].ID, Cell: cell, Target: live[0].ID})
	}
	return out, len(out) > 0
}

// HoldCompromised says whether a standing hold must be abandoned for squad
// defense: some live hostile is known to be at or behind the line's cover
// row, or its lord evidence is known to be anything but an edge assault (a
// breach or sapper toil the raid switched to mid-fight). Unknown evidence
// never abandons a hold; the caller keeps the line until it has proof.
func HoldCompromised(firing []domain.Cell, toward domain.Rotation, threats []DefensiveThreatFacts) bool {
	for _, t := range threats {
		dead, dk := t.Dead.Value()
		downed, wk := t.Downed.Value()
		if !dk || !wk || dead || downed {
			continue
		}
		if job, jk := t.LordJobClass.Value(); jk {
			if toil, tk := t.LordToilClass.Value(); tk && !edgeAssault(job, toil) {
				return true
			}
		}
		if position, pk := t.Position.Value(); pk && BehindFiringLine(firing, toward, position) {
			return true
		}
	}
	return false
}

// BehindFiringLine reports whether cell lies at or past the firing line's
// cover row along the corridor direction: the sandbags stand one cell in
// front of each firing cell (toward the map edge), so anything projected
// onto toward at or beyond the nearest cover row has crossed the line. A
// raider in the trap lane or at the sandbags' outer face is still in front.
// Without a corridor direction there is no line to be behind.
func BehindFiringLine(firing []domain.Cell, toward domain.Rotation, cell domain.Cell) bool {
	v, ok := towardVector(toward)
	if !ok || len(firing) == 0 {
		return false
	}
	project := func(c domain.Cell) int32 { return c.X*v.X + c.Z*v.Z }
	cover := project(firing[0]) - 1
	for _, f := range firing[1:] {
		if p := project(f) - 1; p < cover {
			cover = p
		}
	}
	return project(cell) >= cover
}

func towardVector(r domain.Rotation) (domain.Cell, bool) {
	switch r {
	case domain.North:
		return directions[0], true
	case domain.East:
		return directions[1], true
	case domain.South:
		return directions[2], true
	case domain.West:
		return directions[3], true
	}
	return domain.Cell{}, false
}

// edgeAssault recognises the native lord classes of a walk-in assault. Any
// sapper, breach, siege or drop-arrival toil is excluded by name rather than
// inferred from anything else.
func edgeAssault(job, toil string) bool {
	if job != "LordJob_AssaultColony" {
		return false
	}
	for _, bypass := range []string{"Sapper", "Breach", "Siege", "Drop"} {
		if strings.Contains(toil, bypass) {
			return false
		}
	}
	return strings.HasPrefix(toil, "LordToil_")
}
