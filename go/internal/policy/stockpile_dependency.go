package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// StockpileDependencies are the structural edges a missing store puts on the
// haul goals: while the review proposes creating the general or the food
// stockpile, MaintainStorage and SecureSupplies have nowhere to carry their
// items, so MaintainStockpiles takes the next slot ahead of them
// (ResolveDonations). Without the edge the haul goals' deficit, high because
// nothing is stored, outranked the small stockpile deficit on every review
// and the colony never got its stores.
func StockpileDependencies(review domain.Fact[StockpileReview]) []DevelopmentDependency {
	v, known := review.Value()
	if !known || !v.Known || !v.Active {
		return nil
	}
	for _, e := range v.Edits {
		if e.Kind == StockpileCreate && (e.Role == domain.GeneralRole || e.Role == domain.FoodRole) {
			return []DevelopmentDependency{
				{Dependent: MaintainStorage, Prerequisite: MaintainStockpiles},
				{Dependent: SecureSupplies, Prerequisite: MaintainStockpiles},
			}
		}
	}
	return nil
}
