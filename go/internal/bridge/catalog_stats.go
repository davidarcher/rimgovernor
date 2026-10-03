package bridge

import (
	"math"

	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// statTable is the decoded stat table (#1759): the rows of every ThingDef by
// (def, stuff) and of every TerrainDef by name, then stat name.
type statTable struct {
	things   map[defStuff]*statRow
	terrains map[string]*statRow
}

// decodeStatTable indexes the stat table by (def, stuff) then stat name. A
// thing row must name a known ThingDef, and a known stuff when it has one; a
// terrain row a known TerrainDef with no stuff; each carries one finite value
// per distinct stat of the shared table. An absent table stays nil.
func decodeStatTable(v *o.DefStatTable, things map[string]*d.ThingDef, terrains map[string]*d.TerrainDef) (*statTable, error) {
	if v == nil {
		return nil, nil
	}
	seen := make(map[string]bool, len(v.Stats))
	for _, name := range v.Stats {
		if validID(name) != nil || seen[name] {
			return nil, contract("invalid or repeated catalog stat %q", name)
		}
		seen[name] = true
	}
	out := &statTable{things: make(map[defStuff]*statRow, len(v.Rows)), terrains: make(map[string]*statRow, len(v.TerrainRows))}
	for _, row := range v.Rows {
		key := defStuff{row.GetDefName(), row.GetStuffName()}
		if things[key.def] == nil || (key.stuff != "" && things[key.stuff] == nil) {
			return nil, contract("catalog stat row for unknown def %s or stuff %q", key.def, key.stuff)
		}
		if out.things[key] != nil {
			return nil, contract("duplicate catalog stat row for def %s with stuff %q", key.def, key.stuff)
		}
		decoded, err := decodeStatRow(v.Stats, row)
		if err != nil {
			return nil, err
		}
		for _, cost := range row.Costs {
			if validID(cost.GetDefName()) != nil || things[cost.GetDefName()] == nil || cost.GetUnits() <= 0 {
				return nil, contract("catalog cost of %s/%q names %q x %d", key.def, key.stuff, cost.GetDefName(), cost.GetUnits())
			}
		}
		decoded.costs = row.Costs
		out.things[key] = decoded
	}
	for _, row := range v.TerrainRows {
		name := row.GetDefName()
		if terrains[name] == nil || row.GetStuffName() != "" || len(row.Costs) > 0 {
			return nil, contract("catalog terrain stat row for unknown terrain %s, or with stuff %q or costs", name, row.GetStuffName())
		}
		if out.terrains[name] != nil {
			return nil, contract("duplicate catalog stat row for terrain %s", name)
		}
		decoded, err := decodeStatRow(v.Stats, row)
		if err != nil {
			return nil, err
		}
		out.terrains[name] = decoded
	}
	return out, nil
}

// decodeStatRow is a row's finite values by stat name.
func decodeStatRow(stats []string, row *o.DefStatRow) (*statRow, error) {
	key := defStuff{row.GetDefName(), row.GetStuffName()}
	if len(row.Stat) != len(row.Value) {
		return nil, contract("catalog stat row %s/%q has %d stats and %d values", key.def, key.stuff, len(row.Stat), len(row.Value))
	}
	values := make(map[string]float32, len(row.Stat))
	for i, index := range row.Stat {
		if index < 0 || int(index) >= len(stats) {
			return nil, contract("catalog stat row %s/%q stat index %d is outside the table", key.def, key.stuff, index)
		}
		name := stats[index]
		if _, dup := values[name]; dup {
			return nil, contract("catalog stat row %s/%q repeats stat %s", key.def, key.stuff, name)
		}
		if f := float64(row.Value[i]); math.IsNaN(f) || math.IsInf(f, 0) {
			return nil, contract("catalog stat %s of %s/%q is %v", name, key.def, key.stuff, f)
		}
		values[name] = row.Value[i]
	}
	return &statRow{values: values}, nil
}

// statRow is one (def, stuff) row: the game's stat values by stat name and
// its adjusted cost list (things only).
type statRow struct {
	values map[string]float32
	costs  []*o.Quantity
}

// AdjustedCosts is the game's ThingDef.CostListAdjusted(stuff) of def, with
// stuff empty for a def not made from stuff; an empty list is a def with no
// cost. A catalog without the stat table or a pair without a row is an error.
func (catalog *DefinitionCatalog) AdjustedCosts(def, stuff string) ([]*o.Quantity, error) {
	if catalog == nil || catalog.statValues == nil {
		return nil, contract("definition catalog carries no stat values")
	}
	row, ok := catalog.statValues.things[defStuff{def, stuff}]
	if !ok {
		return nil, contract("no stat values for def %s with stuff %q", def, stuff)
	}
	return row.costs, nil
}

// TerrainStatValue is the game's GetStatValueAbstract(stat) of a TerrainDef.
// A catalog without the stat table, a terrain without a row and a stat the
// game does not show for the terrain are errors, never a default.
func (catalog *DefinitionCatalog) TerrainStatValue(terrain, stat string) (float32, error) {
	if catalog == nil || catalog.statValues == nil {
		return 0, contract("definition catalog carries no stat values")
	}
	row, ok := catalog.statValues.terrains[terrain]
	if !ok {
		return 0, contract("no stat values for terrain %s", terrain)
	}
	value, shown := row.values[stat]
	if !shown {
		return 0, contract("stat %s is not shown for terrain %s", stat, terrain)
	}
	return value, nil
}
