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

// RoundsFoodStorageUpkeepSource is the native census RoundsFoodStorageUpkeepPlanner
// reads immediately before proposing a MaintainFoodStorage method: a fresh
// colony facts read for the current food-supply census (the same top-level
// section used during ordinary rounds, not gated behind the planning
// flag), the generic per-definition resource-storage census
// rounds_resource.go's RoundsResourceSource already established (reused
// here rather than inventing a food-specific storage read), and the same
// generic bench/recipe census and ingredient stock funding
// GearProduce/MaintainMedicalReserves/MaintainResource already use.
// Native checks the bill against
// live state when the ProductionBillIntent applies.
type RoundsFoodStorageUpkeepSource interface {
	ReadColonyFacts(context.Context, *c.Identity, bool) (*o.ColonyFactsReply, bridge.Result, error)
	ReadResourceSources(context.Context, *c.Identity, string) ([]bridge.ResourceSourceRow, policy.ResourceStorage, bridge.Result, error)
	ReadGearBenches(context.Context, *c.Identity) ([]bridge.GearBenchRead, bridge.Result, error)
	// FrameTables carries the things table the colony census's food stocks
	// reference and the catalog their defs resolve against.
	FrameTables(context.Context, *c.Identity) (bridge.Tables, error)
}
type RoundsFoodStorageUpkeepPlanner struct {
	reviewer *Rounder
	native   RoundsFoodStorageUpkeepSource
}
type RoundsFoodStorageUpkeepResult struct {
	Verdict
	Plan domain.PlanID
}

func NewRoundsFoodStorageUpkeepPlanner(reviewer *Rounder, native RoundsFoodStorageUpkeepSource) (*RoundsFoodStorageUpkeepPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, fmt.Errorf("%w: NewRoundsFoodStorageUpkeepPlanner: reviewer == nil || native == nil", ErrControl)
	}
	return &RoundsFoodStorageUpkeepPlanner{reviewer, native}, nil
}

// foodStorageObservationFacts decodes the freshly read colony census with
// the same observation.DecodeFoodSupply the rounds uses, so the
// planner and the review agree on every stock's cover and temperature
// facts; a stock the things table misses leaves them unknown.
func foodStorageObservationFacts(ctx context.Context, native RoundsFoodStorageUpkeepSource, v *o.ColonyFactsSnapshot) policy.FoodStorageObservation {
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

func (r *RoundsFoodStorageUpkeepPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoundsFoodStorageUpkeepResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoundsFoodStorageUpkeepResult{Verdict: BuildingReasonDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoundsFoodStorageUpkeepResult{}, fmt.Errorf("%w: step: !state.ObservationKnown || state.Snapshot.Validate() != nil", ErrControl)
	}
	review, err := p.journal.LoadRounds(call)
	if err != nil {
		return RoundsFoodStorageUpkeepResult{}, err
	}
	if !review.Enabled || review.Snapshot != state.Snapshot {
		return RoundsFoodStorageUpkeepResult{Verdict: BuildingReasonNoReview}, nil
	}
	goal, workable, err := p.journal.Workable(call, review, policy.MaintainFoodStorage)
	if err != nil {
		return RoundsFoodStorageUpkeepResult{}, err
	}
	if !workable {
		return RoundsFoodStorageUpkeepResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoundsFoodStorageUpkeepResult{}, err
		}
		var pending []domain.Progress
		for _, progress := range plan.Progress {
			if progress.Action().Kind() != domain.ProductionBillAction {
				pending = append(pending, progress)
			}
		}
		if domain.StandardWorkOpen(pending) {
			return RoundsFoodStorageUpkeepResult{Verdict: BuildingReasonExistingWork}, nil
		}
	}
	identity := boundary.Identity(state.Snapshot)
	reply, _, err := r.reviewer.colonyFacts(call, r.native, identity, false)
	if err != nil {
		return RoundsFoodStorageUpkeepResult{}, err
	}
	observed := reply.GetObserved()
	if observed == nil {
		return RoundsFoodStorageUpkeepResult{}, fmt.Errorf("%w: step: observed == nil", ErrControl)
	}
	if err = bridge.ValidateColonyFacts(observed, identity); err != nil {
		return RoundsFoodStorageUpkeepResult{}, fmt.Errorf("%w: step: err != nil", ErrControl)
	}
	if _, err = boundary.Context(observed.Context, state.Snapshot); err != nil || observed.Context.GetTick() < int64(review.Tick) {
		return RoundsFoodStorageUpkeepResult{}, fmt.Errorf("%w: step: err != nil || observed.Context.GetTick() < int64(review.Tick)", ErrControl)
	}
	facts := foodStorageObservationFacts(call, r.native, observed)
	expected := observation.Identity{Colony: state.Snapshot.Colony, Load: state.Snapshot.Load, Map: state.Snapshot.Map,
		Tick: domain.Tick(observed.Context.GetTick()), NativeGeneration: domain.Known(state.Snapshot.Native)}
	reading, err := r.reviewer.observeOwned(call, r.reviewer.native, expected, domain.Unknown[[]policy.ConstructionClaim]())
	if err != nil {
		return RoundsFoodStorageUpkeepResult{}, err
	}
	if !foodPlanSupport(reading.Projection.Facts.FoodPlan, policy.CandidateReserve, "stock-protection") {
		return RoundsFoodStorageUpkeepResult{Verdict: awaitingFoodPlan("stock-protection")}, nil
	}
	if reserve, known := reading.Projection.Facts.FoodReserve.Value(); known && (len(reserve.Hold) > 0 || len(reserve.Release) > 0) {
		if reading.Projection.Identity.Tick != domain.Tick(observed.Context.GetTick()) {
			return RoundsFoodStorageUpkeepResult{Verdict: fieldUnavailable("food_reserve")}, nil
		}
		return r.admitReserve(call, epoch, goal, observed, reserve)
	}
	larder, err := policy.SelectCorpseLarder(facts)
	if err != nil {
		return RoundsFoodStorageUpkeepResult{}, err
	}
	if larder.Kind != "" {
		return r.admitCorpseLarder(call, epoch, arbiter, goal, observed, larder)
	}
	foodReview, err := policy.ReviewFoodStorage(facts, review.Latches.FoodStorage, r.reviewer.policy.FoodStorage)
	if err != nil {
		return RoundsFoodStorageUpkeepResult{}, err
	}
	if !foodReview.Active {
		return RoundsFoodStorageUpkeepResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	choice, err := r.storageChoice(call, identity, facts, foodReview)
	if err != nil {
		return RoundsFoodStorageUpkeepResult{}, err
	}
	if choice.Kind == policy.FoodStorageStuck {
		// Vanilla hauling already carries loose food to covered storage, so no
		// haul is planned: the verdict names what blocks it.
		return RoundsFoodStorageUpkeepResult{Verdict: foodNotStoredVerdict(policy.FoodStorageStuckReason(facts, r.reviewer.policy.FoodStorage, policy.DangerHaulers(reading.Projection.WorkPawns)))}, nil
	}
	if choice.Kind != policy.FoodStorageProduce {
		return RoundsFoodStorageUpkeepResult{Verdict: foodStorageChoiceVerdict(choice.Kind)}, nil
	}
	// The meal stock bill is the ledger's (DeclareOrders): name why none is wanted.
	benches, err := r.gearBenches(call, identity)
	if err != nil {
		return RoundsFoodStorageUpkeepResult{}, err
	}
	if _, kind, ok, err := policy.FoodStorageMealOrder(choice, benches); err != nil {
		return RoundsFoodStorageUpkeepResult{}, err
	} else if !ok {
		return RoundsFoodStorageUpkeepResult{Verdict: medicineChoiceVerdict(kind, choice.Resource)}, nil
	}
	return RoundsFoodStorageUpkeepResult{Verdict: waitFor(WaitMethodUsed, "ledger_bill")}, nil
}

// gearBenches is the bench census with its bills, the ledger's readback.
func (r *RoundsFoodStorageUpkeepPlanner) gearBenches(ctx context.Context, identity *c.Identity) ([]policy.GearBench, error) {
	census, _, err := r.native.ReadGearBenches(ctx, identity)
	if err != nil {
		return nil, err
	}
	benches := make([]policy.GearBench, 0, len(census))
	for _, row := range census {
		benches = append(benches, row.Bench)
	}
	return benches, nil
}

// storageChoice is the method for food that is unstored: the spare capacity of
// each covered site the unstored stock could use, read per definition (an
// unread site is unknown capacity), then the policy's choice.
func (r *RoundsFoodStorageUpkeepPlanner) storageChoice(ctx context.Context, identity *c.Identity, facts policy.FoodStorageObservation, review policy.FoodStorageReview) (policy.FoodStorageMethod, error) {
	names := foodStorageDefNames(facts, r.reviewer.policy.FoodStorage)
	sites := make([]policy.FoodStorageSite, 0, len(names))
	for _, name := range names {
		_, storage, _, err := r.native.ReadResourceSources(ctx, identity, name)
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
	return policy.SelectFoodStorageMethod(policy.FoodStoragePlanningRequest{Review: review, Sites: domain.Known(sites), Resource: foodStorageResourceDefinition})
}

// DeclareOrders declares the meal stock bill MaintainFoodStorage falls back to
// when no covered site has room (OrderDeclarer). The reserve and the corpse
// larder come first, as in the step: while either is owed the standing meal
// bill is kept as it stands.
func (r *RoundsFoodStorageUpkeepPlanner) DeclareOrders(ctx context.Context, snapshot domain.GenerationSnapshot, projection observation.ColonyProjection, benches []policy.GearBench) (policy.Declared, error) {
	f := projection.Facts
	if !policy.FoodPlanSupport(f.FoodPlan, policy.CandidateReserve, "stock-protection") {
		return policy.Declared{Abstain: true}, nil
	}
	standing := policy.StandingStockOrders(benches, foodStorageResourceDefinition)
	if reserve, known := f.FoodReserve.Value(); known && (len(reserve.Hold) > 0 || len(reserve.Release) > 0) {
		return standing, nil
	}
	larder, err := policy.SelectCorpseLarder(f.FoodStorageUpkeep)
	if err != nil {
		return policy.Declared{Abstain: true}, nil
	}
	if larder.Kind != "" {
		return standing, nil
	}
	review, err := r.reviewer.player.journal.LoadRounds(ctx)
	if err != nil {
		return abstainOnRead(ctx)
	}
	foodReview, err := policy.ReviewFoodStorage(f.FoodStorageUpkeep, review.Latches.FoodStorage, r.reviewer.policy.FoodStorage)
	if err != nil {
		return policy.Declared{Abstain: true}, nil
	}
	if !foodReview.Active {
		return policy.Declared{}, nil
	}
	choice, err := r.storageChoice(ctx, boundary.Identity(snapshot), f.FoodStorageUpkeep, foodReview)
	if err != nil {
		return policy.Declared{Abstain: true}, nil
	}
	switch choice.Kind {
	case policy.FoodStorageUnknown:
		return policy.Declared{Abstain: true}, nil
	case policy.FoodStorageProduce:
		spec, kind, ok, err := policy.FoodStorageMealOrder(choice, benches)
		switch {
		case err != nil || kind == policy.MedicineUnknown:
			return policy.Declared{Abstain: true}, nil
		case ok:
			return policy.Declared{Orders: []policy.OrderSpec{spec}}, nil
		}
	}
	return policy.Declared{}, nil
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
