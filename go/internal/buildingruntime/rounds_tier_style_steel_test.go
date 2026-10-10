package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// Steel is for the heater, generators and hydroponics: with 450 steel and 300
// wood and no resupply the shell takes wood, not the steel reserve; a steel
// source lifts the guard, and a temperate stock is unchanged.
func TestShellStyleGuardsSteel(t *testing.T) {
	withSteel := func(p observation.ColonyProjection) observation.ColonyProjection {
		for i := range p.Definitions {
			if p.Definitions[i].Name == "Wall" {
				p.Definitions[i].StuffOptions = append(p.Definitions[i].StuffOptions, observation.StuffOption{Stuff: "Steel", Costs: []policy.Amount{{Resource: "Steel", Count: 5}}, Value: 1.8, Common: true,
					Stats: map[string]float64{bridge.StatMaxHitPoints: 300, bridge.StatFlammability: 0}})
			}
		}
		return p
	}
	ice := withSteel(shellProjection(map[policy.Resource]int64{"Steel": 450, "WoodLog": 300}))
	// Sea ice has no stone, so the catalog's stone walls are never an option there.
	for i := range ice.Definitions {
		kept := ice.Definitions[i].StuffOptions[:0:0]
		for _, o := range ice.Definitions[i].StuffOptions {
			if o.Stuff != "BlocksGranite" && o.Stuff != "BlocksSandstone" {
				kept = append(kept, o)
			}
		}
		ice.Definitions[i].StuffOptions = kept
	}
	ice.Acquisition = domain.Known([]policy.AcquisitionSource{})
	if got := shellStyle(ice).WallStuff; got != "WoodLog" {
		t.Fatal("sea ice walls spent", got)
	}
	ice.Acquisition = domain.Known([]policy.AcquisitionSource{{Resource: "Steel"}})
	if got := shellStyle(ice).WallStuff; got != "Steel" {
		t.Fatal("a resupplied steel is not guarded", got)
	}
	temperate := withSteel(shellProjection(map[policy.Resource]int64{"WoodLog": 1000, "BlocksGranite": 5 * policy.ShellWallBudget}))
	if got := shellStyle(temperate).WallStuff; got != "BlocksGranite" {
		t.Fatal("temperate choice changed", got)
	}
}
