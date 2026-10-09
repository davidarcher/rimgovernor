package buildingruntime

import (
	"context"
	"crypto/sha256"
	"errors"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
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
// cook-ahead bill MaintainRefrigeration under a solar flare, and the
// pinned sculpture bills MaintainArt, the part bills
// MaintainSurgery, the baby food bill MaintainBabyFeeding, and the mech gestation bills MaintainMechs.
func NewRoundsBillPlanner(reviewer *Rounder, native BillPlannerNative, purpose policy.BillPurpose) (*RoundsBillPlanner, error) {
	if reviewer == nil || native == nil || (purpose != policy.CookFood && purpose != policy.PreserveFood && purpose != policy.ButcherFood && purpose != policy.CookAheadFood && purpose != policy.SurgeryPartBill && purpose != policy.BabyFoodBill && purpose != policy.MechGestationBill) {
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
	case policy.SurgeryPartBill:
		need = policy.MaintainSurgery
	case policy.BabyFoodBill:
		need = policy.MaintainBabyFeeding
	case policy.MechGestationBill:
		need = policy.MaintainMechs
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
	if r.purpose == policy.SurgeryPartBill {
		// A part bill whose need is gone (the owner stayed Met) is removed
		// first.
		if plan, err := r.reviewer.removeStaleBill(call, epoch, arbiter, state, goal, r.need); err != nil || plan != "" {
			return RoundsBillResult{Verdict: BuildingReasonAdmitted, Plan: plan}, err
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
	if r.purpose == policy.MechGestationBill {
		selected, verdict, err := r.mechSelection(call, state, projection)
		if err != nil {
			return RoundsBillResult{}, err
		}
		if !verdict.IsZero() {
			return RoundsBillResult{Verdict: verdict}, nil
		}
		// Each gestation is a new method of the goal: the bill is the same
		// bench, recipe and count as the one before it.
		return r.admit(call, epoch, arbiter, state, goal, read, selected, len(goal.OwnerMethods()))
	}
	if r.purpose == policy.SurgeryPartBill {
		parts, benches, err := surgeryPartDemand(call, r.native, boundary.Identity(state.Snapshot), projection.Facts.MedicalPawns, projection.SurgeryContext())
		if err != nil {
			return RoundsBillResult{}, err
		}
		// A part bill no waiting operation names goes whatever the owner's
		// finding.
		wanted := policy.SurgeryPartsWanted(parts)
		judge := func(b policy.StaleBill) (bool, bool) { return true, policy.BillWanted(b.Products, wanted) }
		if plan, err := r.reviewer.removeUnwantedBill(call, epoch, arbiter, state, review, goal, r.need, judge); err != nil || plan != "" {
			return RoundsBillResult{Verdict: BuildingReasonAdmitted, Plan: plan}, err
		}
		selected, gap := policy.SelectSurgeryPartBill(benches, parts)
		if gap != "" {
			return RoundsBillResult{Verdict: billGapVerdict(gap, "surgery_part_bill")}, nil
		}
		return r.admit(call, epoch, arbiter, state, goal, read, selected, 0)
	}
	if r.purpose == policy.BabyFoodBill {
		babies, known := projection.Facts.BabyFeeding.Value()
		if !known {
			return RoundsBillResult{Verdict: fieldUnavailable("baby_feeding")}, nil
		}
		selected, known := policy.SelectProductionBill(r.purpose, projection.ProductionBenches, projection.Facts.Colonists, domain.Fact[float64]{}, domain.Fact[float64]{}, 1, policy.ProductionBillContext{BabyFeeding: &babies})
		if !known {
			return RoundsBillResult{Verdict: BuildingReasonNoDeficit}, nil
		}
		return r.admit(call, epoch, arbiter, state, goal, read, selected, 0)
	}
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
	return r.lendReserveWork(RoundsBillResult{Verdict: waitFor(WaitMethodUsed, "ledger_bill")}, reserveRunning), nil
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

// DeclareOrders declares a food purpose's wanted bills (OrderDeclarer).
func (r *RoundsBillPlanner) DeclareOrders(ctx context.Context, _ domain.GenerationSnapshot, projection observation.ColonyProjection, benches []policy.GearBench) (policy.Declared, error) {
	review, err := r.reviewer.player.journal.LoadRounds(ctx)
	if err != nil {
		return abstainOnRead(ctx)
	}
	request, err := r.foodRequest(projection, review)
	if err != nil {
		// A review that cannot be read from the facts declares nothing.
		return abstainOnRead(ctx)
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
		return waitFor(WaitExistingWork, subject)
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

// admit commits the selected bill as the goal's method, once per goal
// epoch and claim.
func (r *RoundsBillPlanner) admit(call, epoch context.Context, arbiter *stepArbiter, state ControlState, goal store.WorkOwner, read observation.RoundsReading, selected policy.BillSelection, round int) (RoundsBillResult, error) {
	p := r.reviewer.player
	value, err := domain.NewProductionBill(selected.Bench, selected.Recipe, selected.Mode, selected.Target, selected.Ingredients...)
	if selected.Mode == domain.HumanButcherForever {
		value, err = domain.NewHumanButcherBill(selected.Bench, selected.Recipe, selected.Worker)
	} else if err == nil && selected.Worker != "" {
		value, err = value.PinWorker(selected.Worker)
	}
	if err != nil {
		return RoundsBillResult{}, err
	}
	claimed, err := p.journal.BillClaimed(call, state.Snapshot, selected.Bench, value.ClaimRecipe())
	if err != nil {
		return RoundsBillResult{}, err
	}
	// Finite batches expire (store.checkBillMethod): a claim from an earlier
	// batch does not bar the next one.
	if claimed && selected.Replace == "" && selected.Mode != domain.GearBatch {
		return RoundsBillResult{Verdict: waitFor(WaitMethodUsed, "bill_claim")}, nil
	}
	// The bill planners of one step run concurrently and read the same
	// bench token; the second bill on a bench would hold forever on the
	// first's write. One bill per bench per step.
	if arbiter != nil && !arbiter.tryClaim(nil, "bench:"+selected.Bench) {
		return RoundsBillResult{Verdict: claimHeld("bench")}, nil
	}
	hash := sha256.New()
	fmt.Fprintf(hash, "%s/%s/%s/%d", selected.Bench, selected.Recipe, selected.Mode, selected.Target)
	if selected.Worker != "" {
		fmt.Fprintf(hash, "/%s", selected.Worker)
	}
	// A finished sculpture batch stays on the bench, inactive; the next
	// piece of the same shape (a sale sculpture after the room's)
	// is a new method, keyed by the finished batches before it.
	if round > 0 {
		fmt.Fprintf(hash, "/round%d", round)
	}
	if selected.Replace != "" {
		fmt.Fprint(hash, "/", selected.Replace, "/", selected.Token)
	}
	method := domain.MethodID(fmt.Sprintf("bill-%x", hash.Sum(nil)[:16]))
	if _, err = p.journal.LoadOwnerMethod(call, goal, method); err == nil {
		return RoundsBillResult{Verdict: waitFor(WaitMethodUsed, "bill_method")}, nil
	} else if !errors.Is(err, store.ErrNotFound) {
		return RoundsBillResult{}, err
	}
	id := domain.MintPlanID()
	if selected.Replace != "" {
		value, err = value.ReplaceOwnedBill(selected.Replace)
		if err != nil {
			return RoundsBillResult{}, err
		}
	}
	action, err := domain.NewProductionBillAction(domain.ActionID(string(id)+"-0"), value)
	if err != nil {
		return RoundsBillResult{}, err
	}
	actions := []domain.Action{action}
	plan, err := domain.NewPlan(id, 1, actions)
	if err != nil {
		return RoundsBillResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoundsBillResult{}, err
	}
	if p.session.State() != state {
		return RoundsBillResult{}, fmt.Errorf("%w: step: p.session.State() != state", ErrControl)
	}
	now := r.reviewer.clock.Now()
	if now.Before(read.StartedAt) || now.Sub(read.StartedAt) > r.reviewer.maxAge {
		return RoundsBillResult{}, observation.ErrStale
	}
	if err = p.journal.CommitOwnerMethod(call, goal, method, "", plan); err != nil {
		return RoundsBillResult{}, err
	}
	return RoundsBillResult{Verdict: BuildingReasonAdmitted, Plan: id}, nil
}
