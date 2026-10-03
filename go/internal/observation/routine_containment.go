package observation

import (
	"iter"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// containmentPlanning is the containment cell's inputs (#1741) from the
// frame: the entities' demand from the pawn table, the standing holders from
// the building table and the prediction's def inputs from the catalog. All
// stay unknown without Anomaly.
func containmentPlanning(frame bridge.RoutineFrame, definitions []PlanningDefinition) policy.ContainmentPlanning {
	out := policy.ContainmentPlanning{Demand: domain.Unknown[policy.ContainmentDemand](), Holders: domain.Unknown[[]policy.BuiltHolder](), Defs: domain.Unknown[policy.ContainmentDefs](), Entities: domain.Unknown[[]policy.CapturableEntity]()}
	if frame.Catalog == nil || frame.Catalog.Anomaly == nil {
		return out
	}
	out.Demand = containmentDemand(frame.Tables.Pawns.Values())
	out.Holders = builtHolders(frame.Tables.Buildings.Values())
	out.Entities = capturableEntities(frame.Tables.Pawns.Values())
	definition := func(name string) (PlanningDefinition, string) {
		for _, d := range definitions {
			if d.Name == name {
				return d, ""
			}
		}
		return PlanningDefinition{}, name + " is not among the planning definitions"
	}
	wall, reason := definition(policy.ShellWallDefinition)
	if reason == "" {
		var door PlanningDefinition
		if door, reason = definition(policy.ShellDoorDefinition); reason == "" {
			// The shell is one stuff: the wall's cheapest, which the door must
			// be makeable from too (an unstuffed pair has no stuff).
			stuff, shared := SharedStuff(wall, door)
			if !shared {
				reason = "the shell's wall and door are built from different stuff"
			} else if defs, err := frame.Catalog.ContainmentDefs(policy.ShellWallDefinition, stuff, policy.ShellDoorDefinition, stuff); err != nil {
				reason = err.Error()
			} else {
				out.Defs = domain.Known(defs)
			}
		}
	}
	out.DefsReason = reason
	return out
}

// containmentDemand counts the living entities the game lets the colony
// capture that no platform holds, and the strength the most demanding needs.
// A pawn row whose entity, held or strength fact is unread leaves the demand
// unknown; a pawn that is no entity, or an entity with no holding-platform
// target, asks nothing.
func containmentDemand(rows iter.Seq[*o.PawnState]) domain.Fact[policy.ContainmentDemand] {
	var demand policy.ContainmentDemand
	for row := range rows {
		a, ok := bridge.PawnAnomaly(row.Anomaly).Value()
		if !ok {
			continue
		}
		entity, known := a.Entity.Value()
		if !known {
			return domain.Unknown[policy.ContainmentDemand]()
		}
		if !entity || row.Dead == nil || row.GetDead() {
			if row.Dead == nil {
				return domain.Unknown[policy.ContainmentDemand]()
			}
			continue
		}
		held, known := a.Held.Value()
		if !known {
			return domain.Unknown[policy.ContainmentDemand]()
		}
		if held == nil {
			continue
		}
		isHeld, hk := held.Held.Value()
		capturable, ck := held.CanBeCaptured.Value()
		strength, sk := a.MinContainmentStrength.Value()
		if !hk || !ck || !sk {
			return domain.Unknown[policy.ContainmentDemand]()
		}
		if isHeld || !capturable {
			continue
		}
		demand.Entities++
		demand.Required = max(demand.Required, strength)
	}
	return domain.Known(demand)
}

// builtHolders are the standing holding platforms' native strength and
// availability; a holder whose facts are unread makes the list unknown.
func builtHolders(rows iter.Seq[*o.BuildingState]) domain.Fact[[]policy.BuiltHolder] {
	holders := []policy.BuiltHolder{}
	for row := range rows {
		b, ok := bridge.BuildingAnomaly(row).Value()
		if !ok {
			continue
		}
		holder, known := b.Holder.Value()
		if !known {
			return domain.Unknown[[]policy.BuiltHolder]()
		}
		if holder == nil {
			continue
		}
		strength, sk := holder.ContainmentStrength.Value()
		available, ak := holder.Available.Value()
		if !sk || !ak {
			return domain.Unknown[[]policy.BuiltHolder]()
		}
		holders = append(holders, policy.BuiltHolder{Strength: strength, Available: available})
	}
	return domain.Known(holders)
}
