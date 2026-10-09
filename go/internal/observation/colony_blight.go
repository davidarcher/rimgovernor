package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// blightCensus lists the blighted plants on colony ground from the planning
// window's thing lists: a plant thing with blighted state on a
// growing zone or the home area. It is unknown (never "no blight") until the
// window and the zone section are both held. A cell the window does not hold
// (fogged) has no plant to see.
func blightCensus(p ColonyProjection) domain.Fact[[]policy.BlightedPlant] {
	if p.Region == (policy.Rectangle{}) || !p.Zones.Complete {
		return domain.Unknown[[]policy.BlightedPlant]()
	}
	farms := map[string]bool{}
	for _, farm := range p.Farms {
		farms[farm.ID] = true
	}
	plants := []policy.BlightedPlant{}
	for _, cell := range p.Cells {
		zone, _ := cell.ZoneID.Value()
		if !farms[zone] {
			zone = ""
		}
		if home, _ := cell.InHome.Value(); zone == "" && !home {
			continue
		}
		for _, thing := range cell.Things {
			if thing.Category != policy.ThingPlant || !thing.Plant.Blighted || thing.ID == 0 {
				continue
			}
			plants = append(plants, policy.BlightedPlant{ID: thing.LoadID(), Definition: thing.Def,
				Cell: cell.Cell, Zone: zone, Designated: thing.Has(policy.FlagDesignated), Eligible: true})
		}
	}
	return domain.Known(plants)
}
