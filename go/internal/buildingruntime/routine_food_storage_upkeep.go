package buildingruntime

import (
	"context"
	"crypto/sha256"
	"fmt"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
)

// foodStorageResourceDefinition is the one native resource definition
// MaintainFoodStorage's Produce fallback replenishes when no covered site has
// spare capacity, matching medicineResourceDefinition's own single hardcoded
// target: RimWorld's basic cooked meal, produced through the same generic
// bench/recipe/StockTarget bill machinery SelectMedicineMethod already uses.
const foodStorageResourceDefinition = policy.Resource("MealSimple")

// foodStorageSiteLimit bounds how many distinct unstored-perishable-stock def
// names this planner probes for spare covered capacity in one step, to avoid
// an unbounded number of native ReadResourceSources calls in a single tick.
const foodStorageSiteLimit = 16

// RoutineFoodStorageUpkeepSource is the native census RoutineFoodStorageUpkeepPlanner
// reads immediately before proposing a MaintainFoodStorage method: a fresh
// colony facts read for the current food-supply census (the same top-level
// section used during ordinary routine review, not gated behind the planning
// flag), the generic per-definition resource-storage census
// routine_resource.go's RoutineResourceSource already established (reused
// here rather than inventing a food-specific storage read), and the same
// generic bench/recipe census and ingredient stock funding
// GearProduce/MaintainMedicalReserves/MaintainResource already use. PreviewBill
// re-checks one already-selected bench/recipe bill immediately before
// dispatch, the same acceptance-not-authority preview those planners use.
type RoutineFoodStorageUpkeepSource interface {
	ReadColonyFacts(context.Context, *c.Identity, bool, []string) (*o.ColonyFactsReply, bridge.Result, error)
	ReadResourceSources(context.Context, *c.Identity, string) ([]bridge.ResourceSourceRow, policy.ResourceStorage, bridge.Result, error)
	ReadGearBenches(context.Context, *c.Identity) ([]bridge.GearBenchRead, bridge.Result, error)
	ReadSupplyStock(context.Context, *c.Identity, []string) ([]policy.Stock, bridge.Result, error)
	PreviewBill(context.Context, *c.Identity, domain.ProductionBill) (*op.PreviewReply, bridge.Result, error)
}
type RoutineFoodStorageUpkeepPlanner struct {
	reviewer *RoutineReviewer
	native   RoutineFoodStorageUpkeepSource
}
type RoutineFoodStorageUpkeepResult struct {
	Reason RoutineBuildingReason
	Plan   domain.PlanID
}

func NewRoutineFoodStorageUpkeepPlanner(reviewer *RoutineReviewer, native RoutineFoodStorageUpkeepSource) (*RoutineFoodStorageUpkeepPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, ErrControl
	}
	return &RoutineFoodStorageUpkeepPlanner{reviewer, native}, nil
}
func (r *RoutineFoodStorageUpkeepPlanner) Step(ctx context.Context) (RoutineFoodStorageUpkeepResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, false)
	if err != nil {
		return RoutineFoodStorageUpkeepResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

// foodStorageObservationFacts decodes the freshly read colony census with
// the same observation.DecodeFoodSupply the routine review uses, so the
// planner and the review agree on every stock's cover and temperature facts.
func foodStorageObservationFacts(v *o.ColonyFactsSnapshot) policy.FoodStorageObservation {
	food := v.GetFoodSupply().GetObserved()
	if food == nil {
		return policy.FoodStorageObservation{}
	}
	supply, err := observation.DecodeFoodSupply(food)
	if err != nil {
		return policy.FoodStorageObservation{}
	}
	return policy.FoodStorageStocks(supply)
}

// foodStorageDefNames collects the distinct native resource definition names
// among currently-unstored, at-risk (perishable, not yet rotted) stock rows,
// deterministically ordered and capped at foodStorageSiteLimit -- this
// planner's FoodStorageSite candidates are "spare covered capacity for a
// given food resource definition" (see FoodStorageSite's own doc comment),
// not a summed-across-items site, matching how SecureSupplies/upkeep already
// treat storage capacities as per-definition.
func foodStorageDefNames(observed policy.FoodStorageObservation, p policy.FoodStoragePolicy) []string {
	stocks, known := observed.Stocks.Value()
	if !known {
		return nil
	}
	names := map[string]bool{}
	for _, entry := range stocks {
		if !policy.FoodStorageUnstored(entry, p) {
			continue
		}
		if name := string(entry.Stock.DefName); name != "" {
			names[name] = true
		}
	}
	sorted := make([]string, 0, len(names))
	for name := range names {
		sorted = append(sorted, name)
	}
	sort.Strings(sorted)
	if len(sorted) > foodStorageSiteLimit {
		sorted = sorted[:foodStorageSiteLimit]
	}
	return sorted
}

func (r *RoutineFoodStorageUpkeepPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoutineFoodStorageUpkeepResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutineFoodStorageUpkeepResult{Reason: BuildingMethodDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoutineFoodStorageUpkeepResult{}, ErrControl
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutineFoodStorageUpkeepResult{}, err
	}
	if !review.Enabled || review.Snapshot != state.Snapshot {
		return RoutineFoodStorageUpkeepResult{Reason: BuildingMethodNoReview}, nil
	}
	var goal store.GoalState
	found := false
	for _, binding := range review.Goals {
		if binding.Need == policy.MaintainFoodStorage {
			goal, err = p.journal.LoadGoal(call, binding.Goal)
			found = true
			break
		}
	}
	if err != nil {
		return RoutineFoodStorageUpkeepResult{}, err
	}
	if !found || goal.Goal.Status != domain.GoalActive || goal.Goal.Need != domain.NeedDeficit {
		return RoutineFoodStorageUpkeepResult{Reason: BuildingMethodNoDeficit}, nil
	}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoutineFoodStorageUpkeepResult{}, err
		}
		var pending []domain.Progress
		for _, progress := range plan.Progress {
			if progress.Action().Kind() != domain.ProductionBillAction {
				pending = append(pending, progress)
			}
		}
		if domain.GoalWorkOpen(pending) {
			return RoutineFoodStorageUpkeepResult{Reason: BuildingMethodExistingWork}, nil
		}
	}
	started := r.reviewer.clock.Now()
	identity := boundary.Identity(state.Snapshot)
	reply, _, err := r.native.ReadColonyFacts(call, identity, false, nil)
	if err != nil {
		return RoutineFoodStorageUpkeepResult{}, err
	}
	observed := reply.GetObserved()
	if observed == nil {
		return RoutineFoodStorageUpkeepResult{}, ErrControl
	}
	if err = bridge.ValidateColonyFacts(observed, identity); err != nil {
		return RoutineFoodStorageUpkeepResult{}, ErrControl
	}
	if _, err = boundary.Context(observed.Context, state.Snapshot); err != nil || observed.Context.GetTick() < int64(review.Tick) {
		return RoutineFoodStorageUpkeepResult{}, ErrControl
	}
	facts := foodStorageObservationFacts(observed)
	expected := observation.Identity{Colony: state.Snapshot.Colony, Load: state.Snapshot.Load, Map: state.Snapshot.Map,
		Tick: domain.Tick(observed.Context.GetTick()), NativeGeneration: domain.Known(state.Snapshot.Native)}
	reading, err := r.reviewer.observeOwned(call, r.reviewer.native, expected, domain.Unknown[[]policy.ConstructionClaim]())
	if err != nil {
		return RoutineFoodStorageUpkeepResult{}, err
	}
	if !foodPlanSupport(reading.Projection.Facts.FoodPlan, policy.FoodReserve, "stock-protection") {
		return RoutineFoodStorageUpkeepResult{Reason: BuildingMethodUnknown}, nil
	}
	if reserve, known := reading.Projection.Facts.FoodReserve.Value(); known && (len(reserve.Hold) > 0 || len(reserve.Release) > 0) {
		if reading.Projection.Identity.Tick != domain.Tick(observed.Context.GetTick()) {
			return RoutineFoodStorageUpkeepResult{Reason: BuildingMethodUnknown}, nil
		}
		return r.admitReserve(call, epoch, goal, observed, reserve)
	}
	larder, err := policy.SelectCorpseLarder(facts)
	if err != nil {
		return RoutineFoodStorageUpkeepResult{}, err
	}
	if larder.Kind != "" {
		return r.admitCorpseLarder(call, epoch, arbiter, goal, observed, larder)
	}
	foodReview, err := policy.ReviewFoodStorage(facts, review.Latches.FoodStorage, r.reviewer.policy.FoodStorage)
	if err != nil {
		return RoutineFoodStorageUpkeepResult{}, err
	}
	if !foodReview.Active {
		return RoutineFoodStorageUpkeepResult{Reason: BuildingMethodUsed}, nil
	}
	seen := make([]domain.MethodID, 0, len(goal.Methods))
	for _, method := range goal.Methods {
		seen = append(seen, method.Method)
	}
	names := foodStorageDefNames(facts, r.reviewer.policy.FoodStorage)
	sites := make([]policy.FoodStorageSite, 0, len(names))
	for _, name := range names {
		_, storage, _, err := r.native.ReadResourceSources(call, identity, name)
		if err != nil {
			sites = append(sites, policy.FoodStorageSite{ID: name, Capacity: domain.Unknown[int64]()})
			continue
		}
		spare := storage.Capacity - storage.Stored
		if spare < 0 {
			spare = 0
		}
		sites = append(sites, policy.FoodStorageSite{ID: name, Capacity: domain.Known(spare)})
	}
	choice, err := policy.SelectFoodStorageMethod(policy.FoodStoragePlanningRequest{Review: foodReview, Sites: domain.Known(sites), Resource: foodStorageResourceDefinition, Seen: seen})
	if err != nil {
		return RoutineFoodStorageUpkeepResult{}, err
	}
	if choice.Kind == policy.FoodStorageRelocate {
		// Relocating already-observed unstored stock into an existing
		// covered site needs a concrete pawn/thing/destination-cell triple
		// (domain.NewHaulAction's own requirement) or an equivalent
		// hauling-to-zone native action; policy.FoodStorageMethod's Relocate
		// outcome only carries {Site: defName, Amount}, with no such native
		// evidence, and no existing domain/native machinery already expresses
		// "haul stock of definition X into covered storage" generically.
		// Dispatching Relocate is therefore a follow-on slice needing new
		// native hauling-to-covered-storage wiring; this slice only acts on
		// the Produce fallback below, mirroring how routine_medical.go only
		// acts on MedicineProduce and reports every other outcome as used.
		return RoutineFoodStorageUpkeepResult{Reason: BuildingMethodUsed}, nil
	}
	if choice.Kind != policy.FoodStorageProduce {
		return RoutineFoodStorageUpkeepResult{Reason: BuildingMethodUsed}, nil
	}
	census, _, err := r.native.ReadGearBenches(call, identity)
	if err != nil {
		return RoutineFoodStorageUpkeepResult{}, err
	}
	if len(census) > 256 {
		return RoutineFoodStorageUpkeepResult{}, ErrControl
	}
	benches := make([]policy.GearBench, 0, len(census))
	tokens := map[string]string{}
	for _, row := range census {
		benches = append(benches, row.Bench)
		tokens[row.Bench.ID] = row.Token
	}
	ingredientNames := recipeIngredientNames(census, "")
	var stock []policy.Stock
	if len(ingredientNames) > 0 {
		stock, _, err = r.native.ReadSupplyStock(call, identity, ingredientNames)
		if err != nil {
			return RoutineFoodStorageUpkeepResult{}, err
		}
	}
	// SelectFoodStorageMethod's Produce outcome only returns {Resource,
	// Target}; bench/recipe selection for that resource is the routine
	// planner's own job, the same deferral SelectMedicineMethod/GearProduce
	// perform. SelectMedicineMethod's bench-walking loop is already
	// generic-resource-shaped (its Resource field is not hardcoded to
	// MedicineHerbal -- only its caller's medicineResourceDefinition
	// constant is), so it is reused directly here rather than duplicated: a
	// synthetic MedicalReserveReview{Active: true, Target: choice.Target}
	// stands in for the medicine-specific review SelectMedicineMethod
	// otherwise expects, since it only reads Review.Active/Review.Target.
	medChoice, err := policy.SelectMedicineMethod(policy.MedicinePlanningRequest{
		Review:   policy.MedicalReserveReview{Active: true, Target: domain.Known(choice.Target)},
		Resource: choice.Resource, Seen: seen, Benches: domain.Known(benches), Stock: stock,
	})
	if err != nil {
		return RoutineFoodStorageUpkeepResult{}, err
	}
	if medChoice.Kind != policy.MedicineProduce {
		return RoutineFoodStorageUpkeepResult{Reason: BuildingMethodUsed}, nil
	}
	token, ok := tokens[medChoice.Bench]
	if !ok {
		return RoutineFoodStorageUpkeepResult{}, ErrControl
	}
	if !arbiter.tryClaim(nil, "bench:"+medChoice.Bench) {
		return RoutineFoodStorageUpkeepResult{Reason: BuildingMethodUsed}, nil
	}
	digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%d/%s", goal.Goal.ID, goal.Goal.Epoch, medChoice.ID)))
	id := domain.PlanID(fmt.Sprintf("routine-food-storage-upkeep-%x", digest[:16]))
	target := int32(medChoice.Target)
	if int64(target) != medChoice.Target {
		return RoutineFoodStorageUpkeepResult{}, ErrControl
	}
	bill, err := domain.NewProductionBill(medChoice.Bench, medChoice.Recipe, token, domain.StockTarget, target)
	if err != nil {
		return RoutineFoodStorageUpkeepResult{}, err
	}
	preview, _, err := r.native.PreviewBill(call, boundary.Identity(state.Snapshot), bill)
	if err != nil {
		return RoutineFoodStorageUpkeepResult{}, err
	}
	evaluated := preview.GetEvaluated()
	if evaluated == nil || !evaluated.GetAccepted() {
		return RoutineFoodStorageUpkeepResult{Reason: BuildingMethodRefused}, nil
	}
	if _, err = boundary.Context(evaluated.Context, state.Snapshot); err != nil {
		return RoutineFoodStorageUpkeepResult{}, ErrControl
	}
	action, err := domain.NewProductionBillAction(domain.ActionID(fmt.Sprintf("%s-0", id)), bill)
	if err != nil {
		return RoutineFoodStorageUpkeepResult{}, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoutineFoodStorageUpkeepResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutineFoodStorageUpkeepResult{}, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoutineFoodStorageUpkeepResult{}, ErrControl
	}
	if _, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, medChoice.ID, plan); err != nil {
		return RoutineFoodStorageUpkeepResult{}, err
	}
	return RoutineFoodStorageUpkeepResult{Reason: BuildingMethodAdmitted, Plan: id}, nil
}
