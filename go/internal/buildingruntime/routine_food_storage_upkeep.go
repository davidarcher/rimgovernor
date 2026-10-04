package buildingruntime

import (
	"context"
	"fmt"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// foodStorageResourceDefinition is the one native resource definition
// MaintainFoodStorage's Produce fallback replenishes when no covered site has
// spare capacity: RimWorld's basic cooked meal, produced through the same generic
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
// GearProduce/MaintainMedicalReserves/MaintainResource already use.
// Native checks the bill against
// live state when the ProductionBillIntent applies.
type RoutineFoodStorageUpkeepSource interface {
	ReadColonyFacts(context.Context, *c.Identity, bool) (*o.ColonyFactsReply, bridge.Result, error)
	ReadResourceSources(context.Context, *c.Identity, string) ([]bridge.ResourceSourceRow, policy.ResourceStorage, bridge.Result, error)
	ReadGearBenches(context.Context, *c.Identity) ([]bridge.GearBenchRead, bridge.Result, error)
	ReadSupplyStock(context.Context, *c.Identity, []string) ([]policy.Stock, bridge.Result, error)
	// FrameTables carries the things table the colony census's food stocks
	// reference (#1343) and the catalog their defs resolve against (#1733).
	FrameTables(context.Context, *c.Identity) (bridge.Tables, error)
}
type RoutineFoodStorageUpkeepPlanner struct {
	reviewer *RoutineReviewer
	native   RoutineFoodStorageUpkeepSource
}
type RoutineFoodStorageUpkeepResult struct {
	Verdict
	Plan domain.PlanID
}

func NewRoutineFoodStorageUpkeepPlanner(reviewer *RoutineReviewer, native RoutineFoodStorageUpkeepSource) (*RoutineFoodStorageUpkeepPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, fmt.Errorf("%w: NewRoutineFoodStorageUpkeepPlanner: reviewer == nil || native == nil", ErrControl)
	}
	return &RoutineFoodStorageUpkeepPlanner{reviewer, native}, nil
}

// foodStorageObservationFacts decodes the freshly read colony census with
// the same observation.DecodeFoodSupply the routine review uses, so the
// planner and the review agree on every stock's cover and temperature
// facts; a stock the things table misses leaves them unknown.
func foodStorageObservationFacts(ctx context.Context, native RoutineFoodStorageUpkeepSource, v *o.ColonyFactsSnapshot) policy.FoodStorageObservation {
	food := v.GetFoodSupply().GetObserved()
	if food == nil {
		return policy.FoodStorageObservation{}
	}
	tables, err := native.FrameTables(ctx, v.GetContext().GetIdentity())
	if err != nil {
		return policy.FoodStorageObservation{}
	}
	supply, known, err := observation.DecodeFoodSupply(food, tables.Things, tables.Catalog)
	if err != nil || !known {
		return policy.FoodStorageObservation{}
	}
	return policy.FoodStorageStocks(supply, float64(tables.Catalog.Constants.FullRotRateC))
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
		if !policy.FoodStorageUnstored(entry, observed.ChilledMaxC, p) {
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
		return RoutineFoodStorageUpkeepResult{Verdict: BuildingReasonDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoutineFoodStorageUpkeepResult{}, fmt.Errorf("%w: step: !state.ObservationKnown || state.Snapshot.Validate() != nil", ErrControl)
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutineFoodStorageUpkeepResult{}, err
	}
	if !review.Enabled || review.Snapshot != state.Snapshot {
		return RoutineFoodStorageUpkeepResult{Verdict: BuildingReasonNoReview}, nil
	}
	goal, workable, err := p.journal.Workable(call, review, policy.MaintainFoodStorage)
	if err != nil {
		return RoutineFoodStorageUpkeepResult{}, err
	}
	if !workable {
		return RoutineFoodStorageUpkeepResult{Verdict: BuildingReasonNoDeficit}, nil
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
			return RoutineFoodStorageUpkeepResult{Verdict: BuildingReasonExistingWork}, nil
		}
	}
	started := r.reviewer.clock.Now()
	identity := boundary.Identity(state.Snapshot)
	reply, _, err := r.reviewer.colonyFacts(call, r.native, identity, false)
	if err != nil {
		return RoutineFoodStorageUpkeepResult{}, err
	}
	observed := reply.GetObserved()
	if observed == nil {
		return RoutineFoodStorageUpkeepResult{}, fmt.Errorf("%w: step: observed == nil", ErrControl)
	}
	if err = bridge.ValidateColonyFacts(observed, identity); err != nil {
		return RoutineFoodStorageUpkeepResult{}, fmt.Errorf("%w: step: err != nil", ErrControl)
	}
	if _, err = boundary.Context(observed.Context, state.Snapshot); err != nil || observed.Context.GetTick() < int64(review.Tick) {
		return RoutineFoodStorageUpkeepResult{}, fmt.Errorf("%w: step: err != nil || observed.Context.GetTick() < int64(review.Tick)", ErrControl)
	}
	facts := foodStorageObservationFacts(call, r.native, observed)
	expected := observation.Identity{Colony: state.Snapshot.Colony, Load: state.Snapshot.Load, Map: state.Snapshot.Map,
		Tick: domain.Tick(observed.Context.GetTick()), NativeGeneration: domain.Known(state.Snapshot.Native)}
	reading, err := r.reviewer.observeOwned(call, r.reviewer.native, expected, domain.Unknown[[]policy.ConstructionClaim]())
	if err != nil {
		return RoutineFoodStorageUpkeepResult{}, err
	}
	if !foodPlanSupport(reading.Projection.Facts.FoodPlan, policy.FoodReserve, "stock-protection") {
		return RoutineFoodStorageUpkeepResult{Verdict: awaitingFoodPlan("stock-protection")}, nil
	}
	if reserve, known := reading.Projection.Facts.FoodReserve.Value(); known && (len(reserve.Hold) > 0 || len(reserve.Release) > 0) {
		if reading.Projection.Identity.Tick != domain.Tick(observed.Context.GetTick()) {
			return RoutineFoodStorageUpkeepResult{Verdict: fieldUnavailable("food_reserve")}, nil
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
		return RoutineFoodStorageUpkeepResult{Verdict: BuildingReasonNoDeficit}, nil
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
	if choice.Kind == policy.FoodStorageStuck {
		// Vanilla hauling already carries loose food to covered storage, so no
		// haul is planned: the verdict names what blocks it.
		return RoutineFoodStorageUpkeepResult{Verdict: foodNotStoredVerdict(policy.FoodStorageStuckReason(facts, r.reviewer.policy.FoodStorage, policy.DangerHaulers(reading.Projection.WorkPawns)))}, nil
	}
	if choice.Kind != policy.FoodStorageProduce {
		return RoutineFoodStorageUpkeepResult{Verdict: foodStorageChoiceVerdict(choice.Kind)}, nil
	}
	census, _, err := r.native.ReadGearBenches(call, identity)
	if err != nil {
		return RoutineFoodStorageUpkeepResult{}, err
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
	// a medicine -- only its caller's medicine resource
	// is), so it is reused directly here rather than duplicated: a
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
		return RoutineFoodStorageUpkeepResult{Verdict: medicineChoiceVerdict(medChoice.Kind, choice.Resource)}, nil
	}
	_, ok := tokens[medChoice.Bench]
	if !ok {
		return RoutineFoodStorageUpkeepResult{}, fmt.Errorf("%w: step: !ok", ErrControl)
	}
	if !arbiter.tryClaim(nil, "bench:"+medChoice.Bench) {
		return RoutineFoodStorageUpkeepResult{Verdict: claimHeld("bench")}, nil
	}
	id := domain.MintPlanID()
	target := int32(medChoice.Target)
	if int64(target) != medChoice.Target {
		return RoutineFoodStorageUpkeepResult{}, fmt.Errorf("%w: step: int64(target) != medChoice.Target", ErrControl)
	}
	bill, err := domain.NewProductionBill(medChoice.Bench, medChoice.Recipe, domain.StockTarget, target)
	if err != nil {
		return RoutineFoodStorageUpkeepResult{}, err
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
		return RoutineFoodStorageUpkeepResult{}, fmt.Errorf("%w: step: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge", ErrControl)
	}
	if _, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, medChoice.ID, plan); err != nil {
		return RoutineFoodStorageUpkeepResult{}, err
	}
	return RoutineFoodStorageUpkeepResult{Verdict: BuildingReasonAdmitted, Plan: id}, nil
}

// foodNotStoredVerdict is the verdict naming why food is unstored though a
// covered site has room.
func foodNotStoredVerdict(reason policy.FoodStuckReason) Verdict {
	return awaitingPlan("food_not_stored", string(reason))
}

// foodStorageChoiceVerdict is the verdict of a food-storage method choice that
// is neither a relocation nor a production: the deficit recovered, the spare
// capacity of a storage site is unread, or the production method was already
// tried.
func foodStorageChoiceVerdict(kind policy.FoodStorageMethodKind) Verdict {
	switch kind {
	case policy.FoodStorageRecovered:
		return BuildingReasonNoDeficit
	case policy.FoodStorageUnknown:
		return fieldUnavailable("food_storage_capacity")
	}
	return waitFor(WaitMethodUsed, "food_storage_method")
}
