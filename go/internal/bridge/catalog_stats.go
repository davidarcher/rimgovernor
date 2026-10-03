package bridge

import (
	"math"

	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// decodeStatTable indexes the stat table (#1759) by (def, stuff) then stat
// name. A row must name a known def, and a known stuff when it has one, with
// one finite value per distinct stat of the shared table; an absent table
// stays nil.
func decodeStatTable(v *o.DefStatTable, things map[string]*d.ThingDef) (map[defStuff]*statRow, error) {
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
	out := make(map[defStuff]*statRow, len(v.Rows))
	for _, row := range v.Rows {
		key := defStuff{row.GetDefName(), row.GetStuffName()}
		if things[key.def] == nil || (key.stuff != "" && things[key.stuff] == nil) {
			return nil, contract("catalog stat row for unknown def %s or stuff %q", key.def, key.stuff)
		}
		if out[key] != nil {
			return nil, contract("duplicate catalog stat row for def %s with stuff %q", key.def, key.stuff)
		}
		if len(row.Stat) != len(row.Value) {
			return nil, contract("catalog stat row %s/%q has %d stats and %d values", key.def, key.stuff, len(row.Stat), len(row.Value))
		}
		values := make(map[string]float32, len(row.Stat))
		for i, index := range row.Stat {
			if index < 0 || int(index) >= len(v.Stats) {
				return nil, contract("catalog stat row %s/%q stat index %d is outside the table", key.def, key.stuff, index)
			}
			name := v.Stats[index]
			if _, dup := values[name]; dup {
				return nil, contract("catalog stat row %s/%q repeats stat %s", key.def, key.stuff, name)
			}
			if f := float64(row.Value[i]); math.IsNaN(f) || math.IsInf(f, 0) {
				return nil, contract("catalog stat %s of %s/%q is %v", name, key.def, key.stuff, f)
			}
			values[name] = row.Value[i]
		}
		for _, cost := range row.Costs {
			if validID(cost.GetDefName()) != nil || things[cost.GetDefName()] == nil || cost.GetUnits() <= 0 {
				return nil, contract("catalog cost of %s/%q names %q x %d", key.def, key.stuff, cost.GetDefName(), cost.GetUnits())
			}
		}
		out[key] = &statRow{values: values, costs: row.Costs}
	}
	return out, nil
}

// statRow is one (def, stuff) row: the game's stat values by stat name and
// its adjusted cost list.
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
	row, ok := catalog.statValues[defStuff{def, stuff}]
	if !ok {
		return nil, contract("no stat values for def %s with stuff %q", def, stuff)
	}
	return row.costs, nil
}
