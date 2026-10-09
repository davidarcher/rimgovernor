package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

type wallCatalogSource struct {
	RoundsDefenseLayoutSource
	catalog *bridge.DefinitionCatalog
}

func (s wallCatalogSource) DefinitionCatalog(context.Context, *c.Identity) (*bridge.DefinitionCatalog, error) {
	return s.catalog, nil
}

// Terrain whose affordances lack the wall's need reads WallSupport false; a
// wooden wall takes Light from its stuff, and an unlisted terrain stays
// unknown.
func TestDefenseMarkWallSupportFromTerrainAffordances(t *testing.T) {
	t.Parallel()
	catalog := &bridge.DefinitionCatalog{
		ThingDefs: map[string]*d.ThingDef{
			"Wall":      {DefName: "Wall", UseStuffTerrainAffordance: true, TerrainAffordanceNeeded: "Heavy"},
			"WoodLog":   {DefName: "WoodLog", TerrainAffordanceNeeded: "Light"},
			"TrapSpike": {DefName: "TrapSpike", UseStuffTerrainAffordance: true},
		},
		TerrainDefs: map[string]*d.TerrainDef{
			"Soil":         {DefName: "Soil", Affordances: []string{"Light", "Heavy"}},
			"WaterShallow": {DefName: "WaterShallow", Affordances: []string{"Heavy"}},
		},
	}
	planner := &RoundsDefenseLayoutPlanner{native: wallCatalogSource{catalog: catalog}}
	request := policy.DefenseRequest{Definitions: defenseDefinitions}
	request.Definitions.Trap, request.Definitions.TrapStuff = "TrapSpike", "WoodLog"
	var site []bridge.DefenseCell
	for i, terrain := range []string{"Soil", "WaterShallow", "Mystery"} {
		cell := bridge.DefenseCell{Cell: domain.Cell{X: int32(i), Z: 0}, Terrain: terrain}
		site = append(site, cell)
		request.Cells = append(request.Cells, defenseCellFacts(cell))
	}
	if err := planner.markWallSupport(context.Background(), nil, &request, site); err != nil {
		t.Fatal(err)
	}
	want := []domain.Fact[bool]{domain.Known(true), domain.Known(false), {}}
	for i, cell := range request.Cells {
		if cell.TrapSupport != want[i] {
			t.Errorf("%s: TrapSupport %+v, want %+v", site[i].Terrain, cell.TrapSupport, want[i])
		}
		if cell.WallSupport != want[i] {
			t.Errorf("%s: WallSupport %+v, want %+v", site[i].Terrain, cell.WallSupport, want[i])
		}
	}
}
