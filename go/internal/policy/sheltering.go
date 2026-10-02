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

// SafeAreaLabel is the native label of the bot-owned Safe allowed area
// (NativeAreaIntent.LabelPrefix + SafeAreaKey).
const SafeAreaLabel = "RimGovernor " + SafeAreaKey

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
// still restricted to the Safe area go back to unrestricted; other areas are
// left to their own planners. It keeps no history: the same facts give the
// same moves after a restart or reload.
func PlanSheltering(f RoutineFacts) []AllowedAreaChange {
	safe, sk := f.ShelterArea.Value()
	trigger, tk := ShelterTriggerOf(f)
	if !sk || safe == "" || !tk {
		return nil
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
		if shelter && area != safe {
			changes = append(changes, AllowedAreaChange{id, animal, safe})
		} else if !shelter && trigger == ShelterNone && area == safe {
			changes = append(changes, AllowedAreaChange{id, animal, ""})
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
