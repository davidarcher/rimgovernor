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
	// Hunting is an animal the emergency census lists as hunting a colonist;
	// unknown counts as not hunting.
	Hunting        domain.Fact[bool]
	RangedEquipped domain.Fact[bool]
	// Building marks a hostile building (an insect hive, a crashed ship
	// part) rather than a pawn: engaged in melee, since the defender walks
	// to it and the native ranged predicates need it in range now, and
	// only once no eligible hostile pawn remains (a hive's insects and a
	// ship part's guards first). Dead is destroyed; the other pawn facts
	// are irrelevant.
	Building bool
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
	RangedEquipped, MeleeEquipped, Armed             domain.Fact[bool]
	// FrontLine is the pawn's place in FrontLine's split of the roster: a
	// line holder (Tough, Nimble, Brawler or Melee over Shooting) takes a
	// melee opponent before a shooter does, and shooters take ranged
	// opponents and firing cells first. False when the profile is unknown;
	// it orders preference only, never eligibility.
	FrontLine bool
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
// single-raider tribal 3-defender/85%-health sub-case is the caller's
// separate SelectTribalRaiderDefense preference, and an unarmed defender is
// simply excluded here rather than equipped inline (RoutineEquipPlanner arms
// colonists on its own independently-scheduled goal).
//
// Assignments are a proposal only; EvaluateMeleeDefense/EvaluateRangedDefense
// re-validate each chosen (defender, target) pair against fresh facts before
// dispatch, and downed/dead opponents drop out at that point without being
// re-selected here.
// squadDefenderEligible is the shared combat_health_hold gate: every fact
// known, idle, undrafted, violence-capable, not needing tending and above
// the native combat-health threshold.
func squadDefenderEligible(d SquadDefenderFacts) bool {
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

func SelectSquadDefense(threats []SquadThreatFacts, defenders []SquadDefenderFacts) ([]SquadAssignment, bool) {
	eligibleThreat := func(t SquadThreatFacts) bool {
		dead, dk := t.Dead.Value()
		if t.Building {
			return dk && !dead
		}
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
		hunting, _ := t.Hunting.Value()
		if !sk || !mk || !manhunter && !hunting {
			return false
		}
		return size > 0 && size <= 4
	}
	eligibleDefender := squadDefenderEligible

	var threatPool, buildings []SquadThreatFacts
	for _, t := range threats {
		switch {
		case !eligibleThreat(t):
		case t.Building:
			buildings = append(buildings, t)
		default:
			threatPool = append(threatPool, t)
		}
	}
	// Buildings wait for the field to clear: a hive's insects and a ship
	// part's guards are the live danger, the building itself goes nowhere.
	if len(threatPool) == 0 {
		threatPool = buildings
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

	// A melee opponent goes to the line holders first and a ranged one to
	// the shooters; either falls back to whoever is left.
	used := map[domain.PawnID]bool{}
	take := func(ranged bool) (domain.PawnID, bool) {
		for _, preferred := range []bool{true, false} {
			for _, d := range defenderPool {
				if used[d.ID] || preferred && d.FrontLine == ranged {
					continue
				}
				equipped, known := d.RangedEquipped.Value()
				if ranged && (!known || !equipped) {
					continue
				}
				used[d.ID] = true
				return d.ID, true
			}
		}
		return "", false
	}

	var assignments []SquadAssignment
	for _, t := range threatPool {
		if len(assignments)+requiredDefendersPerFoe > maxSquadDefenders {
			break
		}
		ranged, known := t.RangedEquipped.Value()
		if t.Building {
			ranged, known = false, true
		}
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

// SelectTribalRaiderDefense mirrors single_raider_defense's tribal branch: a
// lone humanlike, non-ranged opponent is bounded tighter than the general
// N-opponent case above — three healthy (>=85%, not needing tend) already-
// armed defenders instead of two. There is no synchronous per-encounter
// weapon fetch: an unarmed candidate is simply excluded here rather than
// equipped inline: EnsureBasicDefense arms colonists on its own
// independently-scheduled goal (see RoutineEquipPlanner), so this method
// just holds — via the caller falling back to SelectSquadDefense's general,
// unarmed-tolerant bound — until enough defenders are already armed.
func SelectTribalRaiderDefense(threat SquadThreatFacts, defenders []SquadDefenderFacts) ([]SquadAssignment, bool) {
	if threat.Building {
		return nil, false
	}
	dead, dk := threat.Dead.Value()
	downed, wk := threat.Downed.Value()
	humanlike, hk := threat.Humanlike.Value()
	ranged, rk := threat.RangedEquipped.Value()
	if !dk || !wk || !hk || !rk || dead || downed || !humanlike || ranged {
		return nil, false
	}
	eligible := func(d SquadDefenderFacts) bool {
		dead, dk := d.Dead.Value()
		downed, wk := d.Downed.Value()
		drafted, tk := d.Drafted.Value()
		mental, mk := d.MentalState.Value()
		forced, fk := d.PlayerForced.Value()
		queued, qk := d.QueuedJobs.Value()
		violent, vk := d.ViolenceCapable.Value()
		needsTend, nk := d.NeedsTend.Value()
		health, hk2 := d.HealthFraction.Value()
		armed, ak := d.Armed.Value()
		if !dk || !wk || !tk || !mk || !fk || !qk || !vk || !nk || !hk2 || !ak {
			return false
		}
		if dead || downed || drafted || mental || forced || queued != 0 || !violent || needsTend || !armed {
			return false
		}
		return health >= 0.85
	}
	var pool []SquadDefenderFacts
	for _, d := range defenders {
		if eligible(d) {
			pool = append(pool, d)
		}
	}
	sort.Slice(pool, func(i, j int) bool {
		iRanged, _ := pool[i].RangedEquipped.Value()
		jRanged, _ := pool[j].RangedEquipped.Value()
		if iRanged != jRanged {
			return iRanged
		}
		if pool[i].FrontLine != pool[j].FrontLine {
			return pool[i].FrontLine
		}
		return pool[i].ID < pool[j].ID
	})
	if len(pool) < 3 {
		return nil, false
	}
	var assignments []SquadAssignment
	for _, d := range pool[:3] {
		mode := SquadMelee
		if ranged, _ := d.RangedEquipped.Value(); ranged {
			mode = SquadRanged
		}
		assignments = append(assignments, SquadAssignment{Defender: d.ID, Target: threat.ID, Mode: mode})
	}
	return assignments, true
}
