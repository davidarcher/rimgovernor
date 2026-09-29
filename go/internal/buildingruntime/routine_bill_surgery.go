package buildingruntime

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

// surgeryPartDemand is MaintainSurgery's part demand (#1168): the
// part-short restore wants from the pawn care facts, and the gear benches
// that could fabricate them. A native without the gear bench census, or a
// bench whose recipes or bills are unknown, fabricates nothing.
func surgeryPartDemand(call context.Context, native any, identity *c.Identity, pawns domain.Fact[[]policy.CarePawn]) ([]policy.SurgeryPart, []policy.ProductionBench, error) {
	parts := policy.SurgeryParts(policy.SelectSurgery(pawns, nil, policy.SurgeryContext{}).Wants)
	source, ok := native.(artBenchSource)
	if len(parts) == 0 || !ok {
		return parts, nil, nil
	}
	reads, _, err := source.ReadGearBenches(call, identity)
	if err != nil {
		return nil, nil, err
	}
	return parts, partBenches(reads), nil
}

// partBenches converts the gear benches into production benches carrying
// each recipe's products; Available is researched and offered here.
func partBenches(reads []bridge.GearBenchRead) []policy.ProductionBench {
	var out []policy.ProductionBench
	for _, read := range reads {
		recipes, rk := read.Bench.Recipes.Value()
		bills, bk := read.Bench.Bills.Value()
		if !rk || !bk {
			continue
		}
		bench := policy.ProductionBench{ID: read.Bench.ID, Token: domain.Known(read.Token), Usable: domain.Known(true)}
		for _, recipe := range recipes {
			row := policy.ProductionRecipe{Name: recipe.Definition}
			available, ak := recipe.Available.Value()
			on, ok := recipe.AvailableOn.Value()
			if ak && ok {
				row.Available = domain.Known(available && on)
			}
			for _, product := range recipe.Products {
				row.Products = append(row.Products, policy.ProductionProduct{Name: string(product)})
			}
			bench.Recipes = append(bench.Recipes, row)
		}
		for _, bill := range bills {
			bench.Bills = append(bench.Bills, policy.ExistingProductionBill{ID: bill.ID, Recipe: bill.Recipe, Worker: bill.Worker, Active: bill.Active})
		}
		out = append(out, bench)
	}
	return out
}
