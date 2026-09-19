package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// orderedWorkCost ranks interruption evidence without excluding a pawn from
// Auto planning. Unknown provenance is costlier than a known idle/ordinary job.
// Active controller claims and native legality are checked independently.
func orderedWorkCost(forced domain.Fact[bool], queued ...domain.Fact[uint32]) int {
	cost := 0
	if v, known := forced.Value(); !known {
		cost = 2
	} else if v {
		cost = 1
	}
	for _, q := range queued {
		if v, known := q.Value(); !known {
			cost += 2
		} else if v > 0 {
			cost++
		}
	}
	return cost
}
