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
// state is the art benches' sculpture bills. A zero verdict means a bill was
// selected; otherwise the verdict says what is missing.
func (r *RoutineBillPlanner) artSelection(call context.Context, state ControlState, projection observation.ColonyProjection, medicineActive bool) (selected policy.BillSelection, verdict Verdict, art artBenchState, err error) {
	native, ok := r.native.(artBenchSource)
	if !ok {
		return policy.BillSelection{}, fieldUnavailable("art_benches"), art, nil
	}
	pawns, pk := projection.WorkPawns.Value()
	if !pk {
		return policy.BillSelection{}, fieldUnavailable("work_pawns"), art, nil
	}
	reads, _, err := native.ReadGearBenches(call, boundary.Identity(state.Snapshot))
	if err != nil {
		return policy.BillSelection{}, Verdict{}, art, err
	}
	profiles := policy.Profiles(pawns)
	list := artBenches(reads)
	art = artBills(list)
	benches := domain.Known(list)
	// An inspired artist's large sculpture comes first (#1192).
	// Sale sculptures (#1193) are asked for while no room is owed.
	demand := artDemand(projection, profiles)
	demand.Sale = policy.RoutineArtForSale(projection.Facts, r.reviewer.policy, medicineActive)
	bills := append(policy.SelectInspiredArtBills(benches, policy.InspiredArtists(profiles)), policy.SelectArtBills(benches, projection.Facts.Colonists, policy.Artists(profiles), demand)...)
	switch {
	case len(bills) > 0:
		return bills[0], Verdict{}, art, nil
	case len(list) == 0:
		return policy.BillSelection{}, awaitingPlan("art_bench", ""), art, nil
	case len(policy.Artists(profiles)) == 0:
		return policy.BillSelection{}, noWorker("artist"), art, nil
	}
	return policy.BillSelection{}, BuildingReasonNoDeficit, art, nil
}

// artBenchState is the art benches' sculpture bills (#1195): sculpting
// while one is active (the clock owes it game time), and each worker's
// finished batches, which stay on the bench and key the next batch's
// method apart from theirs.
type artBenchState struct {
	sculpting bool
	finished  map[string]int
}

func artBills(benches []policy.ProductionBench) artBenchState {
	out := artBenchState{finished: map[string]int{}}
	for _, bench := range benches {
		for _, bill := range bench.Bills {
			active, known := bill.Active.Value()
			if !known || !policy.IsSculptureRecipe(bill.Recipe) {
				continue
			}
			if active {
				out.sculpting = true
			} else if worker, wk := bill.Worker.Value(); wk {
				out.finished[worker]++
			}
		}
	}
	return out
}

// artDemand sizes the art bills (#1191) from the same bedroom census as
// sculptureRoomsOwed, the colony stock and the artists' skills; an unknown
// census leaves only the small sculpture.
func artDemand(facts observation.ColonyProjection, profiles []policy.PawnProfile) policy.ArtDemand {
	stock, _ := facts.Resources.Value()
	rooms, rk := facts.Rooms.Value()
	census, ck := facts.Facts.CurrentConstruction.Value()
	obs, sk := facts.Facts.Sleeping.Value()
	traits := sleepingTraits(facts)
	if !rk || !ck || !sk || !census.Colony || traits == nil {
		return policy.NewArtDemand(domain.Unknown[policy.SleepingObservation](), nil, nil, stock, profiles)
	}
	tier, _ := facts.BuildTier.Value()
	return policy.NewArtDemand(facts.Facts.Sleeping, policy.RoomQualityTargets(obs, traits, tier), policy.TidyFurnitureRooms(rooms, census, facts.Cells), stock, profiles)
}

// artBenches converts the gear benches offering a sculpture recipe into
// production benches; a bench whose bills are unknown is left out.
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
			if !policy.IsSculptureRecipe(recipe.Definition) {
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

// artNativeWorkTicks is the window an active sculpture bill asks for per
// step; the review re-reads the bench between windows (#1195).
const artNativeWorkTicks = 2500
