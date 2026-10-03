package bridge

import "github.com/davidarcher/RimGovernor/go/internal/policy"

// The StatDefs a floor is judged by.
const (
	StatCleanliness  = "Cleanliness"
	StatBeauty       = "Beauty"
	StatFlammability = "Flammability"
)

// FloorTerrains prices every TerrainDef row (#1726): the table a flooring
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

// FloorTerrain is what a TerrainDef says about the floor it lays (#1733): its
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
	return policy.FloorTerrain{Cleanliness: float64(stats[0]), Beauty: float64(stats[1]), Flammability: float64(stats[2]), PathCost: row.GetPathCost(), Natural: row.GetNatural()}, nil
}
