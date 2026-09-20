package store

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// lootReachFilter narrows the loot census's Allow candidates to the resource
// reach stage and unmet demand (#522) before the safety review (#336) acts on
// it. The reach reads the same derived extent the routines API reports.
func lootReachFilter(request RoutineReviewRequest) (domain.Fact[[]policy.LootItem], []policy.LootHold, error) {
	f := request.Facts
	if _, known := f.EventLoot.Value(); !known {
		return f.EventLoot, nil, nil
	}
	r, err := policy.SalvageContext(request.Policy, f)
	if err != nil {
		return domain.Unknown[[]policy.LootItem](), nil, err
	}
	return policy.FilterLootReach(f.EventLoot, r)
}
