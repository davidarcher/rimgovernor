package policy

import "sort"

const (
	// ColonistsPerExtraBench is how many colonists justify each bench of one
	// kind beyond the first: the cap on a kind is 1 + colonists/this.
	ColonistsPerExtraBench = 4
	// MaxBenchesPerKind bounds a kind however large the colony grows.
	MaxBenchesPerKind = 4
)

// BenchCap is the most benches of one kind the ladder stands for a colony of
// the given size.
func BenchCap(colonists int) int {
	return min(MaxBenchesPerKind, 1+max(colonists, 0)/ColonistsPerExtraBench)
}

// FurtherBenchKinds are the bench kinds that should get another bench: the
// dispatcher reports a shortfall only a new bench can close (no_bench,
// benches_exhausted) and the kind is below BenchCap. Other reasons (slots,
// ingredients, hauling) are not helped by a bench and never trigger.
func FurtherBenchKinds(unmet []UnmetThroughput, benches []GearBench, colonists int) []string {
	standing := map[string]int{}
	for _, b := range benches {
		standing[b.Def]++
	}
	var out []string
	for _, u := range unmet {
		if u.ShortPerDay <= 0 || u.Reason != UnmetNoBench && u.Reason != UnmetBenchesExhausted {
			continue
		}
		if standing[u.BenchKind] < BenchCap(colonists) {
			out = append(out, u.BenchKind)
		}
	}
	sort.Strings(out)
	return out
}
