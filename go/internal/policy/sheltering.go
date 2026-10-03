package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// AllowedAreaChange is one MaintainShelter sheltering move (#1326): a pawn's
// allowed area set to the Safe area, or cleared ("") once no trigger holds.
type AllowedAreaChange struct {
	Pawn   PawnID
	Animal bool
	Area   string
}

// SafeAreaLabel is the native label of the bot-owned Safe allowed area;
// AreaIntent labels an area with its plain key.
const SafeAreaLabel = SafeAreaKey

// NoKillboxAreaKey is the bot area key of the NoKillbox allowed area: the
// home area minus the killbox cells (#1327).
const NoKillboxAreaKey = "NoKillbox"

// NoKillboxAreaLabel is the native label of the NoKillbox area.
const NoKillboxAreaLabel = NoKillboxAreaKey

// KillboxCooldown is how long after the last live, unrestrained hostile
// haulers stay out of the killbox (one in-game hour).
const KillboxCooldown domain.Tick = domain.TicksPerHour

// KillboxWindowOf reports whether haulers are kept out of the killbox: a
// hostile is live (hostiles > 0) or the last one was seen (lastThreat, when
// lastKnown) less than KillboxCooldown before now. Unknown with hostiles.
func KillboxWindowOf(hostiles domain.Fact[int64], lastThreat domain.Tick, lastKnown bool, now domain.Tick) domain.Fact[bool] {
	n, known := hostiles.Value()
	if !known {
		return domain.Unknown[bool]()
	}
	return domain.Known(n > 0 || lastKnown && now-lastThreat < KillboxCooldown)
}

// KillboxHaulers are the work pawns with Hauling enabled (priority 1..4).
// Unknown when any row's work is unknown.
func KillboxHaulers(workers domain.Fact[[]WorkPawn]) domain.Fact[[]PawnID] {
	rows, known := workers.Value()
	if !known {
		return domain.Unknown[[]PawnID]()
	}
	out := []PawnID{}
	for _, w := range rows {
		work, wk := w.Work.Value()
		if !wk {
			return domain.Unknown[[]PawnID]()
		}
		for _, p := range work {
			if p.Work == WorkHauling && !p.Disabled && p.Priority > 0 {
				out = append(out, w.ID)
			}
		}
	}
	return domain.Known(out)
}

// NoKillboxCells is the home area minus the killbox cells, sorted.
func NoKillboxCells(home, killbox []domain.Cell) []domain.Cell {
	excluded := map[domain.Cell]bool{}
	for _, c := range killbox {
		excluded[c] = true
	}
	set := map[domain.Cell]bool{}
	for _, c := range home {
		if !excluded[c] {
			set[c] = true
		}
	}
	return sortedCells(set)
}

// ShelterPriority is the priority of sheltering work (the Safe area and the
// moves into it) while trigger holds. A threat is itself the ActiveCombat
// emergency and EmergencyRule vetoes priority 2 and above, so under a threat
// sheltering is an emergency need too (priority 1); development is already
// held. Fallout and weather raise no emergency and keep priority 2.
func ShelterPriority(trigger ShelterTrigger) int {
	if trigger == ShelterThreat {
		return 1
	}
	return 2
}

// ShelterTrigger is why pawns shelter now.
type ShelterTrigger string

const (
	ShelterNone        ShelterTrigger = ""
	ShelterThreat      ShelterTrigger = "threat"
	ShelterFallout     ShelterTrigger = "fallout"
	ShelterTemperature ShelterTrigger = "temperature"
)

// ShelterTriggerOf reports the active sheltering trigger: toxic fallout, a
// cold snap or heat wave whose outdoor temperature is outside the sleeping
// comfort range, or hostiles on the map (a raid or manhunter pack). Known
// false only when every trigger is known clear.
func ShelterTriggerOf(f RoutineFacts) (ShelterTrigger, bool) {
	conditions, ck := f.DisasterConditions.Value()
	weather := false
	for _, c := range conditions {
		switch c.Definition {
		case ConditionToxicFallout:
			return ShelterFallout, true
		case ConditionColdSnap, ConditionHeatWave:
			weather = true
		}
	}
	tempKnown := true
	if weather {
		out, ok := f.OutdoorTemperature.Value()
		lo, lk := f.SleepingMin.Value()
		hi, hk := f.SleepingMax.Value()
		tempKnown = ok && lk && hk
		if tempKnown && (out < lo || out > hi) {
			return ShelterTemperature, true
		}
	}
	hostiles, hk := f.Hostiles.Value()
	if hk && hostiles > 0 {
		return ShelterThreat, true
	}
	return ShelterNone, ck && tempKnown && hk
}

// PlanSheltering is MaintainShelter's sheltering planner (#1326). While a
// trigger holds, undrafted colonists (for a threat, those outside the squad's
// draft set, ShelterCombatants) and animals that take areas without a pen
// are moved into the Safe area. Once every trigger is known clear, pawns
// still restricted to the Safe area go back to unrestricted. Haulers not
// sheltered are kept to the NoKillbox area while KillboxWindow holds, and go
// back to unrestricted once it is known closed; other areas are left to
// their own planners. It keeps no history: the same facts give the
// same moves after a restart or reload.
func PlanSheltering(f RoutineFacts) []AllowedAreaChange {
	safe, _ := f.ShelterArea.Value()
	trigger, tk := ShelterTriggerOf(f)
	if !tk {
		return nil
	}
	noKill, _ := f.NoKillboxArea.Value()
	window, windowKnown := f.KillboxWindow.Value()
	haulerRows, hk := f.KillboxHaulers.Value()
	haulers := map[PawnID]bool{}
	for _, id := range haulerRows {
		haulers[id] = true
	}
	var combatants map[PawnID]bool
	if trigger == ShelterThreat {
		rows, known := f.ShelterCombatants.Value()
		if known {
			combatants = map[PawnID]bool{}
			for _, id := range rows {
				combatants[id] = true
			}
		}
	}
	var changes []AllowedAreaChange
	add := func(id PawnID, animal, shelter bool, current domain.Fact[string]) {
		area, known := current.Value()
		if !known {
			return
		}
		hauler := !animal && haulers[id]
		to, move := "", false
		switch {
		case shelter && safe != "":
			to, move = safe, area != safe
		case noKill != "" && windowKnown && window && hk && hauler:
			// Haulers stay out of the killbox through the fight and its
			// cooldown (#1327).
			to, move = noKill, area != noKill
		case safe != "" && area == safe:
			move = trigger == ShelterNone
		case noKill != "" && area == noKill:
			move = windowKnown && (!window || hk && !hauler)
		}
		if move {
			changes = append(changes, AllowedAreaChange{id, animal, to})
		}
	}
	safety, known := f.RecoverySafety.Value()
	workers, wk := f.RecoveryWorkers.Value()
	if known && wk {
		for _, worker := range workers {
			if !areaKnownFalse(worker.Dead) || !areaKnownFalse(worker.Downed) || !areaKnownFalse(worker.Drafted) || !areaKnownFalse(worker.Mental) {
				continue
			}
			// A threat shelters only pawns known outside the draft set.
			shelter := trigger != ShelterNone && (trigger != ShelterThreat || combatants != nil && !combatants[worker.Pawn])
			for _, restriction := range safety.Restrictions {
				if restriction.Pawn == worker.Pawn {
					add(worker.Pawn, false, shelter, restriction.Area)
					break
				}
			}
		}
	}
	if animals, known := f.AnimalUpkeep.Animals.Value(); known {
		for _, animal := range animals {
			if !positive(animal.SupportsAreas) || !areaKnownFalse(animal.RequiresPen) {
				continue
			}
			add(animal.ID, true, trigger != ShelterNone, animal.AllowedArea)
		}
	}
	sort.SliceStable(changes, func(i, j int) bool { return changes[i].Pawn < changes[j].Pawn })
	return changes
}

func areaKnownFalse(f domain.Fact[bool]) bool { v, k := f.Value(); return k && !v }

// ShelterHeld is whether a threat's sheltering response is complete: a
// threat triggers sheltering and every living, undowned, undrafted, sane
// colonist (at least one) is restricted to the Safe area. The clock window
// then watches the threat instead of refusing it (#1560): sheltered
// colonists wait it out on game time.
func ShelterHeld(f RoutineFacts) bool {
	trigger, tk := ShelterTriggerOf(f)
	safe, sk := f.ShelterArea.Value()
	safety, known := f.RecoverySafety.Value()
	workers, wk := f.RecoveryWorkers.Value()
	if !tk || trigger != ShelterThreat || !sk || safe == "" || !known || !wk {
		return false
	}
	held := 0
	for _, worker := range workers {
		if !areaKnownFalse(worker.Dead) || !areaKnownFalse(worker.Downed) || !areaKnownFalse(worker.Mental) {
			continue
		}
		drafted, known := worker.Drafted.Value()
		if !known {
			return false
		}
		if drafted {
			continue
		}
		in := false
		for _, restriction := range safety.Restrictions {
			if restriction.Pawn == worker.Pawn {
				area, known := restriction.Area.Value()
				in = known && area == safe
				break
			}
		}
		if !in {
			return false
		}
		held++
	}
	return held > 0
}
