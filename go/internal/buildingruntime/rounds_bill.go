package buildingruntime

import (
	"context"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

type RoundsBillPlanner struct {
	reviewer *Rounder
	need     policy.ConcernID
	purpose  policy.BillPurpose
	native   BillPlannerNative
}
type RoundsBillResult struct {
	Verdict
	Plan domain.PlanID
	// NativeWorkTicks asks for game time while a claimed art bill is still
	// being sculpted: a placed bill is no work to the clock.
	NativeWorkTicks uint32
}

// BillPlannerNative is the native reader behind a bill planner; the
// butcher purpose asserts it to RoundsResourceSource for a corpse storage zone.
type BillPlannerNative interface{}

// artBenchSource is the native read the part and gestation bills need: the
// benches' recipes and bills from the gear bench census.
type artBenchSource interface {
	ReadGearBenches(context.Context, *c.Identity) ([]bridge.GearBenchRead, bridge.Result, error)
}

var _ artBenchSource = (*bridge.Client)(nil)

// NewRoundsBillPlanner composes one bill purpose: cooking serves
// EnsureCooking, preservation MaintainFoodStorage, butchery EnsureFoodSupply, the
// cook-ahead bill MaintainRefrigeration under a solar flare. The sculpture,
// part, baby food and mech gestation bills are the ledger's
// (RoundsArtPlanner, RoundsCareBillDeclarer).
func NewRoundsBillPlanner(reviewer *Rounder, native BillPlannerNative, purpose policy.BillPurpose) (*RoundsBillPlanner, error) {
	if reviewer == nil || native == nil || (purpose != policy.CookFood && purpose != policy.PreserveFood && purpose != policy.ButcherFood && purpose != policy.CookAheadFood) {
		return nil, fmt.Errorf("%w: NewRoundsBillPlanner: reviewer == nil || native == nil || (purpose != policy.CookFood && purpose != policy.PreserveFood && purpos", ErrControl)
	}
	need := policy.EnsureFoodSupply
	switch purpose {
	case policy.CookFood:
		need = policy.EnsureCooking
	case policy.PreserveFood:
		need = policy.MaintainFoodStorage
	case policy.CookAheadFood:
		need = policy.MaintainRefrigeration
	}
	return &RoundsBillPlanner{reviewer: reviewer, native: native, purpose: purpose, need: need}, nil
}
func (r *RoundsBillPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoundsBillResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoundsBillResult{Verdict: BuildingReasonDisabled}, nil
	}
	if !state.ObservationKnown {
		return RoundsBillResult{}, fmt.Errorf("%w: step: !state.ObservationKnown", ErrControl)
	}
	review, err := p.journal.LoadRounds(call)
	if err != nil {
		return RoundsBillResult{}, err
	}
	if !review.Enabled || review.Snapshot != state.Snapshot {
		return RoundsBillResult{Verdict: BuildingReasonNoReview}, nil
	}
	goal, workable, err := p.journal.WorkableOwner(call, review, r.need)
	if err != nil {
		return RoundsBillResult{}, err
	}
	if !workable {
		return RoundsBillResult{Verdict: BuildingReasonNoDeficit}, nil
	}

	for _, method := range goal.OwnerMethods() {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoundsBillResult{}, err
		}
		if r.need == policy.EnsureFoodSupply {
			// Fields, foraging and hunts share the goal and stay open for
			// days; only an open bill is this planner's own work.
			for _, progress := range plan.Progress {
				if progress.Action().Kind() == domain.ProductionBillAction && domain.StandardWorkOpen([]domain.Progress{progress}) {
					if b, ok := progress.Action().ProductionBill(); ok && r.purpose == policy.ButcherFood && b.Mode() == domain.ButcherForever {
						continue
					}
					return RoundsBillResult{Verdict: BuildingReasonExistingWork}, nil
				}
			}
			continue
		}
		if r.purpose != policy.CookFood && store.PlanOpen(plan) {
			return RoundsBillResult{Verdict: BuildingReasonExistingWork}, nil
		}
	}
	plans, err := p.journal.LoadPlans(call)
	if err != nil {
		return RoundsBillResult{}, err
	}
	playerPlans, err := p.journal.PlayerPlans(call, playerWorld(state.Snapshot))
	if err != nil {
		return RoundsBillResult{}, err
	}
	definitions := roundsProjectDefinitions(plans, state.Snapshot, playerPlans)
	if r.purpose == policy.CookFood {
		definitions = append(definitions, "NutrientPasteDispenser", "Hopper")
	}
	expected, err := stepScope(call, r.reviewer.native)
	if err != nil {
		return RoundsBillResult{}, err
	}
	if !roundsBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoundsBillResult{}, fmt.Errorf("%w: step: !roundsBuildingBoundary(expected, state.Snapshot, review.Tick)", ErrControl)
	}
	claims, err := p.journal.ConstructionClaims(call, state.Snapshot, expected.Tick)
	if err != nil {
		return RoundsBillResult{}, err
	}
	read, err := r.reviewer.observeOwned(call, r.reviewer.native, expected, claims, definitions...)
	if err != nil {
		return RoundsBillResult{}, err
	}
	projection := read.Projection
	recordStepRead("bill", r.need, state.Snapshot, projection)
	// The food bills are the ledger's (DeclareOrders): this planner only says
	// why the purpose has nothing to place and lends game time to a reserve.
	request, err := r.foodRequest(projection, review)
	if err != nil {
		return RoundsBillResult{}, err
	}
	order := policy.SelectFoodOrder(request)
	reserveRunning := order.ReserveRunning
	switch order.Gap {
	case policy.FoodOrderPlan:
		return RoundsBillResult{Verdict: awaitingFoodPlan(order.Subject)}, nil
	case policy.FoodOrderField:
		return RoundsBillResult{Verdict: fieldUnavailable(order.Subject)}, nil
	case policy.FoodOrderNoDeficit:
		return RoundsBillResult{Verdict: BuildingReasonNoDeficit}, nil
	case policy.FoodOrderNoBill:
		return r.lendReserveWork(RoundsBillResult{Verdict: r.noBill(projection.ProductionBenches, projection.Facts.Colonists)}, reserveRunning), nil
	}
	return r.lendReserveWork(RoundsBillResult{Verdict: waitFor(policy.CauseMethodUsed, "ledger_bill")}, reserveRunning), nil
}

// foodRequest is the food purpose's reviewed inputs: the declaration and the
// verdict above read the same request.
func (r *RoundsBillPlanner) foodRequest(projection observation.ColonyProjection, review store.Rounds) (policy.FoodOrderRequest, error) {
	seasonal := r.reviewer.seasonal(projection.Facts)
	request := policy.FoodOrderRequest{Purpose: r.purpose, Benches: projection.ProductionBenches, Facts: projection.Facts, Supply: projection.CombinedFoodSupply, Humans: projection.FoodSupply, TargetDays: seasonal.FoodTargetDays, Warm: domain.Unknown[float64]()}
	switch r.purpose {
	case policy.CookFood:
		request.Meals = projection.MealRequest(seasonal.FoodMinDays, seasonal.FoodTargetDays)
	case policy.CookAheadFood:
		refrigeration, err := policy.ReviewRefrigeration(projection.Facts.FoodStorageUpkeep, review.Latches.Refrigeration, r.reviewer.policy.FoodStorage)
		if err != nil {
			return policy.FoodOrderRequest{}, err
		}
		request.Warm = refrigeration.WarmNutrition
	}
	return request, nil
}

// DeclareOrders is the declaration owned by r.need.
func (r *RoundsBillPlanner) DeclareOrders(ctx context.Context, snapshot domain.GenerationSnapshot, projection observation.ColonyProjection, benches []policy.GearBench) (policy.Declared, error) {
	declared, err := r.declareOrders(ctx, snapshot, projection, benches)
	return declared.For(r.need), err
}

// declareOrders declares a food purpose's wanted bills (OrderDeclarer).
func (r *RoundsBillPlanner) declareOrders(ctx context.Context, snapshot domain.GenerationSnapshot, projection observation.ColonyProjection, benches []policy.GearBench) (policy.Declared, error) {
	review, err := r.reviewer.player.journal.LoadRounds(ctx)
	if err != nil {
		return abstainOnRead(ctx, policy.CauseUnreadReview)
	}
	request, err := r.foodRequest(projection, review)
	if err != nil {
		// A review that cannot be read from the facts declares nothing.
		return abstainOnRead(ctx, policy.CauseUnreadFoodFacts)
	}
	return policy.DeclareFoodOrders(request, benches), nil
}

// billGapVerdict is the verdict of a bill selector that chose nothing: the
// gap names why, the subject the bill the planner wanted.
func billGapVerdict(gap policy.BillGap, subject string) Verdict {
	switch gap {
	case policy.BillGapNothingWanted:
		return BuildingReasonNoDeficit
	case policy.BillGapInProduction:
		return waitFor(policy.CauseExistingWork, subject)
	case policy.BillGapNoRecipe:
		return awaitingPlan(subject, string(gap))
	case policy.BillGapBenchFull:
		return awaitingPlan(subject, string(gap))
	case policy.BillGapWaste:
		return awaitingPlan("mech_waste", "")
	case policy.BillGapNoCharger:
		return awaitingPlan("mech_charger", "")
	}
	panic(fmt.Sprintf("bill gap %q is not one of the closed set", gap))
}

// noBill says why the food-bill selection chose nothing: a census it needs is
// unread, no usable bench of the purpose's kind stands, or the bills already
// standing cover what is owed.
func (r *RoundsBillPlanner) noBill(benches domain.Fact[[]policy.ProductionBench], colonists domain.Fact[int64]) Verdict {
	rows, known := benches.Value()
	if !known {
		return fieldUnavailable("production_benches")
	}
	if _, known := colonists.Value(); !known {
		return fieldUnavailable("colonists")
	}
	butcher := r.purpose == policy.ButcherFood
	for _, bench := range rows {
		if usable, known := bench.Usable.Value(); known && usable && bench.Butcher == butcher {
			return BuildingReasonNoDeficit
		}
	}
	if butcher {
		return awaitingPlan("butcher_bench", "")
	}
	return awaitingPlan("cooking_bench", "")
}

// reserveBillWorkTicks bounds one clock window spent letting a standing
// reserve bill run; the next review re-measures the stock.
const reserveBillWorkTicks = domain.TicksPerHour

// lendReserveWork asks for game time while a reserve bill runs and no new
// bill was admitted this step.
func (r *RoundsBillPlanner) lendReserveWork(result RoundsBillResult, running bool) RoundsBillResult {
	if running && result.Verdict != BuildingReasonAdmitted {
		result.NativeWorkTicks = max(result.NativeWorkTicks, reserveBillWorkTicks)
	}
	return result
}
