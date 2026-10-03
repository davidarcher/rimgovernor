package buildingruntime

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// The armory:<roomID> and wardrobe:<roomID> stockpiles (#1774, sited by the
// storage planner) keep fixed settings: the catalog's armor split decides
// their filters (policy.GearFilters), so while the catalog names no armor
// their zones are left as they are. The 2x2 apparel and weapons zones they
// replaced retire: any still claimed is deleted and its gear rehomes to the
// armory, the wardrobe or the general store.
func init() {
	gearStore := func(armory bool) StockpileRoleSource {
		return func(in StockpileRoleInput, _ string) (policy.StockpileRoleState, bool) {
			if len(in.Projection.Facts.Items.Armor) == 0 {
				return policy.StockpileRoleState{}, false
			}
			armoryFilter, wardrobeFilter, err := policy.GearFilters(in.Projection.Facts.Items.Armor)
			if err != nil {
				return policy.StockpileRoleState{}, false
			}
			filter := wardrobeFilter
			if armory {
				filter = armoryFilter
			}
			return policy.StockpileRoleState{Filter: filter, Priority: domain.PreferredPriority, Fixed: true}, true
		}
	}
	RegisterStockpileRole("armory", gearStore(true))
	RegisterStockpileRole("wardrobe", gearStore(false))
	retired := func(StockpileRoleInput, string) (policy.StockpileRoleState, bool) {
		return policy.StockpileRoleState{Retired: true}, true
	}
	RegisterStockpileRole(domain.ApparelRole, retired)
	RegisterStockpileRole(domain.WeaponsRole, retired)
}
