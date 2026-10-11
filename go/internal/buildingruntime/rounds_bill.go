package buildingruntime

import (
	"context"
	"fmt"
	"sync"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

// RoundsBillPlanner is one food purpose's OrderDeclarer: cooking, preservation,
// butchery or the cook-ahead bill. The bills are the ledger's; the declarer
// also says why the purpose has nothing to place (purposeReporter) and the
// ledger files that verdict.
type RoundsBillPlanner struct {
	reviewer *Rounder
	need     policy.ConcernID
	purpose  policy.BillPurpose
	mu       sync.Mutex
	latest   purposeReport
}

// BillPlannerNative is the native reader behind a bill declarer.
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
func NewRoundsBillPlanner(reviewer *Rounder, purpose policy.BillPurpose) (*RoundsBillPlanner, error) {
	if reviewer == nil || (purpose != policy.CookFood && purpose != policy.PreserveFood && purpose != policy.ButcherFood && purpose != policy.CookAheadFood) {
		return nil, fmt.Errorf("%w: NewRoundsBillPlanner: reviewer == nil || (purpose != policy.CookFood && purpose != policy.PreserveFood && purpose != policy.ButcherFood && purpose != policy.CookAheadFood)", ErrControl)
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
	return &RoundsBillPlanner{reviewer: reviewer, purpose: purpose, need: need}, nil
}

// Each bill purpose files its verdict under a key of its own, not under the
// concern it serves: cooking, butchery, preservation and the cook-ahead bill
// share EnsureCooking, EnsureFoodSupply, MaintainFoodStorage and
// MaintainRefrigeration with the building, acquisition and storage planners,
// whose notes the concern's strip shows. A bill verdict filed on the concern
// would compete with them for the one winning cause (noteRank) and relabel the
// strip with another purpose's reason. The keys are subject keys: they have no
// progress record, so they only explain the bill purpose
// (concern_transitions.concern).
const (
	cookingBillsKey      policy.ConcernID = "cookingBills"
	preservationBillsKey policy.ConcernID = "preservationBills"
	butcherBillsKey      policy.ConcernID = "butcherBills"
	cookAheadBillsKey    policy.ConcernID = "cookAheadBills"
)

// reportTarget is the key the purpose's verdict files under and the concern
// whose owner gates it.
func (r *RoundsBillPlanner) reportTarget() (key, need policy.ConcernID) {
	switch r.purpose {
	case policy.CookFood:
		return cookingBillsKey, r.need
	case policy.PreserveFood:
		return preservationBillsKey, r.need
	case policy.CookAheadFood:
		return cookAheadBillsKey, r.need
	}
	return butcherBillsKey, r.need
}

func (r *RoundsBillPlanner) report() purposeReport {
	r.mu.Lock()
	defer r.mu.Unlock()
	return r.latest
}

func (r *RoundsBillPlanner) note(report purposeReport) {
	r.mu.Lock()
	defer r.mu.Unlock()
	r.latest = report
}

// openWork reports whether the owner holds this purpose's own open work. The
// food supply goal is shared with fields, foraging and hunts that stay open
// for days, so only an open bill counts there.
func (r *RoundsBillPlanner) openWork(ctx context.Context, journal *store.Store, owner store.StandardState) (bool, error) {
	for _, method := range owner.Methods {
		plan, err := journal.LoadPlan(ctx, method.Plan)
		if err != nil {
			return false, err
		}
		if r.need == policy.EnsureFoodSupply {
			for _, progress := range plan.Progress {
				if progress.Action().Kind() == domain.ProductionBillAction && domain.StandardWorkOpen([]domain.Progress{progress}) {
					if b, ok := progress.Action().ProductionBill(); ok && r.purpose == policy.ButcherFood && b.Mode() == domain.ButcherForever {
						continue
					}
					return true, nil
				}
			}
			continue
		}
		if r.purpose != policy.CookFood && store.PlanOpen(plan) {
			return true, nil
		}
	}
	return false, nil
}

// foodReport is the purpose's verdict on a reviewed request: why the selection
// has nothing to place, and the game time a standing reserve bill needs.
func (r *RoundsBillPlanner) foodReport(request policy.FoodOrderRequest, projection observation.ColonyProjection) purposeReport {
	order := policy.SelectFoodOrder(request)
	report := purposeReport{openWork: r.openWork}
	switch order.Gap {
	case policy.FoodOrderPlan:
		report.order = awaitingFoodPlan(order.Subject)
		return report
	case policy.FoodOrderField:
		report.order = fieldUnavailable(order.Subject)
		return report
	case policy.FoodOrderNoDeficit:
		report.order = BuildingReasonNoDeficit
		return report
	case policy.FoodOrderNoBill:
		report.order = r.noBill(projection.ProductionBenches, projection.Facts.Colonists)
	default:
		report.order = waitFor(policy.CauseMethodUsed, "ledger_bill")
	}
	if order.ReserveRunning {
		report.workTicks = reserveBillWorkTicks
	}
	return report
}

// foodRequest is the food purpose's reviewed inputs: the declaration and the
// verdict read the same request.
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

// declareOrders declares a food purpose's wanted bills (OrderDeclarer) and
// notes the purpose's verdict on the same request.
func (r *RoundsBillPlanner) declareOrders(ctx context.Context, snapshot domain.GenerationSnapshot, projection observation.ColonyProjection, benches []policy.GearBench) (policy.Declared, error) {
	review, err := r.reviewer.player.journal.LoadRounds(ctx)
	if err != nil {
		r.note(purposeReport{order: fieldUnavailable("review")})
		return abstainOnRead(ctx, policy.CauseUnreadReview)
	}
	request, err := r.foodRequest(projection, review)
	if err != nil {
		// A review that cannot be read from the facts declares nothing.
		r.note(purposeReport{order: fieldUnavailable("food_facts")})
		return abstainOnRead(ctx, policy.CauseUnreadFoodFacts)
	}
	r.note(r.foodReport(request, projection))
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
