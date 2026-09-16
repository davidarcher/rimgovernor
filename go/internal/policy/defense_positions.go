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
// (the firing cells supplied) and every live hostile is an ordinary
// edge-arriving assault does the colony hold the line. Sappers, breachers,
// sieges, drop pods, hostiles without a lord or with unknown lord evidence,
// and hostiles already within engaged distance all return false so the
// caller falls back to SelectSquadDefense. Ranged, healthy, idle defenders
// take firing cells in cell order, one each, and engage the lowest-ID live
// hostile; unarmed or melee-only colonists are never positioned.
func SelectDefensivePositions(firing []domain.Cell, threats []DefensiveThreatFacts, defenders []SquadDefenderFacts) ([]DefensivePosition, bool) {
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
		if !hk || !humanlike || !jk || !tk || !nk || !edgeAssault(job, toil) || distance <= defensiveEngagedDistance {
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
	sort.Slice(pool, func(i, j int) bool { return pool[i].ID < pool[j].ID })
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
