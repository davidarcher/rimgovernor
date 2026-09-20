package remoteaccept

import (
	"cmp"
	"fmt"
	"math"
	"slices"
	"strings"
)

const DependencyAlgorithm = "dependency-round-robin-v2"
const BudgetAlgorithm = "dependency-budget-lpt-v3"

// PlanShards keeps generated saves in the worker profile that consumes them.
// Inputs remain sorted for the evidence contract; each generator and consumer
// form one scheduling group, ordered generator first.
func PlanShards(names []string, limit int, algorithm string, budgets map[string]int64) ([]PlannedShard, error) {
	if limit < 1 || len(names) == 0 || !slices.IsSorted(names) {
		return nil, fmt.Errorf("empty or unsorted selection or invalid shard limit")
	}
	if algorithm != "sorted-round-robin-v1" && algorithm != DependencyAlgorithm && algorithm != BudgetAlgorithm {
		return nil, fmt.Errorf("unknown shard algorithm %q", algorithm)
	}
	consumers := map[string]string{}
	for i, name := range names {
		if i > 0 && name == names[i-1] {
			return nil, fmt.Errorf("duplicate selected case %s", name)
		}
		if suffix, ok := strings.CutPrefix(name, "sustained/matrix-"); ok && algorithm != "sorted-round-robin-v1" {
			generator := "tools/variantsavegen-" + suffix
			if !slices.Contains(names, generator) {
				return nil, fmt.Errorf("%s requires selected generator %s", name, generator)
			}
			consumers[generator] = name
		}
	}
	count := min(len(names)-len(consumers), limit)
	shards := make([]PlannedShard, count)
	for i := range shards {
		shards[i] = PlannedShard{ID: fmt.Sprintf("s%d", i+1), Cases: []string{}}
	}
	type schedulingGroup struct {
		names  []string
		budget int64
	}
	groups := []schedulingGroup{}
	var total int64
	for _, name := range names {
		if algorithm != "sorted-round-robin-v1" && strings.HasPrefix(name, "sustained/matrix-") {
			continue
		}
		g := schedulingGroup{names: []string{name}}
		if consumer, ok := consumers[name]; ok {
			g.names = append(g.names, consumer)
		}
		if algorithm == BudgetAlgorithm {
			for _, member := range g.names {
				budget := budgets[member]
				if budget <= 0 || budget > math.MaxInt64-total {
					return nil, fmt.Errorf("invalid or overflowing budget for %s", member)
				}
				total += budget
				g.budget += budget
			}
		}
		groups = append(groups, g)
	}
	if algorithm == BudgetAlgorithm {
		slices.SortFunc(groups, func(a, b schedulingGroup) int {
			if order := cmp.Compare(b.budget, a.budget); order != 0 {
				return order
			}
			return strings.Compare(a.names[0], b.names[0])
		})
	}
	loads := make([]int64, count)
	for i, g := range groups {
		target := i % count
		if algorithm == BudgetAlgorithm {
			target = 0
			for j := 1; j < count; j++ {
				if loads[j] < loads[target] {
					target = j
				}
			}
		}
		shard := &shards[target]
		shard.Cases = append(shard.Cases, g.names...)
		loads[target] += g.budget
	}
	return shards, nil
}
