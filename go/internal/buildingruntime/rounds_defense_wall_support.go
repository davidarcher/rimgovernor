package buildingruntime

import (
	"context"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

// defenseWallAffordance is the terrain affordance a wall of the given stuff
// needs: the stuff's own when the wall takes it from its stuff (a wooden wall
// needs Light, stone Heavy), else the wall def's. Empty when the catalog does
// not say.
func defenseWallAffordance(catalog *bridge.DefinitionCatalog, wall, stuff string) string {
	def := catalog.ThingDef(wall)
	if def == nil {
		return ""
	}
	if def.GetUseStuffTerrainAffordance() {
		if s := catalog.ThingDef(stuff); s != nil && s.GetTerrainAffordanceNeeded() != "" {
			return s.GetTerrainAffordanceNeeded()
		}
	}
	return def.GetTerrainAffordanceNeeded()
}

// markWallSupport sets each visible cell's WallSupport from the catalog's
// terrain affordances (#2119): the funnel then leaves cells the native
// preview would refuse ("requires terrain that supports: Light") unsited.
// A source without the catalog, a wall with no stated need or a terrain the
// catalog lacks leaves the fact unknown.
func (r *RoundsDefenseLayoutPlanner) markWallSupport(call context.Context, identity *c.Identity, request *policy.DefenseRequest, site []bridge.DefenseCell) error {
	catalog, err := pawnCatalog(call, r.native, identity)
	if err != nil || catalog == nil {
		return err
	}
	need := defenseWallAffordance(catalog, request.Definitions.Wall, request.Definitions.WallStuff)
	if need == "" {
		return nil
	}
	terrain := map[domain.Cell]string{}
	for _, cell := range site {
		if !cell.Fogged {
			terrain[cell.Cell] = cell.Terrain
		}
	}
	for i, cell := range request.Cells {
		if def := catalog.TerrainDef(terrain[cell.Cell]); def != nil {
			request.Cells[i].WallSupport = domain.Known(slices.Contains(def.GetAffordances(), need))
		}
	}
	return nil
}
