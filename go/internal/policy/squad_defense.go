package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// SquadThreatFacts describes one candidate opponent. RangedEquipped decides
// whether an assigned defender must also be ranged-equipped: a ranged
// opponent may not be engaged by a melee-only defender.
type SquadThreatFacts struct {
	ID                PawnID
	Dead, Downed      domain.Fact[bool]
	Humanlike, Animal domain.Fact[bool]
	BodySize          domain.Fact[float64]
	Manhunter         domain.Fact[bool]
	RangedEquipped    domain.Fact[bool]
}

// SquadDefenderFacts describes one candidate defender. Health/NeedsTend mirror
// combat_health_hold: a defender who needs tending or is below the native
// combat-health threshold is never assigned.
type SquadDefenderFacts struct {
	ID                                               domain.PawnID
	Dead, Downed, Drafted, MentalState, PlayerForced domain.Fact[bool]
	QueuedJobs                                       domain.Fact[uint32]
	ViolenceCapable, NeedsTend                       domain.Fact[bool]
	HealthFraction                                   domain.Fact[float64]
	RangedEquipped, MeleeEquipped                    domain.Fact[bool]
}

type SquadMode uint8

const (
	SquadMelee SquadMode = iota + 1
	SquadRanged
)

type SquadAssignment struct {
	Defender domain.PawnID
	Target   PawnID
	Mode     SquadMode
}

const (
	maxSquadOpponents       = 4
	maxSquadDefenders       = 8
	requiredDefendersPerFoe = 2
)

// SelectSquadDefense ports the bounded core of combat_method.squad_defense:
// at least two capable defenders per observed opponent, up to four opponents
// and eight defenders, with a ranged opponent requiring a ranged-equipped
// defender. This proposal covers the general N-opponent case only; the
// single-raider tribal 3-defender/85%-health sub-case and unarmed-defender
// weapon fetch are deferred (native AI still defends adequately without them,
// just with the standard bounds instead of the narrower special case).
//
// Assignments are a proposal only; EvaluateMeleeDefense/EvaluateRangedDefense
// re-validate each chosen (defender, target) pair against fresh facts before
// dispatch, and downed/dead opponents drop out at that point without being
// re-selected here.
func SelectSquadDefense(threats []SquadThreatFacts, defenders []SquadDefenderFacts) ([]SquadAssignment, bool) {
	eligibleThreat := func(t SquadThreatFacts) bool {
		dead, dk := t.Dead.Value()
		downed, wk := t.Downed.Value()
		humanlike, hk := t.Humanlike.Value()
		animal, ak := t.Animal.Value()
		if !dk || !wk || !hk || !ak || dead || downed {
			return false
		}
		if humanlike {
			return true
		}
		if !animal {
			return false
		}
		size, sk := t.BodySize.Value()
		manhunter, mk := t.Manhunter.Value()
		if !sk || !mk || !manhunter {
			return false
		}
		return size >= 0 && size <= 4
	}
	eligibleDefender := func(d SquadDefenderFacts) bool {
		dead, dk := d.Dead.Value()
		downed, wk := d.Downed.Value()
		drafted, tk := d.Drafted.Value()
		mental, mk := d.MentalState.Value()
		forced, fk := d.PlayerForced.Value()
		queued, qk := d.QueuedJobs.Value()
		violent, vk := d.ViolenceCapable.Value()
		needsTend, nk := d.NeedsTend.Value()
		health, hk := d.HealthFraction.Value()
		if !dk || !wk || !tk || !mk || !fk || !qk || !vk || !nk || !hk {
			return false
		}
		if dead || downed || drafted || mental || forced || queued != 0 || !violent || needsTend {
			return false
		}
		return health > float64(float32(0.5005))
	}

	var threatPool []SquadThreatFacts
	for _, t := range threats {
		if eligibleThreat(t) {
			threatPool = append(threatPool, t)
		}
	}
	sort.Slice(threatPool, func(i, j int) bool { return threatPool[i].ID < threatPool[j].ID })
	if len(threatPool) > maxSquadOpponents {
		threatPool = threatPool[:maxSquadOpponents]
	}
	if len(threatPool) == 0 {
		return nil, false
	}

	var defenderPool []SquadDefenderFacts
	for _, d := range defenders {
		if eligibleDefender(d) {
			defenderPool = append(defenderPool, d)
		}
	}
	sort.Slice(defenderPool, func(i, j int) bool { return defenderPool[i].ID < defenderPool[j].ID })

	used := map[domain.PawnID]bool{}
	take := func(ranged bool) (domain.PawnID, bool) {
		for _, d := range defenderPool {
			if used[d.ID] {
				continue
			}
			equipped, known := d.RangedEquipped.Value()
			if ranged && (!known || !equipped) {
				continue
			}
			used[d.ID] = true
			return d.ID, true
		}
		return "", false
	}

	var assignments []SquadAssignment
	for _, t := range threatPool {
		if len(assignments)+requiredDefendersPerFoe > maxSquadDefenders {
			break
		}
		ranged, known := t.RangedEquipped.Value()
		if !known {
			continue
		}
		mode := SquadMelee
		if ranged {
			mode = SquadRanged
		}
		var chosen []domain.PawnID
		ok := true
		for i := 0; i < requiredDefendersPerFoe; i++ {
			id, found := take(ranged)
			if !found {
				ok = false
				break
			}
			chosen = append(chosen, id)
		}
		if !ok {
			// Release any partial reservation; an unsupported encounter is an
			// explicit hold, not a partially defended one.
			for _, id := range chosen {
				delete(used, id)
			}
			continue
		}
		for _, id := range chosen {
			assignments = append(assignments, SquadAssignment{Defender: id, Target: t.ID, Mode: mode})
		}
	}
	return assignments, len(assignments) > 0
}
