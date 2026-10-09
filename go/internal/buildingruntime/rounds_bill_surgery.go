package buildingruntime

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

// surgeryPartDemand is MaintainSurgery's part demand: the
// part-short restore wants from the pawn care facts, and the gear benches
// that could fabricate them. A native without the gear bench census, or a
// bench whose recipes or bills are unknown, fabricates nothing. The chosen
// elective adds its part after the served ones when a bench can
// fabricate it; ctx gates it.
func surgeryPartDemand(call context.Context, native any, identity *c.Identity, pawns domain.Fact[[]policy.CarePawn], ctx policy.SurgeryContext) ([]policy.SurgeryPart, []policy.ProductionBench, error) {
	parts := policy.SurgeryParts(policy.SelectSurgery(pawns, nil, policy.SurgeryContext{}).Wants)
	_, chosen := policy.ChosenElective(pawns, ctx)
	source, ok := native.(artBenchSource)
	if len(parts) == 0 && !chosen || !ok {
		return parts, nil, nil
	}
	reads, _, err := source.ReadGearBenches(call, identity)
	if err != nil {
		return nil, nil, err
	}
	benches := make([]policy.GearBench, 0, len(reads))
	for _, read := range reads {
		benches = append(benches, read.Bench)
	}
	parts, productions := surgeryPartsOn(pawns, ctx, benches)
	return parts, productions, nil
}

// surgeryPartsOn is the part demand over a bench readback already in hand: the
// served parts, and the chosen elective's when a bench can fabricate it, with
// the benches as production benches. No bench is returned while nothing is
// wanted.
func surgeryPartsOn(pawns domain.Fact[[]policy.CarePawn], ctx policy.SurgeryContext, benches []policy.GearBench) ([]policy.SurgeryPart, []policy.ProductionBench) {
	parts := policy.SurgeryParts(policy.SelectSurgery(pawns, nil, policy.SurgeryContext{}).Wants)
	elective, chosen := policy.ChosenElective(pawns, ctx)
	if len(parts) == 0 && !chosen {
		return parts, nil
	}
	productions := partBenches(benches)
	return append(parts, policy.ElectiveParts(elective, chosen, policy.FabricableParts(productions))...), productions
}

// partBenches converts the gear benches into production benches carrying
// each recipe's products; Available is researched and offered here. A bench's
// id is its token: the ledger writes no bill against a token.
func partBenches(benches []policy.GearBench) []policy.ProductionBench {
	var out []policy.ProductionBench
	for _, gear := range benches {
		recipes, rk := gear.Recipes.Value()
		bills, bk := gear.Bills.Value()
		if !rk || !bk {
			continue
		}
		bench := policy.ProductionBench{ID: gear.ID, Token: domain.Known(gear.ID), Usable: domain.Known(true)}
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
