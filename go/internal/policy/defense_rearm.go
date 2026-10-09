package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// DefenseTurretFacts is one standing tier turret's service state from the
// power census (turrets are power consumers, so the census carries their
// refuelable comp as it does a generator's). Every field is an observation;
// an unknown fuel state is neither a deficit nor a rearm.
type DefenseTurretFacts struct {
	ID         string
	Definition string
	Cell       domain.Cell
	// DPS is the turret's observed damage per second (#1188).
	DPS       domain.Fact[float64]
	Powered   domain.Fact[bool]
	OutOfFuel domain.Fact[bool]
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
// in stock. The fuel an empty barrel needs and stock lacks is the fuel
// runway's demand (PlanFuelRunway), not this plan's.
type DefenseTurretUpkeep struct {
	Unpowered []domain.Cell
	Empty     []domain.Cell
	Rearm     []DefenseRearm
}

// DefenseRearmTurrets measures the tier's turrets. The rearm's pawn is an
// available colonist whose Hauling work (the native refuel work giver's
// type) is not disabled, preferring one with it enabled at the highest
// priority; the order is a forced one, so the game's own auto-refuel
// setting and threshold do not gate it. Under a solar flare (blackout)
// every turret is dark for the outage and none is a power deficit: the
// tier is absent, not unserviced (#408); an empty barrel is still rearmed
// so the line is whole when the flare ends.
func DefenseRearmTurrets(turrets []DefenseTurretFacts, workers []WorkPawn, stock domain.Fact[map[Resource]int64], blackout bool) DefenseTurretUpkeep {
	var out DefenseTurretUpkeep
	stocked, stockKnown := stock.Value()
	pawn, pawnOK := defenseRearmPawn(workers)
	for _, t := range turrets {
		if on, known := t.Powered.Value(); known && !on && !blackout {
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
			continue
		}
		if pawnOK && len(out.Rearm) == 0 && t.ID != "" {
			out.Rearm = append(out.Rearm, DefenseRearm{Turret: t.ID, Cell: t.Cell, Pawn: pawn, Fuel: fuel})
		}
	}
	return out
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
