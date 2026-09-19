package bridge

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

// ReadBillConsumption uses one bench's native recipe catalog after production
// was observed. Alternative ingredients remain unknown independently for steel
// and components. This forecast evidence never authorizes or completes work.
func (client *Client) ReadBillConsumption(ctx context.Context, identity *c.Identity, bill domain.ProductionBill, iterations uint32) (*domain.BillConsumption, error) {
	if err := ValidateIdentity(identity); err != nil {
		return nil, err
	}
	recipes, err := client.readGearRecipes(ctx, identity, bill.Bench())
	if err != nil {
		return nil, err
	}
	out := &domain.BillConsumption{}
	for _, recipe := range recipes {
		if recipe.Definition != bill.Recipe() {
			continue
		}
		if n, known := policy.RecipeResourceUse("Steel", recipe.Ingredients, bill.Ingredients(), iterations).Value(); known {
			out.Steel = &n
		}
		if n, known := policy.RecipeResourceUse("ComponentIndustrial", recipe.Ingredients, bill.Ingredients(), iterations).Value(); known {
			out.Components = &n
		}
	}
	return out, nil
}
