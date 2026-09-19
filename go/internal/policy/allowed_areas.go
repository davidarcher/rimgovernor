package policy

import (
	"slices"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// AllowedAreaChange is derived from the current census, never from the origin
// of a saved restriction. An empty Area restores access to ordinary food/work.
// Native admission must still prove a refuge is safe and reachable.
type AllowedAreaChange struct {
	Pawn   PawnID
	Animal bool
	Area   string
}

// PlanAllowedAreas keeps roof protection while it is needed and otherwise
// removes saved restrictions. Unknown safety cannot authorize widening access.
// It deliberately has no history: restart, reload and repeated Manual edits
// must produce the same correction from the same current needs.
func PlanAllowedAreas(f RoutineFacts) []AllowedAreaChange {
	safety, known := f.RecoverySafety.Value()
	hazard, hk := safety.RoofHazard.Value()
	if !known || !hk {
		return nil
	}
	areas := slices.Clone(safety.SafeAreas)
	sort.Strings(areas)
	var changes []AllowedAreaChange
	add := func(id PawnID, animal bool, current domain.Fact[string]) {
		area, known := current.Value()
		if !known {
			return
		}
		if hazard {
			if slices.Contains(areas, area) {
				return
			}
			for _, target := range areas {
				changes = append(changes, AllowedAreaChange{id, animal, target})
			}
		} else if area != "" {
			changes = append(changes, AllowedAreaChange{id, animal, ""})
		}
	}
	workers, wk := f.RecoveryWorkers.Value()
	if wk && len(workers) == len(safety.Restrictions) {
		for _, worker := range workers {
			if !areaKnownFalse(worker.Dead) || !areaKnownFalse(worker.Downed) || !areaKnownFalse(worker.Drafted) || !areaKnownFalse(worker.Mental) {
				continue
			}
			for _, restriction := range safety.Restrictions {
				if restriction.Pawn == worker.Pawn {
					add(worker.Pawn, false, restriction.Area)
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
			add(animal.ID, true, animal.AllowedArea)
		}
	}
	sort.SliceStable(changes, func(i, j int) bool { return changes[i].Pawn < changes[j].Pawn })
	return changes
}

func areaKnownFalse(f domain.Fact[bool]) bool { v, k := f.Value(); return k && !v }
