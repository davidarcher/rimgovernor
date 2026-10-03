package bridge

import (
	"cmp"
	"math"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

// StatComfort is the StatDef a chair's comfort is read from.
const StatComfort = "Comfort"

// DiningFurniture is the furniture the dining and recreation planners place,
// chosen by rules over the catalog rows and never by name:
//   - the chair is the buildable ThingDef the game lets a pawn sit on
//     (BuildingProperties.isSittable) with the most Comfort per unit of cost,
//     taken at its best stuff (the market value of its adjusted costs); ties
//     go to the name;
//   - the table is the buildable ThingDef whose surfaceType is Eat with the
//     smallest footprint, then the cheapest (at its cheapest stuff), then the
//     name;
//   - the pin is the recreation foothold (RecreationFoothold), whose lane is
//     the far end of its watch stand distance range plus the pin's own cell,
//     one cell when the building is not watched from a distance.
//
// A catalog with no chair, no table or no foothold is an error, and so is a
// shape the dining template cannot lay out (policy.DiningFurniture.Validate).
func (catalog *DefinitionCatalog) DiningFurniture() (policy.DiningFurniture, error) {
	if catalog == nil {
		return policy.DiningFurniture{}, contract("no definition catalog")
	}
	chair, err := catalog.DiningChair()
	if err != nil {
		return policy.DiningFurniture{}, err
	}
	table, err := catalog.DiningTable()
	if err != nil {
		return policy.DiningFurniture{}, err
	}
	foothold, err := catalog.RecreationFoothold()
	if err != nil {
		return policy.DiningFurniture{}, err
	}
	row := catalog.ThingDefs[foothold]
	lane := int32(1)
	if stand := row.GetBuilding().GetWatchBuildingStandDistanceRange(); stand != nil {
		lane = stand.GetMax() + 1
	}
	out := policy.DiningFurniture{Chair: catalog.pieceShape(chair), Table: catalog.pieceShape(table), Pin: catalog.pieceShape(foothold), Lane: lane}
	if err := out.Validate(); err != nil {
		return policy.DiningFurniture{}, contract("%v", err)
	}
	return out, nil
}

// pieceShape is a def's plannable footprint: its size at rotation North.
func (catalog *DefinitionCatalog) pieceShape(def string) policy.InteriorPieceDef {
	size := catalog.ThingDefs[def].GetSize()
	return policy.InteriorPieceDef{Def: def, Size: domain.Cell{X: size.GetX(), Z: size.GetZ()}}
}

// DiningChair is the sittable building with the most comfort per cost.
func (catalog *DefinitionCatalog) DiningChair() (string, error) {
	best, bestRatio := "", math.Inf(-1)
	for name, row := range catalog.ThingDefs {
		if !Buildable(row) || !row.GetBuilding().GetIsSittable() {
			continue
		}
		stuffs, err := catalog.AllowedStuffs(name)
		if err != nil {
			return "", err
		}
		if len(stuffs) == 0 {
			stuffs = []string{""}
		}
		ratio := math.Inf(-1)
		for _, stuff := range stuffs {
			comfort, shown, err := catalog.ShownStatValue(name, stuff, StatComfort)
			if err != nil {
				return "", err
			}
			if !shown {
				continue
			}
			cost, err := catalog.costValue(name, stuff)
			if err != nil {
				return "", err
			}
			if cost <= 0 {
				return "", contract("sittable building %s costs nothing with stuff %q", name, stuff)
			}
			ratio = math.Max(ratio, float64(comfort)/cost)
		}
		if ratio > bestRatio || ratio == bestRatio && name < best {
			best, bestRatio = name, ratio
		}
	}
	if best == "" || math.IsInf(bestRatio, -1) {
		return "", contract("catalog has no buildable sittable def with a comfort stat")
	}
	return best, nil
}

// DiningTable is the buildable eating surface with the smallest footprint,
// then the lowest cost.
func (catalog *DefinitionCatalog) DiningTable() (string, error) {
	type candidate struct {
		name string
		area int32
		cost float64
	}
	var found []candidate
	for name, row := range catalog.ThingDefs {
		if !Buildable(row) || row.GetSurfaceType() != d.SurfaceType_SURFACE_TYPE_EAT {
			continue
		}
		cost, err := catalog.CheapestCostValue(name)
		if err != nil {
			return "", err
		}
		found = append(found, candidate{name, row.GetSize().GetX() * row.GetSize().GetZ(), cost})
	}
	if len(found) == 0 {
		return "", contract("catalog has no buildable def with an eating surface")
	}
	slices.SortFunc(found, func(a, b candidate) int {
		return cmp.Or(cmp.Compare(a.area, b.area), cmp.Compare(a.cost, b.cost), cmp.Compare(a.name, b.name))
	})
	return found[0].name, nil
}
