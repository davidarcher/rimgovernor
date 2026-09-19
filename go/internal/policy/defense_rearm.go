package policy

import (
	"math"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// DefenseTurretFacts is one standing tier turret's service state from the
// power census (turrets are power consumers, so the census carries their
// refuelable comp as it does a generator's). Every field is an observation;
// an unknown fuel state is neither a deficit nor a rearm.
type DefenseTurretFacts struct {
	ID               string
	Cell             domain.Cell
	Powered          domain.Fact[bool]
	OutOfFuel        domain.Fact[bool]
	Fuel, TargetFuel domain.Fact[float64]
	// FuelDefinitions are the definitions the barrel accepts (steel for the
	// mini turret), in the census's order.
	FuelDefinitions []Resource
}

// DefenseRearm is one refuel order for an empty barrel: the pawn that
// carries it and the fuel in stock it will load.
type DefenseRearm struct {
	Turret string
	Cell   domain.Cell
	Pawn   PawnID
	Fuel   Resource
}

// DefenseTurretUpkeep is the turret tier's observed service deficits once
// every tier stands. Unpowered and Empty are the deficit cells (an empty
// barrel cannot fire, whether or not the definition marks fuel mandatory);
// Rearm is the one order proposed for the first empty barrel whose fuel is
// in stock; Shortage is the fuel the empty barrels need and stock lacks,
// per definition, for the resource policy to raise.
type DefenseTurretUpkeep struct {
	Unpowered []domain.Cell
	Empty     []domain.Cell
	Rearm     []DefenseRearm
	Shortage  []Amount
}

// DefenseRearmTurrets measures the tier's turrets. The rearm's pawn is an
// available colonist whose Hauling work (the native refuel work giver's
// type) is not disabled, preferring one with it enabled at the highest
// priority; the order is a forced one, so the game's own auto-refuel
// setting and threshold do not gate it. The shortage per fuel definition is
// the barrels' fuel gap in fuel units, an estimate: the native units-per-
// item multiplier is not observed, and the resource policy's floor only
// needs to be above zero to start sourcing.
func DefenseRearmTurrets(turrets []DefenseTurretFacts, workers []WorkPawn, stock domain.Fact[map[Resource]int64]) DefenseTurretUpkeep {
	var out DefenseTurretUpkeep
	stocked, stockKnown := stock.Value()
	shortage := map[Resource]int64{}
	pawn, pawnOK := defenseRearmPawn(workers)
	for _, t := range turrets {
		if on, known := t.Powered.Value(); known && !on {
			out.Unpowered = append(out.Unpowered, t.Cell)
		}
		empty, known := t.OutOfFuel.Value()
		if !known || !empty {
			continue
		}
		out.Empty = append(out.Empty, t.Cell)
		if !stockKnown {
			continue
		}
		fuel := Resource("")
		for _, d := range t.FuelDefinitions {
			if stocked[d] > 0 {
				fuel = d
				break
			}
		}
		if fuel == "" {
			if len(t.FuelDefinitions) > 0 {
				shortage[t.FuelDefinitions[0]] += defenseFuelGap(t)
			}
			continue
		}
		if pawnOK && len(out.Rearm) == 0 && t.ID != "" {
			out.Rearm = append(out.Rearm, DefenseRearm{Turret: t.ID, Cell: t.Cell, Pawn: pawn, Fuel: fuel})
		}
	}
	for resource, count := range shortage {
		out.Shortage = append(out.Shortage, Amount{Resource: resource, Count: count})
	}
	sort.Slice(out.Shortage, func(i, j int) bool { return out.Shortage[i].Resource < out.Shortage[j].Resource })
	return out
}

// defenseFuelGap is the fuel a barrel is short of its target, at least one
// unit for an empty barrel whose levels are unknown.
func defenseFuelGap(t DefenseTurretFacts) int64 {
	fuel, fk := t.Fuel.Value()
	target, tk := t.TargetFuel.Value()
	if !fk || !tk || target <= fuel || math.IsNaN(target-fuel) || math.IsInf(target-fuel, 0) {
		return 1
	}
	gap := int64(math.Ceil(target - fuel))
	if gap > 10000 {
		gap = 10000
	}
	return max(gap, 1)
}

func defenseRearmPawn(workers []WorkPawn) (PawnID, bool) {
	best, bestPriority, found := PawnID(""), 0, false
	for _, w := range workers {
		available, ak := w.Available.Value()
		applies, pk := w.Applies.Value()
		work, wk := w.Work.Value()
		if !ak || !available || !pk || !applies || !wk {
			continue
		}
		priority, disabled, listed := 0, false, false
		for _, p := range work {
			if p.Work == WorkHauling {
				priority, disabled, listed = p.Priority, p.Disabled, true
			}
		}
		if !listed || disabled {
			continue
		}
		// Enabled hauling (priority 1..4) beats disabled-by-priority (0);
		// among enabled, the lower number is the higher priority.
		rank := 0
		if priority > 0 {
			rank = 5 - priority
		}
		if !found || rank > bestPriority || rank == bestPriority && w.ID < best {
			best, bestPriority, found = w.ID, rank, true
		}
	}
	return best, found
}

// ResourceGoalTargets merges derived stock floors (a turret barrel's fuel the
// census found no stock of) into the operator's MaintainResource targets; a
// configured floor is never lowered by a derived one.
func ResourceGoalTargets(configured, derived map[Resource]int64) map[Resource]int64 {
	if len(derived) == 0 {
		return configured
	}
	out := make(map[Resource]int64, len(configured)+len(derived))
	for r, v := range configured {
		out[r] = v
	}
	for r, v := range derived {
		if v > 0 && v > out[r] && validResource(r) && v <= 10000 {
			out[r] = v
		}
	}
	return out
}
