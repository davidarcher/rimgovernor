package policy

import "math"

// GearStock is a bounded aggregate of usable apparel in native storage.
type GearStock struct {
	Definition, Stuff      Resource
	Quality, HPBand, Count int
}
type gearStockKey struct{ definition, stuff Resource }

// Stored options assigned by the ensemble planner already satisfied one gap.
// Remove those physical units before netting its remaining bill demand.
func unassignedGearStock(stock []GearStock, loadouts []GearLoadout) []GearStock {
	out := append([]GearStock(nil), stock...)
	for _, l := range loadouts {
		for _, g := range l.Gaps {
			if g.Source != GearStored || g.Gain <= GearGapThreshold(l.Role) {
				continue
			}
			band := min(9, int(math.Floor(g.Wanted.Condition*10)))
			for i := range out {
				row := &out[i]
				if row.Count > 0 && row.Definition == g.Wanted.Definition && row.Stuff == g.Wanted.Stuff && row.Quality == g.Wanted.Quality && row.HPBand == band {
					row.Count--
					break
				}
			}
		}
	}
	return out
}

func gearBatchIngredients(slots [][]Amount, count int) [][]Amount {
	out := make([][]Amount, len(slots))
	for i, slot := range slots {
		for _, a := range slot {
			out[i] = append(out[i], Amount{a.Resource, a.Count * int64(count)})
		}
	}
	return out
}
