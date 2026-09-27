package buildingruntime

import (
	"context"
	"crypto/sha256"
	"errors"
	"fmt"
	"slices"
	"sort"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	snap "github.com/davidarcher/RimGovernor/go/internal/snapshot"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
)

// RoutineResourceSource is the native census RoutineResourcePlanner reads
// immediately before proposing a MaintainResource method. Like
// RoutineMedicalPlanner, it reads a fresh top-level resource stock census
// (not gated behind the planning flag) plus the same generic bench/recipe
// census and ingredient stock funding GearProduce/MaintainMedicalReserves
// already established. Native checks the bill against
// live state when the ProductionBillIntent applies.
//
// This planner covers the resource method's bench/recipe
// fallback branch (policy.SelectResourceMethod), the same bench-production
// path GearProduce/MaintainMedicalReserves dispatch through, plus its native
// mine-source acquisition branch (policy.SelectResourceSources) and its
// material-storage zoning branch (policy.SelectResourceStorageZone). Whenever
// the bench/recipe path cannot fund the deficit and a fresh source selection
// includes a "mine" source, that source's covered storage is checked first
// (materialStorageZoneFallback): if a new stockpile zone is needed, it is
// admitted and mine acquisition is deferred to a later tick (the storage
// zone-build action takes the place of an acquisition action whenever storage is
// inadequate). Only once storage already covers the deficit (or no mine
// source was selected) does this planner dispatch a
// domain.MineAcquisitionAction against the mine source through the second,
// independently-registered mine-acquisition vertical. Extraction development
// is still not dispatched here.
type RoutineResourceSource interface {
	observation.ColonySource
	ReadGearBenches(context.Context, *c.Identity) ([]bridge.GearBenchRead, bridge.Result, error)
	ReadSupplyStock(context.Context, *c.Identity, []string) ([]policy.Stock, bridge.Result, error)
	ReadResourceSources(context.Context, *c.Identity, string) ([]bridge.ResourceSourceRow, policy.ResourceStorage, bridge.Result, error)
	PreviewAcquisition(context.Context, *c.Identity, bridge.AcquisitionTarget) (*op.PreviewReply, bridge.Result, error)
	PreviewZone(context.Context, *c.Identity, bridge.ZoneTarget) (*op.PreviewReply, bridge.Result, error)
}
type RoutineResourcePlanner struct {
	reviewer *RoutineReviewer
	native   RoutineResourceSource
}
type RoutineResourceResult struct {
	Reason RoutineBuildingReason
	Plan   domain.PlanID
	// NativeWorkTicks asks for a clock window without a plan of its own: a
	// standing production bill whose first iteration completed needs game
	// time, not another method, while its resource is still in deficit.
	NativeWorkTicks uint32
	// Sources is populated whenever the bench/recipe production path
	// (policy.SelectResourceMethod) could not fund the dynamically-selected
	// resource and a fresh native ListResourceSources/ReadResourceSources
	// read plus policy.SelectResourceSources found undesignated mine/harvest
	// sources that could cover the outstanding deficit. When the selection
	// includes a "mine" source (the only method carrying a Cell/Token), this
	// same read's StorageCapacity payload is checked first
	// (materialStorageZoneFallback): if hauling that source's yield needs a
	// new covered stockpile zone, that zone is admitted (Reason/Plan set
	// exactly like the bench/recipe path) and the mine source itself is not
	// yet dispatched. Once storage already covers the deficit, the mine
	// source is actually dispatched (dispatchMineSource) -- see Reason/Plan
	// -- against the second, independently-registered mine-acquisition
	// vertical; any other selected method is still surfaced here for
	// observability only, since only mine sources carry the CAS evidence
	// this vertical's AcquireResource dispatch needs. A native read failure
	// here is swallowed rather than propagated, since the bench/recipe
	// outcome above already stands on its own.
	Sources []policy.ResourceSource
}

func NewRoutineResourcePlanner(reviewer *RoutineReviewer, native RoutineResourceSource) (*RoutineResourcePlanner, error) {
	if reviewer == nil || native == nil {
		return nil, fmt.Errorf("%w: NewRoutineResourcePlanner: reviewer == nil || native == nil", ErrControl)
	}
	return &RoutineResourcePlanner{reviewer, native}, nil
}

// resourceStockFacts decodes the same generic top-level resource census
// medicalReserveObservationFacts reads (v.Resources), from a freshly read
// ColonyFactsSnapshot rather than the cached routine review snapshot.
func resourceStockFacts(v *o.ColonyFactsSnapshot) domain.Fact[[]policy.Amount] {
	if medicalIssue(v.Issues, "resources") {
		return domain.Unknown[[]policy.Amount]()
	}
	rows := []policy.Amount{}
	for _, q := range v.Resources {
		if q.Units == nil {
			return domain.Unknown[[]policy.Amount]()
		}
		rows = append(rows, policy.Amount{Resource: policy.Resource(q.GetDefName()), Count: q.GetUnits()})
	}
	return domain.Known(rows)
}

func (r *RoutineResourcePlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoutineResourceResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutineResourceResult{Reason: BuildingMethodDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoutineResourceResult{}, fmt.Errorf("%w: step: !state.ObservationKnown || state.Snapshot.Validate() != nil", ErrControl)
	}
	// The stone-block floor only names its block once the census is read
	// below; a configured floor keeps the step alive until then.
	targets, err := r.reviewer.resourceTargets(call, state.Snapshot, domain.Unknown[[]policy.Amount]())
	if err != nil {
		return RoutineResourceResult{}, err
	}
	if len(targets) == 0 && !r.reviewer.policy.ResourceGoalConfigured() {
		return RoutineResourceResult{Reason: BuildingMethodDisabled}, nil
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutineResourceResult{}, err
	}
	if !review.Enabled || review.Snapshot != state.Snapshot {
		return RoutineResourceResult{Reason: BuildingMethodNoReview}, nil
	}
	call, recorded := recordPlannerStep(call, policy.MaintainResource, state.Snapshot, review.Tick)
	defer recorded()
	var goal store.GoalState
	found := false
	for _, binding := range review.Goals {
		if binding.Need == policy.MaintainResource {
			goal, err = p.journal.LoadGoal(call, binding.Goal)
			found = true
			break
		}
	}
	if err != nil {
		return RoutineResourceResult{}, err
	}
	if !found || goal.Goal.Status != domain.GoalActive || goal.Goal.Need != domain.NeedDeficit {
		return RoutineResourceResult{Reason: BuildingMethodNoDeficit}, nil
	}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoutineResourceResult{}, err
		}
		if store.PlanOpen(plan) {
			return RoutineResourceResult{Reason: BuildingMethodExistingWork}, nil
		}
	}
	started := r.reviewer.clock.Now()
	identity := boundary.Identity(state.Snapshot)
	reply, _, err := r.native.ReadColonyFacts(call, identity, false)
	if err != nil {
		return RoutineResourceResult{}, err
	}
	observed := reply.GetObserved()
	if observed == nil {
		return RoutineResourceResult{}, fmt.Errorf("%w: step: observed == nil", ErrControl)
	}
	if err = bridge.ValidateColonyFacts(observed, identity); err != nil {
		return RoutineResourceResult{}, fmt.Errorf("%w: step: err != nil", ErrControl)
	}
	if _, err = boundary.Context(observed.Context, state.Snapshot); err != nil || observed.Context.GetTick() < int64(review.Tick) {
		return RoutineResourceResult{}, fmt.Errorf("%w: step: err != nil || observed.Context.GetTick() < int64(review.Tick)", ErrControl)
	}
	stock := resourceStockFacts(observed)
	if result, handled, err := r.deepDrill(call, epoch, state, goal, review, started); err != nil || handled {
		return result, err
	}
	if targets, err = r.reviewer.resourceTargets(call, state.Snapshot, stock); err != nil {
		return RoutineResourceResult{}, err
	}
	ranked, err := policy.RankResourceTargets(targets, stock)
	if err != nil {
		return RoutineResourceResult{}, err
	}
	// Worst-covered floor first, but a floor whose only sources are ones
	// this vertical cannot dispatch (a harvest-only selection, nothing
	// reachable) must not starve the next demanded resource behind it
	// (#595): keep going until a target admits a plan, lends a window, or
	// reports a real block. When nothing at all is dispatchable the first
	// target's outcome stands, so its selected sources stay observable.
	var first *RoutineResourceResult
	for _, row := range ranked {
		result, err := r.dispatchResourceGoal(call, epoch, state, goal, review.Tick, identity, row.Resource, row.Target, stock, nil, started)
		if err != nil {
			return RoutineResourceResult{}, err
		}
		if result.Reason != BuildingMethodUsed || result.Plan != "" || result.NativeWorkTicks != 0 {
			return result, nil
		}
		if first == nil {
			first = &result
		}
	}
	if first == nil {
		return RoutineResourceResult{Reason: BuildingMethodUsed}, nil
	}
	return *first, nil
}

// dispatchResourceGoal is the shared MaintainResource/MaintainAnimalFeed
// acquisition tail, once each goal's own selection has picked one
// (resource, absolute stock floor) pair: bench/recipe production
// (policy.SelectResourceMethod) and native mine sources
// (policy.SelectResourceSources) ranked by the acquisition catalog
// (policy.RankResourceCandidates, #728), sources alone when no bill fits,
// and, if hauling them needs new storage, a covered stockpile zone -- see RoutineAnimalFeedPlanner for the
// MaintainAnimalFeed caller. A non-nil benches set restricts the bench/recipe
// path to those bench IDs (the caller's delivery constraint: a bill's product
// drops at its bench); an empty set refuses the production path outright
// rather than producing where the product cannot be used.
func (r *RoutineResourcePlanner) dispatchResourceGoal(call, epoch context.Context, state ControlState, goal store.GoalState, reviewTick domain.Tick, identity *c.Identity, resource policy.Resource, target int64, stock domain.Fact[[]policy.Amount], benchFilter []string, started time.Time, ingredients ...string) (RoutineResourceResult, error) {
	if r.reviewer.policy.GearSpareTargets[resource] > 0 {
		_, storage, _, err := r.native.ReadResourceSources(call, identity, string(resource))
		if err != nil {
			return RoutineResourceResult{}, err
		}
		zone, needed, blocked, err := policy.SelectStockpileCapacity(max(0, target-storage.Stored), storage)
		if err != nil {
			return RoutineResourceResult{}, err
		}
		if blocked {
			return RoutineResourceResult{Reason: BuildingMethodNoSpace}, nil
		}
		if needed {
			return r.admitStorageZone(call, epoch, state, goal, reviewTick, resource, zone.Cells, started, "gear-spares-storage", "routine-resource-zone")
		}
	}
	beer := resource == "Beer"
	if beer {
		resource = "Wort"
	}
	p := r.reviewer.player
	seen := make([]domain.MethodID, 0, len(goal.Methods))
	for _, method := range goal.Methods {
		seen = append(seen, method.Method)
	}
	census, _, err := r.native.ReadGearBenches(call, identity)
	if err != nil {
		return RoutineResourceResult{}, err
	}
	allowed := map[string]bool{}
	for _, id := range benchFilter {
		allowed[id] = true
	}
	benches := make([]policy.GearBench, 0, len(census))
	tokens := map[string]string{}
	for _, row := range census {
		if benchFilter != nil && !allowed[row.Bench.ID] {
			continue
		}
		benches = append(benches, row.Bench)
		tokens[row.Bench.ID] = row.Token
	}
	if benchFilter != nil && len(benches) == 0 {
		// No bench where the product would be usable: producing elsewhere
		// only piles it up out of reach (#237). Lend a window so a bench
		// built or an area widened meanwhile is seen next step.
		clockSchedulerLog("%s: no reachable bench for %s among %d benches", goal.Goal.ID, resource, len(census))
		return RoutineResourceResult{Reason: BuildingMethodRefused, NativeWorkTicks: stockWaitTicks}, nil
	}
	names := recipeIngredientNames(census, resource)
	var supply []policy.Stock
	if len(names) > 0 {
		supply, _, err = r.native.ReadSupplyStock(call, identity, names)
		if err != nil {
			return RoutineResourceResult{}, err
		}
	}
	var runways []policy.ResourceRunway
	if resource == policy.ComponentResource {
		review, err := p.journal.LoadRoutineReview(call)
		if err != nil {
			return RoutineResourceResult{}, err
		}
		if review.Enabled && review.Snapshot == state.Snapshot {
			runways = review.ResourceRunwayState()
		}
	}
	request := policy.ResourceMethodRequest{Resource: resource, Target: target, Seen: seen, Benches: domain.Known(benches), Stock: supply, Runways: runways, CurrentStock: stock}
	snap.NoteResourceMethod(call, request)
	choice, err := policy.SelectResourceMethod(request)
	if err != nil {
		return RoutineResourceResult{}, err
	}
	if choice.Kind != policy.ResourceMethodProduce {
		if beer && choice.Kind == policy.ResourceMethodWait {
			return RoutineResourceResult{Reason: BuildingMethodExistingWork, NativeWorkTicks: stockWaitTicks}, nil
		}
		var pre *sourceSelection
		if !beer && benchFilter == nil {
			remote, err := r.miningReach(call, state, reviewTick)
			if err != nil {
				return RoutineResourceResult{}, err
			}
			selected, sourceStorage, ok := r.sourcesForDeficit(call, identity, resource, target, stock, remote)
			if !ok {
				r.reviewer.bids.bid(state.Snapshot, resource, bidResource, 0, "", reviewTick)
				return RoutineResourceResult{Reason: BuildingMethodUsed}, nil
			}
			pre = &sourceSelection{selected, sourceStorage}
			if result, handled, err := r.storageFloor(call, epoch, state, goal, reviewTick, resource, sourceStorage, target-resourceCount(stock, resource), started); err != nil || handled {
				return result, err
			}
			ranked, err := policy.RankResourceCandidates(policy.ResourceDeficitDemand(resource, target-resourceCount(stock, resource)), policy.MineCandidates(resource, selected, domain.Known(sourceStorage.Capacity)), policy.AcquisitionCompetition{})
			if err != nil {
				return RoutineResourceResult{}, err
			}
			if r.outbid(goal, state, resource, ranked, reviewTick) {
				return RoutineResourceResult{Reason: BuildingMethodUsed}, nil
			}
		}
		result, _, err := r.acquireFromSources(call, epoch, state, goal, reviewTick, identity, resource, target, stock, started, pre)
		return result, err
	}
	// Both a bill and a deposit can cover the deficit (smelting against
	// mining compacted steel): the acquisition catalog ranks them by
	// estimated labor per unit and the cheaper runs (#728). A mine choice
	// that dispatches nothing falls back to the bill.
	if !beer && benchFilter == nil {
		remote, err := r.miningReach(call, state, reviewTick)
		if err != nil {
			return RoutineResourceResult{}, err
		}
		selected, sourceStorage, ok := r.sourcesForDeficit(call, identity, resource, target, stock, remote)
		deficit := target - resourceCount(stock, resource)
		if ok {
			if result, handled, err := r.storageFloor(call, epoch, state, goal, reviewTick, resource, sourceStorage, deficit, started); err != nil || handled && result.Reason == BuildingMethodAdmitted {
				return result, err
			}
		}
		candidates := policy.MineCandidates(resource, selected, domain.Known(sourceStorage.Capacity))
		if produce, found := policy.ProduceCandidate(choice, deficit); found {
			candidates = append(candidates, produce)
		}
		ranked, err := policy.RankResourceCandidates(policy.ResourceDeficitDemand(resource, deficit), candidates, policy.AcquisitionCompetition{})
		if err != nil {
			return RoutineResourceResult{}, err
		}
		if r.outbid(goal, state, resource, ranked, reviewTick) {
			return RoutineResourceResult{Reason: BuildingMethodUsed}, nil
		}
		if ok && len(ranked) > 0 && ranked[0].Kind == policy.AcquisitionMining {
			clockSchedulerLog("%s: %s mining %s scores %.3f over the bill", goal.Goal.ID, resource, ranked[0].ID, ranked[0].Score)
			result, dispatched, err := r.acquireFromSources(call, epoch, state, goal, reviewTick, identity, resource, target, stock, started, &sourceSelection{selected, sourceStorage})
			if err != nil || dispatched {
				return result, err
			}
		}
	}
	_, ok := tokens[choice.Bench]
	if !ok {
		return RoutineResourceResult{}, fmt.Errorf("%w: dispatchResourceGoal: !ok", ErrControl)
	}
	id := domain.MintPlanID("routine-resource")
	targetCount := int32(choice.Target)
	if int64(targetCount) != choice.Target {
		return RoutineResourceResult{}, fmt.Errorf("%w: dispatchResourceGoal: int64(targetCount) != choice.Target", ErrControl)
	}
	mode := domain.StockTarget
	if beer {
		mode = domain.BeerReserve
	}
	bill, err := domain.NewProductionBill(choice.Bench, choice.Recipe, mode, targetCount, ingredients...)
	if err != nil {
		return RoutineResourceResult{}, err
	}
	if choice.Replace != "" {
		bill, err = bill.ReplaceOwnedBill(choice.Replace)
		if err != nil {
			return RoutineResourceResult{}, err
		}
	}
	action, err := domain.NewProductionBillAction(domain.ActionID(fmt.Sprintf("%s-0", id)), bill)
	if err != nil {
		return RoutineResourceResult{}, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoutineResourceResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutineResourceResult{}, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoutineResourceResult{}, fmt.Errorf("%w: dispatchResourceGoal: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge", ErrControl)
	}
	if _, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, choice.ID, plan); err != nil {
		return RoutineResourceResult{}, err
	}
	return RoutineResourceResult{Reason: BuildingMethodAdmitted, Plan: id}, nil
}

// outbid posts this planner's best catalog score for resource on the joint
// board (#728) and reports whether the acquisition planner's chop, harvest
// or hunt bid beats it: the resource is then left to that planner.
func (r *RoutineResourcePlanner) outbid(goal store.GoalState, state ControlState, resource policy.Resource, ranked []policy.AcquisitionScore, tick domain.Tick) bool {
	var best policy.AcquisitionScore
	if len(ranked) > 0 {
		best = ranked[0]
	}
	rival, yield := r.reviewer.bids.bid(state.Snapshot, resource, bidResource, best.Score, best.Kind, tick)
	if yield {
		clockSchedulerLog("%s: %s %s %.3f yields to %s %.3f", goal.Goal.ID, resource, best.Kind, best.Score, rival.kind, rival.score)
	}
	return yield
}

// resourceCount is resource's units in a known census, 0 otherwise.
func resourceCount(stock domain.Fact[[]policy.Amount], resource policy.Resource) int64 {
	rows, _ := stock.Value()
	for _, row := range rows {
		if row.Resource == resource {
			return row.Count
		}
	}
	return 0
}

type sourceSelection struct {
	selected []policy.ResourceSource
	storage  policy.ResourceStorage
}

// acquireFromSources is the mine/harvest branch of dispatchResourceGoal:
// select sources for the deficit (or use pre, already selected), build
// storage when hauling them needs it, then dispatch a mine source.
// dispatched is true when a zone or a mine method was admitted or refused.
func (r *RoutineResourcePlanner) acquireFromSources(call, epoch context.Context, state ControlState, goal store.GoalState, reviewTick domain.Tick, identity *c.Identity, resource policy.Resource, target int64, stock domain.Fact[[]policy.Amount], started time.Time, pre *sourceSelection) (RoutineResourceResult, bool, error) {
	if pre == nil {
		remote, err := r.miningReach(call, state, reviewTick)
		if err != nil {
			return RoutineResourceResult{}, false, err
		}
		selected, storage, ok := r.sourcesForDeficit(call, identity, resource, target, stock, remote)
		if !ok {
			return RoutineResourceResult{Reason: BuildingMethodUsed}, false, nil
		}
		pre = &sourceSelection{selected, storage}
	}
	selected := pre.selected
	zoneResult, handled, err := r.materialStorageZoneFallback(call, epoch, state, goal, reviewTick, resource, selected, pre.storage, target-resourceCount(stock, resource), started)
	if err != nil {
		return RoutineResourceResult{}, false, err
	}
	if handled {
		zoneResult.Sources = selected
		return zoneResult, true, nil
	}
	result, dispatched, err := r.dispatchMineSource(call, epoch, state, goal, resource, selected, started)
	if err != nil {
		return RoutineResourceResult{}, false, err
	}
	if dispatched {
		result.Sources = selected
		return result, true, nil
	}
	return RoutineResourceResult{Reason: BuildingMethodUsed, Sources: selected}, false, nil
}

// sourcesForDeficit reads the resource's fresh native mine/harvest sources
// (and its always-populated StorageCapacity payload) and applies
// policy.SelectResourceSources against the outstanding deficit -- the first
// half of the resource method (its bill-listing
// fallback, which SelectResourceMethod above already covers, is only reached
// once this source loop finds nothing to select). Neither this method nor
// materialStorageZoneFallback dispatches AcquireResource itself --
// dispatchMineSource is what actually acts on the selection once storage is
// adequate -- so a native read failure here is deliberately swallowed
// (ok=false) rather than surfaced, preserving the bench/recipe outcome the
// caller already computed.
func (r *RoutineResourcePlanner) sourcesForDeficit(ctx context.Context, identity *c.Identity, resource policy.Resource, target int64, stock domain.Fact[[]policy.Amount], remote policy.RemoteWorkRequest) (selected []policy.ResourceSource, storage policy.ResourceStorage, ok bool) {
	rows, known := stock.Value()
	if !known {
		return nil, policy.ResourceStorage{}, false
	}
	var have int64
	for _, row := range rows {
		if row.Resource == resource {
			have = row.Count
			break
		}
	}
	if have >= target {
		return nil, policy.ResourceStorage{}, true
	}
	sources, storage, _, err := r.native.ReadResourceSources(ctx, identity, string(resource))
	if err != nil {
		return nil, policy.ResourceStorage{}, false
	}
	remote.Reach.StorageHeadroom = domain.Known(storage.Capacity)
	var holds []policy.RemoteWorkHold
	selected, holds = policy.SelectReachableResourceSources(sources, target, have, remote)
	if len(selected) == 0 && len(holds) > 0 {
		decision := policy.ResourceReach(remote.Reach)
		clockSchedulerLog("resource %s: %d sources held, reach=%s first=%s:%s stock=%d target=%d", resource, len(holds), decision.Stage, holds[0].Target, holds[0].Reason, have, target)
	}
	return selected, storage, true
}

// Mining uses the same observed readiness, colony extent and urgent-work
// competition as remote loot, with destination capacity from the exact
// resource's fresh source census.
func (r *RoutineResourcePlanner) miningReach(ctx context.Context, state ControlState, tick domain.Tick) (policy.RemoteWorkRequest, error) {
	last, _, err := r.reviewer.native.Identity(ctx)
	if err != nil {
		return policy.RemoteWorkRequest{}, err
	}
	expected, err := observation.DecodeIdentity(last)
	if err != nil {
		return policy.RemoteWorkRequest{}, err
	}
	if !routineBuildingBoundary(expected, state.Snapshot, tick) {
		return policy.RemoteWorkRequest{}, fmt.Errorf("%w: miningReach: !routineBuildingBoundary(expected, state.Snapshot, tick)", ErrControl)
	}
	reading, err := r.reviewer.observeOwned(ctx, r.reviewer.native, expected, domain.Unknown[[]policy.ConstructionClaim]())
	if err != nil {
		return policy.RemoteWorkRequest{}, err
	}
	f := reading.Projection.Facts
	f.ConstructionClaims, err = r.reviewer.player.journal.ConstructionClaims(ctx, state.Snapshot, expected.Tick)
	if err != nil {
		return policy.RemoteWorkRequest{}, err
	}
	emergency, err := policy.NewEmergencySnapshot(state.Snapshot, expected.Tick, reading.Emergency)
	if err != nil {
		return policy.RemoteWorkRequest{}, err
	}
	f.Hostiles, _ = policy.EmergencyNeeds(emergency, state.Snapshot, expected.Tick)
	f.UrgentPatients = policy.UrgentPatients(emergency, state.Snapshot, expected.Tick)
	extent, err := policy.DeriveColonyExtent(policy.ColonyExtentRequest{Bounds: f.MapBounds,
		Construction: f.CurrentConstruction, Claims: f.ConstructionClaims, Home: f.HomeCoverage})
	if err != nil {
		return policy.RemoteWorkRequest{}, err
	}
	request := policy.RemoteWorkRequest{Reach: policy.LootReach(f, f.MapBounds, extent), Competition: policy.RemoteCompetition(f)}
	layout, ok, err := r.reviewer.layoutPlan(ctx, state.Snapshot, expected.Tick)
	if err != nil {
		return policy.RemoteWorkRequest{}, err
	}
	if ok {
		request.Plan = domain.Known(layout.Plan)
	}
	return request, nil
}

// materialStorageZoneFallback is the resource method's
// storage branch: once the bench/recipe path can't fund the
// dynamically-selected resource, a fresh source selection that includes a
// "mine" source is checked against the same read's StorageCapacity payload
// (NativeResourceSourcesTool.Storage) to see whether hauling that source's
// yield needs a new covered stockpile zone (policy.SelectResourceStorageZone).
// It mirrors coveredStorageFallback's exact zone-build shape
// (allowListZone/PreviewZone/AdmitBuildingMethod, not
// CommitGoalMethod, since a zone carries footprint like a building), but the
// candidate cells come directly from native's own hauler-reachable, roofed,
// unreserved scan rather than policy.CoveredStorageSites -- no geometry is
// recomputed here. The zone's method ID is content-addressed by resource and
// cells (fingerprint dedup), not attempt-numbered, since
// the candidate set is whatever native reports fresh each call, not
// something this planner deliberately retries several times per episode.
// With no mine source to size it by, a deficit whose storage is full still
// gets one stack of capacity (#796), so remote salvage and loot can land.
// handled is false when nothing applies this tick (no mine source selected
// and capacity left, or existing capacity already covers the deficit) -- the caller should then
// fall through to dispatchMineSource instead: the storage zone-build takes
// the place of an acquisition action only when storage is inadequate.
func (r *RoutineResourcePlanner) materialStorageZoneFallback(call, epoch context.Context, state ControlState, goal store.GoalState, reviewTick domain.Tick, resource policy.Resource, selected []policy.ResourceSource, storage policy.ResourceStorage, deficit int64, started time.Time) (RoutineResourceResult, bool, error) {
	zone, needed, blocked, err := policy.SelectResourceStorageZone(selected, 0, storage)
	if err != nil {
		return RoutineResourceResult{}, false, err
	}
	if blocked {
		return RoutineResourceResult{Reason: BuildingMethodNoSpace}, true, nil
	}
	if !needed {
		if zone, needed, err = policy.SelectFullStorageZone(deficit, storage); err != nil || !needed {
			return RoutineResourceResult{}, false, err
		}
	}
	result, err := r.admitStorageZone(call, epoch, state, goal, reviewTick, resource, zone.Cells, started, "material-storage", "routine-resource-zone")
	return result, true, err
}

// storageFloor runs the full-storage floor (#796, policy.SelectFullStorageZone)
// ahead of every acquisition bid: a deficit whose accepting storage is full
// leaves bills, salvage, loot and trade nowhere to land, so remote salvage
// holds missing_storage forever, and a rival trade or deep-drill bid used to
// outrank this planner's zero-score mining bid before the storage branch was
// reached. Native's candidate cells already admit unroofed ground for a
// definition that does not deteriorate outdoors (DeteriorationRate 0: steel,
// plasteel, precious metals, uranium, jade, stone chunks and blocks), so this
// is the outdoor stockpile for those; a deteriorating resource gets roofed
// cells only. handled is true when a zone was admitted or refused.
func (r *RoutineResourcePlanner) storageFloor(call, epoch context.Context, state ControlState, goal store.GoalState, reviewTick domain.Tick, resource policy.Resource, storage policy.ResourceStorage, deficit int64, started time.Time) (RoutineResourceResult, bool, error) {
	zone, needed, err := policy.SelectFullStorageZone(deficit, storage)
	if err != nil || !needed {
		return RoutineResourceResult{}, false, err
	}
	clockSchedulerLog("%s: %s storage full under a %d deficit; stockpile on %d cells", goal.Goal.ID, resource, deficit, len(zone.Cells))
	result, err := r.admitStorageZone(call, epoch, state, goal, reviewTick, resource, zone.Cells, started, "material-storage", "routine-resource-zone")
	return result, true, err
}

// admitStorageZone admits one allow-listed stockpile zone for resource on
// cells (an already-selected connected footprint), the zone-build shape the
// material-storage and animal-feed delivery fallbacks share: cells under a
// held building reservation are dropped, the method ID is content-addressed
// by resource and cells (methodPrefix; fingerprint dedup reports
// BuildingMethodUsed), and the zone is previewed against the live zone-map
// token and admitted through AdmitBuildingMethod, since a zone carries
// footprint like a building.
func (r *RoutineResourcePlanner) admitStorageZone(call, epoch context.Context, state ControlState, goal store.GoalState, reviewTick domain.Tick, resource policy.Resource, footprint []domain.Cell, started time.Time, methodPrefix, planPrefix string) (RoutineResourceResult, error) {
	p := r.reviewer.player
	held, err := p.journal.BuildingReservations(call, state.Snapshot)
	if err != nil {
		return RoutineResourceResult{}, err
	}
	protected := map[domain.Cell]bool{}
	for _, h := range held {
		for _, cell := range h.Footprint {
			protected[cell] = true
		}
	}
	cells := make([]domain.Cell, 0, len(footprint))
	for _, cell := range footprint {
		if !protected[cell] {
			cells = append(cells, cell)
		}
	}
	if len(cells) == 0 {
		return RoutineResourceResult{Reason: BuildingMethodNoSpace}, nil
	}
	value, err := allowListZone(domain.ImportantPriority, []string{string(resource)}, cells)
	if err != nil {
		return RoutineResourceResult{}, err
	}
	hash := sha256.Sum256([]byte(fmt.Sprintf("%s/%v", resource, cells)))
	method := domain.MethodID(fmt.Sprintf("%s-%x", methodPrefix, hash[:16]))
	return admitZoneMethod(r.reviewer, r.native, call, epoch, state, goal, reviewTick, value, method, planPrefix, started)
}

// zoneMethodNative is the native surface a zone method admission needs: the
// identity and colony facts behind the zone-map token, and the zone preview.
type zoneMethodNative interface {
	observation.ColonySource
	FieldNative
}

// admitZoneMethod admits one already-shaped zone under goal as method: the
// method ID is the caller's (fingerprint dedup reports BuildingMethodUsed),
// the zone is previewed against the live zone-map token and admitted through
// AdmitBuildingMethod, since a zone carries footprint like a building.
func admitZoneMethod(reviewer *RoutineReviewer, native zoneMethodNative, call, epoch context.Context, state ControlState, goal store.GoalState, reviewTick domain.Tick, value domain.ZoneCreate, method domain.MethodID, planPrefix string, started time.Time) (RoutineResourceResult, error) {
	p := reviewer.player
	cells := value.Cells()
	if _, err := p.journal.LoadGoalMethod(call, goal.Goal.ID, goal.Goal.Epoch, method); err == nil {
		return RoutineResourceResult{Reason: BuildingMethodUsed}, nil
	} else if !errors.Is(err, store.ErrNotFound) {
		return RoutineResourceResult{}, err
	}
	id := domain.MintPlanID(planPrefix)
	last, _, err := native.Identity(call)
	if err != nil {
		return RoutineResourceResult{}, err
	}
	expected, err := observation.DecodeIdentity(last)
	if err != nil {
		return RoutineResourceResult{}, err
	}
	if !routineBuildingBoundary(expected, state.Snapshot, reviewTick) {
		return RoutineResourceResult{}, fmt.Errorf("%w: admitZoneMethod: !routineBuildingBoundary(expected, state.Snapshot, reviewTick)", ErrControl)
	}
	reading, err := reviewer.observeColony(call, native, expected, nil)
	if err != nil {
		return RoutineResourceResult{}, err
	}
	projection := reading.Projection
	token, known := projection.ZoneMapToken.Value()
	if !known {
		return RoutineResourceResult{Reason: BuildingMethodUnknown}, nil
	}
	snapshot := state.Snapshot
	snapshot.Plan = id
	snapshot.Revision = 1
	action, err := domain.NewZoneCreateAction(domain.ActionID(fmt.Sprintf("%s-0", id)), value)
	if err != nil {
		return RoutineResourceResult{}, err
	}
	reply, _, err := native.PreviewZone(call, boundary.Identity(snapshot), bridge.ZoneTarget{Zone: value, Token: token})
	if err != nil {
		return RoutineResourceResult{}, err
	}
	v := reply.GetEvaluated()
	if v == nil || !v.GetAccepted() {
		return RoutineResourceResult{Reason: BuildingMethodRefused}, nil
	}
	if _, err = boundary.Context(v.Context, snapshot); err != nil || domain.Tick(v.Context.GetTick()) != projection.Identity.Tick {
		return RoutineResourceResult{}, fmt.Errorf("%w: admitZoneMethod: err != nil || domain.Tick(v.Context.GetTick()) != projection.Identity.Tick", ErrControl)
	}
	preview := policy.Preview{Action: action, Snapshot: snapshot, Tick: projection.Identity.Tick, CanPlace: domain.Known(true), SafeToPlace: domain.Known(true), MadeFromStuff: domain.Known(false), WatchCellsAccessible: domain.Known(true), Footprint: domain.Known(cells), Costs: domain.Known([]policy.Amount{})}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoutineResourceResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutineResourceResult{}, err
	}
	elapsed := reviewer.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > reviewer.maxAge {
		return RoutineResourceResult{}, fmt.Errorf("%w: admitZoneMethod: p.session.State() != state || elapsed < 0 || elapsed > reviewer.maxAge", ErrControl)
	}
	decision, err := p.journal.AdmitBuildingMethod(call, store.BuildingMethodRequest{Goal: goal.Goal.ID, Revision: goal.Revision, Method: method, Plan: plan, Current: snapshot, Tick: projection.Identity.Tick, Bounds: domain.Known(projection.Bounds), Stock: policy.StockObservation{Snapshot: snapshot, Tick: projection.Identity.Tick}, Previews: []policy.Preview{preview}, Purpose: policy.Routine})
	if err != nil {
		return RoutineResourceResult{}, err
	}
	if !decision.Admitted {
		return RoutineResourceResult{Reason: BuildingMethodRefused}, nil
	}
	return RoutineResourceResult{Reason: BuildingMethodAdmitted, Plan: id}, nil
}

// dispatchMineSource actually dispatches a domain.MineAcquisitionAction
// against the selection's mine source, if any -- the second,
// independently-registered mine-acquisition vertical this planner's mine
// dispatch needs, since a mined resource can never appear in the generic
// vertical's AcquisitionFacts census. Only a mine
// method source carries the Cell/Token bridge.ReadMineAcquisition/
// AcquireResource need (policy.SelectResourceSources populates them for
// "mine" rows only); any other selected method is left to the caller's
// observability-only Sources reporting. The caller only reaches this once
// materialStorageZoneFallback reports handled=false, i.e. either no mine
// source was selected or its covered storage already suffices. dispatched is
// false, with a zero result and nil error, when there is nothing to dispatch
// -- the caller then falls back to its own BuildingMethodUsed reporting.
func (r *RoutineResourcePlanner) dispatchMineSource(call, epoch context.Context, state ControlState, goal store.GoalState, resource policy.Resource, sources []policy.ResourceSource, started time.Time) (RoutineResourceResult, bool, error) {
	var source policy.ResourceSource
	found := false
	for _, candidate := range sources {
		if candidate.Method == policy.ResourceSourceMine {
			source, found = candidate, true
			break
		}
	}
	if !found {
		return RoutineResourceResult{}, false, nil
	}
	p := r.reviewer.player
	acquisitionValue, err := domain.NewAcquisition(source.ThingID, string(resource), source.Cell)
	if err != nil {
		return RoutineResourceResult{}, false, err
	}
	digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%d/mine/%s/%d/%d", goal.Goal.ID, goal.Goal.Epoch, source.ThingID, source.Cell.X, source.Cell.Z)))
	id := domain.MintPlanID("routine-resource-mine")
	methodID := domain.MethodID(fmt.Sprintf("resource-mine-%x", digest[:16]))
	target := bridge.AcquisitionTarget{Acquisition: acquisitionValue, Token: source.Token}
	preview, _, err := r.native.PreviewAcquisition(call, boundary.Identity(state.Snapshot), target)
	if err != nil {
		return RoutineResourceResult{}, false, err
	}
	evaluated := preview.GetEvaluated()
	if evaluated == nil || !evaluated.GetAccepted() {
		return RoutineResourceResult{Reason: BuildingMethodRefused}, true, nil
	}
	if _, err = boundary.Context(evaluated.Context, state.Snapshot); err != nil {
		return RoutineResourceResult{}, false, fmt.Errorf("%w: dispatchMineSource: err != nil", ErrControl)
	}
	action, err := domain.NewMineAcquisitionAction(domain.ActionID(fmt.Sprintf("%s-0", id)), acquisitionValue)
	if err != nil {
		return RoutineResourceResult{}, false, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoutineResourceResult{}, false, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutineResourceResult{}, false, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoutineResourceResult{}, false, fmt.Errorf("%w: dispatchMineSource: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge", ErrControl)
	}
	if _, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, methodID, plan); err != nil {
		return RoutineResourceResult{}, false, err
	}
	return RoutineResourceResult{Reason: BuildingMethodAdmitted, Plan: id}, true, nil
}

// recipeIngredientNames lists, sorted, every ingredient alternative of the
// census recipes that produce product ("" for all recipes), for one
// ReadSupplyStock funding read. Category filters (any meat, any hay) make a
// single recipe name well over a hundred alternatives, so the list is cut
// at the read's bound; alternatives past it merely read as unknown stock.
func recipeIngredientNames(census []bridge.GearBenchRead, product policy.Resource) []string {
	ingredients := map[string]bool{}
	for _, row := range census {
		recipes, known := row.Bench.Recipes.Value()
		if !known {
			continue
		}
		for _, recipe := range recipes {
			if product != "" && !slices.Contains(recipe.Products, product) {
				continue
			}
			slots, known := recipe.Ingredients.Value()
			if !known {
				continue
			}
			for _, slot := range slots {
				for _, alt := range slot {
					ingredients[string(alt.Resource)] = true
				}
			}
		}
	}
	names := make([]string, 0, len(ingredients))
	for name := range ingredients {
		names = append(names, name)
	}
	sort.Strings(names)
	return names
}
