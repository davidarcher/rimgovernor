package store

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// lootReachFilter narrows the loot census's Allow candidates to what may be
// released (FilterLootRelease: spawner-forbidden stacks and stacks in danger
// stay forbidden), then to the resource reach stage and unmet demand (#522),
// before the safety review (#336) acts on it. The reach reads the same
// derived extent the routines API reports.
func lootReachFilter(request RoutineReviewRequest) (domain.Fact[[]policy.LootItem], []policy.LootHold, error) {
	f := request.Facts
	if _, known := f.EventLoot.Value(); !known {
		return f.EventLoot, nil, nil
	}
	released, held := policy.FilterLootRelease(f.EventLoot, f.DangerSeeds)
	r, err := policy.SalvageContext(request.Policy, f)
	if err != nil {
		return domain.Unknown[[]policy.LootItem](), nil, err
	}
	census, reach, err := policy.FilterLootReach(released, r)
	if err != nil {
		return domain.Unknown[[]policy.LootItem](), nil, err
	}
	held = append(held, reach...)
	sort.Slice(held, func(i, j int) bool { return held[i].Thing < held[j].Thing })
	return census, held, nil
}
