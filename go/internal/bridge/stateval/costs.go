package stateval

import (
	"fmt"
	"math"
	"slices"

	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

// Cost is one material of an adjusted cost list.
type Cost struct {
	Def   string
	Units int64
}

// StuffCanMake is StuffProperties.CanMake(def): the stuff's categories share
// one with the def's stuffCategories.
func StuffCanMake(stuff, def *d.ThingDef) bool {
	for _, category := range def.GetStuffCategories() {
		if slices.Contains(stuff.GetStuffProps().GetCategories(), category) {
			return true
		}
	}
	return false
}

// AllowedStuffsFor is GenStuff.AllowedStuffsFor(def) with no tech level cap
// and no stuff-generation filter: every stuff that can make the def, sorted by
// defName; empty for a def not made from stuff.
func (e *Evaluator) AllowedStuffsFor(def string) ([]string, error) {
	row := e.catalog.ThingDef(def)
	if row == nil {
		return nil, fmt.Errorf("catalog has no thing def %s", def)
	}
	if len(row.GetStuffCategories()) == 0 {
		return nil, nil
	}
	var out []string
	for name, stuff := range e.catalog.AllThingDefs() {
		if stuff.GetStuffProps() != nil && StuffCanMake(stuff, row) {
			out = append(out, name)
		}
	}
	slices.Sort(out)
	return out, nil
}

// CostListAdjusted is BuildableDef.CostListAdjusted(stuff, errorOnNullStuff:
// false) of a ThingDef, stuff empty for none: the cost list for the difficulty
// with the stuff's units (costStuffCount over its volume, at least 1) added to
// the entry for the stuff or appended. A def made from stuff with none given,
// and stuff given for a def not made from it, are errors (the game logs one and
// reads a null stuff).
func (e *Evaluator) CostListAdjusted(def, stuff string) ([]Cost, error) {
	row := e.catalog.ThingDef(def)
	if row == nil {
		return nil, fmt.Errorf("catalog has no thing def %s", def)
	}
	return e.adjustedCosts(false, def, stuff, len(row.GetStuffCategories()) > 0)
}

// TerrainCostListAdjusted is CostListAdjusted(null) of a TerrainDef.
func (e *Evaluator) TerrainCostListAdjusted(terrain string) ([]Cost, error) {
	row := e.catalog.TerrainDef(terrain)
	if row == nil {
		return nil, fmt.Errorf("catalog has no terrain def %s", terrain)
	}
	return e.adjustedCosts(true, terrain, "", len(row.GetStuffCategories()) > 0)
}

func (e *Evaluator) adjustedCosts(terrain bool, def, stuff string, madeFromStuff bool) ([]Cost, error) {
	costList, costStuff, err := e.costs(terrain, def)
	if err != nil {
		return nil, err
	}
	var num int64
	switch {
	case madeFromStuff && stuff == "":
		return nil, fmt.Errorf("def %s is made from stuff: its adjusted cost list needs one", def)
	case madeFromStuff:
		row := e.catalog.ThingDef(stuff)
		if row == nil {
			return nil, fmt.Errorf("catalog has no stuff def %s", stuff)
		}
		volume := float32(1)
		if row.GetSmallVolume() {
			volume = smallVolumePerUnit
		}
		num = int64(math.RoundToEven(float64(float32(float32(costStuff) / volume))))
		num = max(num, 1)
	case stuff != "":
		return nil, fmt.Errorf("def %s is not made from stuff: it takes no stuff %s", def, stuff)
	}
	var out []Cost
	added := false
	for _, entry := range costList {
		cost := entry.GetValue()
		if stuff != "" && cost.GetThingDef() == stuff {
			out = append(out, Cost{Def: cost.GetThingDef(), Units: int64(cost.GetCount()) + num})
			added = true
		} else {
			out = append(out, Cost{Def: cost.GetThingDef(), Units: int64(cost.GetCount())})
		}
	}
	if !added && num > 0 {
		out = append(out, Cost{Def: stuff, Units: num})
	}
	return out, nil
}
