package observation

import (
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// monumentPieceOptions reads a monument piece's stuffs and build options from
// the catalog rows, the way MonumentMarker.AllowedStuffsFor and the sketch
// buildable do: a def made from stuff with none chosen offers every stuff
// that can make it, one chosen offers only it, and a def not made from stuff
// has the one option with no stuff. The piece names a thing or a terrain def;
// a name both tables hold, or neither does, is an error.
func monumentPieceOptions(catalog *bridge.DefinitionCatalog, def, stuff string) (allowed []string, options []policy.QuestMonumentBuildOption, err error) {
	thing, terrain := catalog.ThingDef(def), bridge.DefRow[*d.TerrainDef](catalog, def)
	if (thing == nil) == (terrain == nil) {
		return nil, nil, fmt.Errorf("monument piece %s is not exactly one of a thing def and a terrain def", def)
	}
	madeFromStuff := len(thing.GetStuffCategories()) > 0
	if thing == nil {
		madeFromStuff = len(terrain.GetStuffCategories()) > 0
	}
	stuffs := []string{""}
	if madeFromStuff {
		stuffs = []string{stuff}
		if stuff == "" {
			if thing == nil {
				return nil, nil, fmt.Errorf("monument terrain piece %s is made from stuff with none chosen", def)
			}
			if stuffs, err = catalog.AllowedStuffs(def); err != nil {
				return nil, nil, err
			}
			allowed = append(allowed, stuffs...)
		}
	}
	for _, s := range stuffs {
		option := policy.QuestMonumentBuildOption{Stuff: s}
		var costs []*o.Quantity
		if thing != nil {
			work, err := catalog.StatValue(def, s, bridge.StatWorkToBuild)
			if err != nil {
				return nil, nil, err
			}
			option.Work = float64(work)
			if costs, err = catalog.AdjustedCosts(def, s); err != nil {
				return nil, nil, err
			}
		} else {
			work, err := catalog.TerrainWorkToBuild(def)
			if err != nil {
				return nil, nil, err
			}
			option.Work = float64(work)
			if costs, err = catalog.TerrainAdjustedCosts(def); err != nil {
				return nil, nil, err
			}
		}
		for _, cost := range costs {
			option.Costs = append(option.Costs, policy.Amount{Resource: policy.Resource(cost.GetDefName()), Count: cost.GetUnits()})
		}
		options = append(options, option)
	}
	return allowed, options, nil
}
