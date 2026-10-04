package policy

import "slices"

// UnsafeLoot lists the things a census reports unsafe to haul, sorted.
func UnsafeLoot(rows []LootItem) []string {
	var out []string
	for _, row := range rows {
		if row.SafetyKnown && !row.SafeToHaul {
			out = append(out, row.Supply.Thing)
		}
	}
	slices.Sort(out)
	return out
}
