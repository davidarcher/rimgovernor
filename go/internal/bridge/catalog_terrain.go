package bridge

import (
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// The StatDefs a floor is judged by.
const (
	StatCleanliness  = "Cleanliness"
	StatBeauty       = "Beauty"
	StatFlammability = "Flammability"
)

// FloorTerrains prices every TerrainDef row: the table a flooring
// census scores its cells against, built once per catalog. The caller must not
// modify the map. A terrain the stat table does not show a floor stat for fails
// the whole table.
func (catalog *DefinitionCatalog) FloorTerrains() (map[string]policy.FloorTerrain, error) {
	if catalog == nil {
		return nil, contract("no definition catalog")
	}
	catalog.floorOnce.Do(func() {
		out := make(map[string]policy.FloorTerrain, len(catalog.TerrainDefs))
		for name := range catalog.TerrainDefs {
			terrain, err := catalog.FloorTerrain(name)
			if err != nil {
				catalog.floorErr = err
				return
			}
			out[name] = terrain
		}
		catalog.floorTerrains = out
	})
	return catalog.floorTerrains, catalog.floorErr
}

// FloorTerrain is what a TerrainDef says about the floor it lays: its
// cleanliness, beauty and flammability stat values from the stat table, and its
// path cost and natural flag from its def row. A terrain without a row or
// without one of the stats is a contract error, never a default.
func (catalog *DefinitionCatalog) FloorTerrain(name string) (policy.FloorTerrain, error) {
	if catalog == nil {
		return policy.FloorTerrain{}, contract("no definition catalog")
	}
	row := catalog.TerrainDefs[name]
	if row == nil {
		return policy.FloorTerrain{}, contract("catalog has no def row for terrain %s", name)
	}
	var stats [3]float32
	for i, stat := range []string{StatCleanliness, StatBeauty, StatFlammability} {
		value, err := catalog.TerrainStatValue(name, stat)
		if err != nil {
			return policy.FloorTerrain{}, err
		}
		stats[i] = value
	}
	return policy.FloorTerrain{Cleanliness: float64(stats[0]), Beauty: float64(stats[1]), Flammability: float64(stats[2]), PathCost: row.GetPathCost(), Fertility: float64(row.GetFertility()), Natural: row.GetNatural(), Tags: slices.Clone(row.GetTags())}, nil
}

// TerrainsWithTags lists, sorted, every TerrainDef carrying any of tags: the
// floors that satisfy a TerrainWithTags requirement.
func (catalog *DefinitionCatalog) TerrainsWithTags(tags []string) []string {
	if catalog == nil {
		return nil
	}
	var out []string
	for name, row := range catalog.TerrainDefs {
		for _, tag := range row.GetTags() {
			if slices.Contains(tags, tag) {
				out = append(out, name)
				break
			}
		}
	}
	slices.Sort(out)
	return out
}
