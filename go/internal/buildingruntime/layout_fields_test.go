package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// Two planned fields at x 0-2 and 5-7 with a firebreak at x 3-4; the left
// field is planted. Fields stay on the planned zones and the break is
// floored in the flagstone the stock can pay for.
func TestFirebreakFloorsBesidePlantedField(t *testing.T) {
	plan := policy.LayoutPlan{Zones: []policy.LayoutZone{
		{Kind: policy.ZoneField, Runs: []policy.RowRun{{Z: 0, X: 0, Length: 3}}},
		{Kind: policy.ZoneField, Runs: []policy.RowRun{{Z: 0, X: 5, Length: 3}}},
	}}
	facts := observation.ColonyProjection{BuildTier: domain.Known(policy.BuildTierMasonry), LayoutPlan: domain.Known(plan)}
	for x := int32(0); x < 9; x++ {
		zoned := x < 3
		facts.Cells = append(facts.Cells, policy.SiteCell{Cell: domain.Cell{X: x}, Walkable: domain.Known(true), Occupied: domain.Known(false), Zone: domain.Known(zoned), Fertility: domain.Known(1.0)})
	}
	facts.Cells[4].Fertility = domain.Known(0.0) // already floored
	facts.Resources = domain.Known(map[policy.Resource]int64{"BlocksGranite": 50, "BlocksSlate": 5})
	for _, d := range []struct{ name, block string }{{"FlagstoneSlate", "BlocksSlate"}, {"FlagstoneGranite", "BlocksGranite"}} {
		facts.Definitions = append(facts.Definitions, observation.PlanningDefinition{Name: d.name, Available: domain.Known(true), Terrain: domain.Known(true), Costs: domain.Known([]policy.Amount{{Resource: policy.Resource(d.block), Count: 4}})})
	}
	candidate, ok := firebreakCandidate(facts)
	if !ok || len(candidate.Buildings) != 1 || candidate.Buildings[0] != (policy.SiteBuilding{Definition: "FlagstoneGranite", Cell: domain.Cell{X: 3}, Rotation: domain.North}) {
		t.Fatalf("candidate %+v %v", candidate, ok)
	}
	protected := layoutFieldProtected(facts, nil)
	if len(protected) != 3 || protected[0] != (domain.Cell{X: 3}) {
		t.Fatalf("protected %v", protected)
	}
	facts.BuildTier = domain.Known(policy.BuildTierCamp)
	if _, ok := firebreakCandidate(facts); ok || len(layoutFieldProtected(facts, nil)) != 0 {
		t.Fatal("camp uses the plan")
	}
}
