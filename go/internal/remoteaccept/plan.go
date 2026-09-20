package remoteaccept

import (
	"fmt"
	"slices"
	"strings"
)

const DependencyAlgorithm = "dependency-round-robin-v2"

// PlanShards keeps generated saves in the worker profile that consumes them.
// Inputs remain sorted for the evidence contract; each generator and consumer
// form one scheduling group, ordered generator first.
func PlanShards(names []string, limit int, algorithm string) ([]PlannedShard, error) {
	if limit < 1 || len(names) == 0 || !slices.IsSorted(names) {
		return nil, fmt.Errorf("empty or unsorted selection or invalid shard limit")
	}
	if algorithm != "sorted-round-robin-v1" && algorithm != DependencyAlgorithm {
		return nil, fmt.Errorf("unknown shard algorithm %q", algorithm)
	}
	consumers := map[string]string{}
	for i, name := range names {
		if i > 0 && name == names[i-1] {
			return nil, fmt.Errorf("duplicate selected case %s", name)
		}
		if suffix, ok := strings.CutPrefix(name, "sustained/matrix-"); ok && algorithm == DependencyAlgorithm {
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
	group := 0
	for _, name := range names {
		if algorithm == DependencyAlgorithm && strings.HasPrefix(name, "sustained/matrix-") {
			continue
		}
		shard := &shards[group%count]
		shard.Cases = append(shard.Cases, name)
		if consumer, ok := consumers[name]; ok {
			shard.Cases = append(shard.Cases, consumer)
		}
		group++
	}
	return shards, nil
}
