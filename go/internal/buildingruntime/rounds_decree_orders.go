package buildingruntime

import (
	"context"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

var errDecreeNoDefinitions = errors.New("decree recipes: the native source serves no definitions")

// DeclareOrders declares the production batches of the open produce-item
// decrees (OrderDeclarer) and notes whether one is declared, for the clock's
// game time (decreeLending). Harvest and hunt decrees stay admitStandardMethod's.
func (r *RoundsPopulationJoinerPlanner) DeclareOrders(ctx context.Context, snapshot domain.GenerationSnapshot, projection observation.ColonyProjection, benches []policy.GearBench) (policy.Declared, error) {
	var catalog *bridge.DefinitionCatalog
	var catalogErr error
	recipes := func(objective policy.QuestObjective) []policy.DecreeRecipe {
		if catalog == nil && catalogErr == nil {
			if source, ok := r.reviewer.native.(observation.DefinitionSource); ok {
				catalog, catalogErr = source.DefinitionCatalog(ctx, boundary.Identity(snapshot))
			} else {
				catalogErr = errDecreeNoDefinitions
			}
		}
		if catalogErr != nil {
			return nil
		}
		return decreeRecipes(benches, catalog, objective)
	}
	declared := policy.DeclareDecreeOrders(policy.DecreeOrderRequest{Offers: projection.Facts.QuestOffers, Now: int64(projection.Identity.Tick), Stock: projection.Facts.Resources, Recipes: recipes})
	if catalogErr != nil {
		if ctx.Err() != nil {
			return policy.Declared{}, ctx.Err()
		}
		declared.Abstain = true
	}
	r.mu.Lock()
	r.decreeWork = len(declared.Orders) > 0
	r.mu.Unlock()
	return declared, nil
}

func (r *RoundsPopulationJoinerPlanner) decreeLending() bool {
	r.mu.Lock()
	defer r.mu.Unlock()
	return r.decreeWork
}

// decreeRecipes are the bench census's recipes making the objective's item,
// each with the product count of one run, the stuff its ingredients decide,
// and the bills already making it (an active count bill whose ingredient filter
// holds the objective's stuff). A bill whose state is unread leaves the recipe
// unknown.
func decreeRecipes(benches []policy.GearBench, catalog *bridge.DefinitionCatalog, objective policy.QuestObjective) []policy.DecreeRecipe {
	var out []policy.DecreeRecipe
	for _, bench := range benches {
		recipes, known := bench.Recipes.Value()
		if !known {
			continue
		}
		bills, _ := bench.Bills.Value()
		for _, recipe := range recipes {
			row := bridge.DefRow[*d.RecipeDef](catalog, recipe.Definition)
			if row == nil {
				continue
			}
			units := int64(0)
			for _, product := range row.GetProducts() {
				if product.GetValue().GetThingDef() == objective.Def {
					units += int64(product.GetValue().GetCount())
				}
			}
			if units <= 0 {
				continue
			}
			available, ak := recipe.Available.Value()
			on, ok := recipe.AvailableOn.Value()
			candidate := policy.DecreeRecipe{Bench: bench.ID, BenchKind: bench.Def, Recipe: recipe.Definition, Product: policy.Resource(objective.Def), Units: units, Ingredients: recipe.Ingredients, Stuff: map[policy.Resource]bool{}}
			if ak && ok {
				candidate.Available = domain.Known(available && on)
			}
			groups, _ := recipe.Ingredients.Value()
			for _, group := range groups {
				for _, alternative := range group {
					def := bridge.DefRow[*d.ThingDef](catalog, string(alternative.Resource))
					if def != nil && def.GetStuffProps() != nil && row.GetProductHasIngredientStuff() {
						candidate.Stuff[alternative.Resource] = true
					}
				}
			}
			for _, bill := range bills {
				if bill.Recipe != recipe.Definition {
					continue
				}
				active, known := bill.Active.Value()
				if !known {
					candidate.Available = domain.Unknown[bool]()
					continue
				}
				if !active {
					continue
				}
				spec, known := bill.Spec.Value()
				if known && spec.Mode == domain.GearBatch && (objective.Stuff == "" || containsString(spec.Ingredients, objective.Stuff)) {
					candidate.Existing = true
					candidate.Standing = append(candidate.Standing, spec)
				} else {
					candidate.Available = domain.Known(false)
				}
			}
			out = append(out, candidate)
		}
	}
	return out
}
