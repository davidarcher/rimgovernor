package buildingruntime

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

// artBenchSource is the native read MaintainArt's bills need: the art
// benches come from the gear bench census (#1190).
type artBenchSource interface {
	ReadGearBenches(context.Context, *c.Identity) ([]bridge.GearBenchRead, bridge.Result, error)
}

var _ artBenchSource = (*bridge.Client)(nil)

// artSelection is the next artist's pinned sculpture bill: the art benches
// from ReadGearBenches, the qualifying artists from the pawn profiles, one
// SelectProductionBill per artist lacking a bill; the first is admitted.
func (r *RoutineBillPlanner) artSelection(call context.Context, state ControlState, projection observation.ColonyProjection) (policy.BillSelection, bool, error) {
	native, ok := r.native.(artBenchSource)
	pawns, pk := projection.WorkPawns.Value()
	if !ok || !pk {
		return policy.BillSelection{}, false, nil
	}
	reads, _, err := native.ReadGearBenches(call, boundary.Identity(state.Snapshot))
	if err != nil {
		return policy.BillSelection{}, false, err
	}
	bills := policy.SelectArtBills(domain.Known(artBenches(reads)), projection.Facts.Colonists, policy.Artists(policy.Profiles(pawns)))
	if len(bills) == 0 {
		return policy.BillSelection{}, false, nil
	}
	return bills[0], true, nil
}

// artBenches converts the gear benches offering the small sculpture recipe
// into production benches; a bench whose bills are unknown is left out.
func artBenches(reads []bridge.GearBenchRead) []policy.ProductionBench {
	var out []policy.ProductionBench
	for _, read := range reads {
		recipes, rk := read.Bench.Recipes.Value()
		bills, bk := read.Bench.Bills.Value()
		if !rk || !bk {
			continue
		}
		bench := policy.ProductionBench{ID: read.Bench.ID, Token: domain.Known(read.Token), Usable: domain.Known(true)}
		for _, recipe := range recipes {
			if recipe.Definition != policy.SculptureRecipe {
				continue
			}
			available, ak := recipe.Available.Value()
			on, ok := recipe.AvailableOn.Value()
			bench.Recipes = append(bench.Recipes, policy.ProductionRecipe{Name: recipe.Definition, Available: domain.Fact[bool]{}})
			if ak && ok {
				bench.Recipes[len(bench.Recipes)-1].Available = domain.Known(available && on)
			}
		}
		if len(bench.Recipes) == 0 {
			continue
		}
		for _, bill := range bills {
			bench.Bills = append(bench.Bills, policy.ExistingProductionBill{ID: bill.ID, Recipe: bill.Recipe, Worker: bill.Worker, Active: bill.Active})
		}
		out = append(out, bench)
	}
	return out
}
