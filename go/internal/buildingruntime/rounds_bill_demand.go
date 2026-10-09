package buildingruntime

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// openBills are the ledger's declared finite batches (an order stands from
// its declaration, placed or not; Forever and stock-target orders are not
// counted) with their catalog slot counts read per recipe: ResourceDemandOf
// turns them into ingredient demand. It is empty when the reviewer's source
// serves no definitions.
func (r *Rounder) openBills(ctx context.Context, snapshot domain.GenerationSnapshot) ([]policy.OpenBill, error) {
	source, ok := r.native.(observation.DefinitionSource)
	if !ok {
		return nil, nil
	}
	var bills []policy.OpenBill
	for _, order := range r.ledger.declaredBatches() {
		bills = append(bills, policy.OpenBill{Recipe: order.Recipe, Count: order.Target, Filter: order.Ingredients})
	}
	if len(bills) == 0 {
		return nil, nil
	}
	catalog, err := source.DefinitionCatalog(ctx, boundary.Identity(snapshot))
	if err != nil {
		return nil, err
	}
	for i := range bills {
		// A recipe the catalog cannot describe leaves its slots unknown.
		if bills[i].Slots, err = catalog.RecipeIngredients(bills[i].Recipe); err != nil {
			bills[i].Slots = domain.Unknown[[][]policy.Amount]()
		}
	}
	return bills, nil
}
