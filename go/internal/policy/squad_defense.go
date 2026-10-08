package policy

import (
	"fmt"
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
	// part) rather than a pawn, engaged only once no eligible hostile pawn
	// remains (a hive's insects and a ship part's guards first). Dead is
	// destroyed; the other pawn facts are irrelevant. A ranged-equipped
	// defender listed under LinesOfFire shoots it from where it stands (the
	// native ranged predicates need the target in range now; #327); every
	// other defender walks to it in melee.
	Building bool
	// Anomaly is the pawn row's Anomaly facts (#1739); unknown without
	// Anomaly.
	Anomaly domain.Fact[PawnAnomaly]
	// Mech is a Mech_ pawn kind (#970), marked from the combat view.
	Mech        bool
	LinesOfFire map[domain.PawnID]bool
	// MeleePower is the pawn's MeleeDPS scaled by health (#969).
	MeleePower domain.Fact[float64]
}

// SquadDefenderFacts describes one candidate defender. Health/NeedsTend mirror
// combat_health_hold: a defender who needs tending or is below the native
// combat-health threshold is never assigned.
type SquadDefenderFacts struct {
	ID                                               domain.PawnID
	Dead, Downed, Drafted, MentalState, PlayerForced domain.Fact[bool]
	// DraftOwned is whether a live plan needs the pawn drafted (#939). A
	// drafted pawn a plan needs is busy elsewhere; a drafted pawn no plan
	// needs is a candidate like any
	// undrafted colonist and the draft adopts it (#461).
	DraftOwned domain.Fact[bool]
	// Deathresting is a pawn known to be in deathrest (#1690): never a
	// defender.
	Deathresting                         bool
	QueuedJobs                           domain.Fact[uint32]
	ViolenceCapable, NeedsTend           domain.Fact[bool]
	HealthFraction                       domain.Fact[float64]
	RangedEquipped, MeleeEquipped, Armed domain.Fact[bool]
	// FrontLine is the pawn's place in FrontLine's split of the roster: a
	// line holder (Tough, Nimble, Brawler or Melee over Shooting) takes a
	// melee opponent before a shooter does, and shooters take ranged
	// opponents and firing cells first. False when the profile is unknown;
	// it orders preference only, never eligibility.
	FrontLine bool
	// Armor is the pawn's worn sharp armor rating; it ranks choke
	// blockers (#864). Unknown ranks after every known rating.
	Armor domain.Fact[float64]
	// MeleePower is the pawn's MeleeDPS scaled by health (#969).
	MeleePower domain.Fact[float64]
	// RangedDPS is the primary ranged weapon's damage per second, 0
	// without one (#1188).
	RangedDPS domain.Fact[float64]
	// Warden is a pawn with Warden work enabled; a prison break ranks it
	// first (#1080).
	Warden bool `json:",omitempty"`
}

// meleeBeats reports whether defenders can win a melee against t (#969):
// their summed melee power above t's. A gun among them, a building or
// any unknown power is not a melee this compares.
func meleeBeats(t SquadThreatFacts, defenders []SquadDefenderFacts) bool {
	foe, ok := t.MeleePower.Value()
	if t.Building || !ok {
		return true
	}
	sum := 0.0
	for _, d := range defenders {
		power, pk := d.MeleePower.Value()
		if !pk || positive(d.RangedEquipped) {
			return true
		}
		sum += power
	}
	return sum > foe
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
// never sent to melee (#948) and is not equipped inline (RoundsEquipPlanner arms
// colonists on its own independently-scheduled goal).
//
// Assignments are a proposal only; native (melee intents) and
// EvaluateRangedDefense
// re-validate each chosen (defender, target) pair against fresh facts before
// dispatch, and downed/dead opponents drop out at that point without being
// re-selected here.
// squadDraftBusy is whether the pawn's draft is spoken for: drafted under an
// owned claim, or drafted with the claim unknown. Forced and queued jobs
// affect candidate preference, never draft ownership.
func squadDraftBusy(d SquadDefenderFacts) (busy, known bool) {
	drafted, tk := d.Drafted.Value()
	if !tk {
		return false, false
	}
	if !drafted {
		return false, true
	}
	owned, ok := d.DraftOwned.Value()
	return !ok || owned, true
}

// squadDefenderEligible is the shared combat_health_hold gate: known health,
// not drafted under another claim, violence-capable, not
// needing tending and above the native combat-health threshold.
func squadDefenderEligible(d SquadDefenderFacts) bool { return len(squadDefenderReasons(d)) == 0 }

// squadDefenderReasons is every reason d is not eligible, empty when eligible.
// Each check stands alone: an unknown fact is a reason, and the known ones
// are still checked.
func squadDefenderReasons(d SquadDefenderFacts) []string {
	var why []string
	if dead, ok := d.Dead.Value(); !ok {
		why = append(why, "dead unknown")
	} else if dead {
		why = append(why, "dead")
	}
	if downed, ok := d.Downed.Value(); !ok {
		why = append(why, "downed unknown")
	} else if downed {
		why = append(why, "downed")
	}
	if busy, ok := squadDraftBusy(d); !ok {
		why = append(why, "drafted unknown")
	} else if busy {
		why = append(why, "drafted under another claim")
	}
	if mental, ok := d.MentalState.Value(); !ok {
		why = append(why, "mental state unknown")
	} else if mental {
		why = append(why, "in a mental state")
	}
	if violent, ok := d.ViolenceCapable.Value(); !ok {
		why = append(why, "violence capability unknown")
	} else if !violent {
		why = append(why, "not violence-capable")
	}
	if needsTend, ok := d.NeedsTend.Value(); !ok {
		why = append(why, "needs-tend unknown")
	} else if needsTend {
		why = append(why, "needs tending")
	}
	if d.Deathresting {
		why = append(why, "deathresting")
	}
	if health, ok := d.HealthFraction.Value(); !ok {
		why = append(why, "health unknown")
	} else if health <= squadHurtHealth {
		why = append(why, fmt.Sprintf("health %.2f", health))
	}
	return why
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
		if !dk || !wk || dead || downed {
			return false
		}
		if t.Mech {
			return true
		}
		if !hk || !ak {
			return false
		}
		if humanlike || MeleeEntity(t) {
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
	standing := false
	for _, t := range threats {
		if !t.Building {
			dead, dk := t.Dead.Value()
			downed, wk := t.Downed.Value()
			standing = standing || !dk || !wk || !dead && !downed
		}
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
	// Any standing hostile pawn guards it, eligible or not: a hive is not
	// attacked while its insects still fight (#948).
	if len(threatPool) == 0 && !standing {
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
	byID := map[domain.PawnID]SquadDefenderFacts{}
	for _, d := range defenders {
		if eligibleDefender(d) {
			defenderPool = append(defenderPool, d)
			byID[d.ID] = d
		}
	}
	sort.Slice(defenderPool, func(i, j int) bool {
		a, b := orderedWorkCost(defenderPool[i].PlayerForced, defenderPool[i].QueuedJobs), orderedWorkCost(defenderPool[j].PlayerForced, defenderPool[j].QueuedJobs)
		if a != b {
			return a < b
		}
		return defenderPool[i].ID < defenderPool[j].ID
	})

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
				// Melee needs a weapon in hand: fists lose to anything
				// worth fighting (#948).
				if armed, ak := d.Armed.Value(); !ranged && (!ak || !armed) {
					continue
				}
				used[d.ID] = true
				return d.ID, true
			}
		}
		return "", false
	}

	// A building takes the shooters with a line of fire on it first, then
	// whoever is left in melee.
	takeBuilding := func(t SquadThreatFacts) (domain.PawnID, SquadMode, bool) {
		for _, d := range defenderPool {
			equipped, known := d.RangedEquipped.Value()
			if !used[d.ID] && known && equipped && t.LinesOfFire[d.ID] {
				used[d.ID] = true
				return d.ID, SquadRanged, true
			}
		}
		id, found := take(false)
		return id, SquadMelee, found
	}

	var assignments []SquadAssignment
	for _, t := range threatPool {
		if len(assignments)+requiredDefendersPerFoe > maxSquadDefenders {
			break
		}
		ranged, known := t.RangedEquipped.Value()
		if !t.Building && !known {
			continue
		}
		var chosen []SquadAssignment
		ok := true
		for i := 0; i < requiredDefendersPerFoe; i++ {
			var id domain.PawnID
			var found bool
			mode := SquadMelee
			if t.Building {
				id, mode, found = takeBuilding(t)
			} else {
				if ranged {
					mode = SquadRanged
				}
				id, found = take(ranged)
			}
			if !found {
				ok = false
				break
			}
			chosen = append(chosen, SquadAssignment{Defender: id, Target: t.ID, Mode: mode})
		}
		if ok && !ranged {
			var pair []SquadDefenderFacts
			for _, a := range chosen {
				pair = append(pair, byID[a.Defender])
			}
			ok = meleeBeats(t, pair)
		}
		if !ok {
			// Release any partial reservation; an unsupported encounter is an
			// explicit hold, not a partially defended one.
			for _, a := range chosen {
				delete(used, a.Defender)
			}
			continue
		}
		assignments = append(assignments, chosen...)
	}
	return assignments, len(assignments) > 0
}

// SelectTribalRaiderDefense mirrors single_raider_defense's tribal branch: a
// lone humanlike, non-ranged opponent is bounded tighter than the general
// N-opponent case above — three healthy (>=85%, not needing tend) already-
// armed defenders instead of two. There is no synchronous per-encounter
// weapon fetch: an unarmed candidate is simply excluded here rather than
// equipped inline: EnsureBasicDefense arms colonists on its own
// independently-scheduled goal (see RoundsEquipPlanner), so this method
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
		drafted, tk := squadDraftBusy(d)
		mental, mk := d.MentalState.Value()
		violent, vk := d.ViolenceCapable.Value()
		needsTend, nk := d.NeedsTend.Value()
		health, hk2 := d.HealthFraction.Value()
		armed, ak := d.Armed.Value()
		if !dk || !wk || !tk || !mk || !vk || !nk || !hk2 || !ak {
			return false
		}
		if dead || downed || drafted || mental || !violent || needsTend || !armed || d.Deathresting {
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
		a, b := orderedWorkCost(pool[i].PlayerForced, pool[i].QueuedJobs), orderedWorkCost(pool[j].PlayerForced, pool[j].QueuedJobs)
		if a != b {
			return a < b
		}
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
	if len(pool) < 3 || !meleeBeats(threat, pool[:3]) {
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
