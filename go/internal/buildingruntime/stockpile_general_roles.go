package buildingruntime

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// The warehouse keeps the indoor-only filter at Low priority so the
// workstation stockpiles draw items first; the opening outdoor store keeps
// the non-perishables at Normal until the warehouse replaces it.
func init() {
	RegisterStockpileRole(domain.GeneralRole, fixedStockpileRole(domain.GeneralFilter(), domain.LowPriority))
	RegisterStockpileRole(domain.OpeningGeneralRole, fixedStockpileRole(domain.OpeningStoreFilter(), domain.NormalPriority))
	RegisterStockpileRole(domain.FoodRole, fixedStockpileRole(domain.FoodFilter(), domain.PreferredPriority))
	// The planner owns every zone: covered:<def> zones left by the removed
	// fallback are retired, so the stockpile review deletes them (#1778).
	RegisterStockpileRole("covered", func(StockpileRoleInput, string) (policy.StockpileRoleState, bool) {
		return policy.StockpileRoleState{Retired: true}, true
	})
}
