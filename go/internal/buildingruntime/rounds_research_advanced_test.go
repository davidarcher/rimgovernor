package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// The advanced lab step builds the high-tech bench first, then the analyzer,
// each only once the catalog lists it buildable, and nothing once both stand
// or the catalog has neither.
func TestAdvancedLabOwedWalksBenchThenAnalyzer(t *testing.T) {
	t.Parallel()
	const bench, analyzer = "AdvBench", "Analyzer"
	standing := func(defs ...string) []policy.SiteCell {
		var cells []policy.SiteCell
		for i, d := range defs {
			cells = append(cells, policy.SiteCell{Cell: domain.Cell{X: int32(i)}, Things: []policy.Thing{{ID: uint64(i + 1), Def: d, Category: policy.ThingBuilding, Faction: policy.FactionPlayer, Flags: policy.FlagEdifice}}})
		}
		return cells
	}
	available := func(names ...string) []observation.PlanningDefinition {
		var out []observation.PlanningDefinition
		for _, n := range names {
			out = append(out, observation.PlanningDefinition{Name: n, Available: domain.Known(true)})
		}
		return out
	}
	projection := func(cells []policy.SiteCell, defs []observation.PlanningDefinition) observation.ColonyProjection {
		var p observation.ColonyProjection
		p.Shapes.Furniture.AdvancedLab = bench
		p.Shapes.Furniture.Analyzer = policy.FacilityLink{Def: analyzer}
		p.Cells, p.Definitions = cells, defs
		return p
	}
	for _, tc := range []struct {
		name string
		p    observation.ColonyProjection
		want string
	}{
		{"bench first", projection(nil, available(bench, analyzer)), bench},
		{"bench not buildable", projection(nil, []observation.PlanningDefinition{{Name: bench, Available: domain.Known(false)}}), ""},
		{"analyzer after bench", projection(standing(bench), available(bench, analyzer)), analyzer},
		{"analyzer not researched", projection(standing(bench), []observation.PlanningDefinition{{Name: analyzer, Available: domain.Known(false)}}), ""},
		{"both stand", projection(standing(bench, analyzer), available(bench, analyzer)), ""},
		{"no catalog rows", projection(nil, nil), ""},
		{"catalog without them", observation.ColonyProjection{}, ""},
	} {
		got, ok := advancedLabOwed(tc.p)
		if got != tc.want || ok != (tc.want != "") {
			t.Errorf("%s: owed %q %v, want %q", tc.name, got, ok, tc.want)
		}
	}
	// An owed buildable high-tech building also wants the planned lab.
	roles := coreRoomsWanted(projection(nil, available(bench)))
	found := false
	for _, role := range roles {
		found = found || role == policy.PlannedLab
	}
	if !found {
		t.Errorf("an owed advanced bench did not want the lab: %v", roles)
	}
}
