package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// coreWithout drops cells from the core candidates.
func coreWithout(zones []LayoutZone, cells map[domain.Cell]bool) []LayoutZone {
	if len(cells) == 0 {
		return zones
	}
	out := make([]LayoutZone, 0, len(zones))
	for _, z := range zones {
		if z.Kind != ZoneCore {
			out = append(out, z)
			continue
		}
		var runs []RowRun
		for _, r := range z.Runs {
			start := r.X
			for x := r.X; x <= r.X+r.Length; x++ {
				if x < r.X+r.Length && !cells[domain.Cell{X: x, Z: r.Z}] {
					continue
				}
				if x > start {
					runs = append(runs, RowRun{Z: r.Z, X: start, Length: x - start})
				}
				start = x + 1
			}
		}
		z.Runs = runs
		out = append(out, z)
	}
	return out
}
