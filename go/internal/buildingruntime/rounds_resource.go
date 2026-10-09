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

// RoundsResourceSource is the native census RoundsResourcePlanner reads
// immediately before proposing a MaintainResource method. Like
// RoundsMedicalPlanner, it reads a fresh top-level resource stock census
// (not gated behind the planning flag) plus the same generic bench/recipe
// census and ingredient stock funding GearProduce/MaintainMedicalReserves
// already established. Native checks the bill against
// live state when the ProductionBillIntent applies.
//
// This planner covers the resource method's bench/recipe
// fallback branch (policy.SelectResourceMethod), the same bench-production
// path GearProduce/MaintainMedicalReserves dispatch through, plus its native
// mine-source acquisition branch (policy.SelectResourceSources). Whenever
// the bench/recipe path cannot fund the deficit and a fresh source selection
// includes a "mine" source, that source's storage is checked first
// (materialStorageBlocked): with no hauler or free cell the verdict is
// no_space; otherwise the warehouse and materials yard hold the yield. This
// planner then dispatches a
// domain.MineAcquisitionAction against the mine source through the second,
// independently-registered mine-acquisition vertical. Extraction development
// is still not dispatched here.
type RoundsResourceSource interface {
	observation.ColonySource
	ReadGearBenches(context.Context, *c.Identity) ([]bridge.GearBenchRead, bridge.Result, error)
	ReadSupplyStock(context.Context, *c.Identity, []string) ([]policy.Stock, bridge.Result, error)
	ReadResourceSources(context.Context, *c.Identity, string) ([]bridge.ResourceSourceRow, policy.ResourceStorage, bridge.Result, error)
	PreviewZone(context.Context, *c.Identity, domain.ZoneCreate) (*op.ZonePreviewReply, bridge.Result, error)
}
type RoundsResourcePlanner struct {
	reviewer *Rounder
	native   RoundsResourceSource
}
type RoundsResourceResult struct {
	Verdict
	Plan domain.PlanID
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
	// (materialStorageBlocked): a blocked store refuses no_space, otherwise
	// the mine source is dispatched (dispatchMineSource) -- see Reason/Plan
	// -- against the second, independently-registered mine-acquisition
	// vertical; any other selected method is still surfaced here for
	// observability only, since only mine sources carry the cell
	// this vertical's acquisition Designate needs. A native read failure
	// here is swallowed rather than propagated, since the bench/recipe
	// outcome above already stands on its own.
	Sources []policy.ResourceSource
}

func NewRoundsResourcePlanner(reviewer *Rounder, native RoundsResourceSource) (*RoundsResourcePlanner, error) {
	if reviewer == nil || native == nil {
		return nil, fmt.Errorf("%w: NewRoundsResourcePlanner: reviewer == nil || native == nil", ErrControl)
	}
	reviewer.resourceNative = native
	return &RoundsResourcePlanner{reviewer, native}, nil
}

// resourceStockFacts decodes the same generic top-level resource census
// observation.ColonyMedicalReserve reads (v.Resources), from a freshly read
// ColonyFactsSnapshot rather than the cached rounds snapshot.
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

// resourceSourceSubject names the awaiting_plan subject of a resource with no
// source this vertical can dispatch (a deposit to mine, a bench to produce at).
const resourceSourceSubject = "resource_source"

// noResourceSource is the refusal of a deficit whose resource has no source
// this planner can dispatch.
func noResourceSource(resource policy.Resource) Verdict {
	return awaitingPlan(resourceSourceSubject, string(resource))
}

// undispatched reports a target the step found nothing to dispatch for: the
// method was already used, a rival planner holds the claim, or no source
// stands. The next demanded resource then gets its turn.
func (r RoundsResourceResult) undispatched() bool {
	v := r.Verdict
	return v.Is(WaitMethodUsed) || v.Is(WaitClaim) || v.Is(RefusalAwaitingPlan) && v.Refusal.Subject == resourceSourceSubject
}

func (r *RoundsResourcePlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoundsResourceResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoundsResourceResult{Verdict: BuildingReasonDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoundsResourceResult{}, fmt.Errorf("%w: step: !state.ObservationKnown || state.Snapshot.Validate() != nil", ErrControl)
	}
	targets := r.reviewer.resourceTargets(state.Snapshot)
	if len(targets) == 0 {
		return RoundsResourceResult{Verdict: BuildingReasonDisabled}, nil
	}
	review, err := p.journal.LoadRounds(call)
	if err != nil {
		return RoundsResourceResult{}, err
	}
	if !review.Enabled || review.Snapshot != state.Snapshot {
		return RoundsResourceResult{Verdict: BuildingReasonNoReview}, nil
	}
	call, recorded := recordPlannerStep(call, policy.MaintainResource, state.Snapshot, review.Tick)
	defer recorded()
	goal, workable, err := p.journal.Workable(call, review, policy.MaintainResource)
	if err != nil {
		return RoundsResourceResult{}, err
	}
	if !workable {
		return RoundsResourceResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	// A tunnel stage held against geometry that changed under it closes so
	// the corridor is reviewed instead of waited on.
	if err := cancelStalledExcavation(call, p.journal, goal, review.Tick); err != nil {
		return RoundsResourceResult{}, err
	}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoundsResourceResult{}, err
		}
		if store.PlanOpen(plan) {
			requestOnly := len(plan.Spec.Actions()) != 0
			for _, action := range plan.Spec.Actions() {
				requestOnly = requestOnly && action.Kind() == domain.CommsTradeRequestAction
			}
			if requestOnly {
				continue
			}
			return RoundsResourceResult{Verdict: BuildingReasonExistingWork}, nil
		}
	}
	started := r.reviewer.clock.Now()
	identity := boundary.Identity(state.Snapshot)
	reply, _, err := r.native.ReadColonyFacts(call, identity, false)
	if err != nil {
		return RoundsResourceResult{}, err
	}
	observed := reply.GetObserved()
	if observed == nil {
		return RoundsResourceResult{}, fmt.Errorf("%w: step: observed == nil", ErrControl)
	}
	if err = bridge.ValidateColonyFacts(observed, identity); err != nil {
		return RoundsResourceResult{}, fmt.Errorf("%w: step: err != nil", ErrControl)
	}
	if _, err = boundary.Context(observed.Context, state.Snapshot); err != nil || observed.Context.GetTick() < int64(review.Tick) {
		return RoundsResourceResult{}, fmt.Errorf("%w: step: err != nil || observed.Context.GetTick() < int64(review.Tick)", ErrControl)
	}
	stock := resourceStockFacts(observed)
	if result, handled, err := r.deepDrill(call, epoch, state, goal, review, started); err != nil || handled {
		return result, err
	}
	targets = r.reviewer.resourceTargets(state.Snapshot)
	supply, err := r.reviewer.resourceSupply(call, state, review, goal)
	if err != nil {
		return RoundsResourceResult{}, err
	}
	ranked, err := policy.RankResourceTargets(policy.ResourceConcernTargets(targets, supply.derived), stock)
	if err != nil {
		return RoundsResourceResult{}, err
	}
	// Worst-covered floor first, but a floor whose only sources are ones
	// this vertical cannot dispatch (a harvest-only selection, nothing
	// reachable) must not starve the next demanded resource behind it:
	// keep going until a target admits a plan, lends a window, or
	// reports a real block. When nothing at all is dispatchable the first
	// target's outcome stands, so its selected sources stay observable.
	have := policy.StockReader{Resources: stock}
	var first *RoundsResourceResult
	for _, row := range ranked {
		var result RoundsResourceResult
		if row.Resource == "Beer" {
			result, err = r.dispatchResourceConcern(call, epoch, state, goal, review.Tick, identity, row.Resource, row.Target, stock, started)
		} else if have.Units(row.Resource) >= row.Target {
			continue
		} else {
			result, err = r.dispatchSupplied(call, epoch, state, goal, review.Tick, identity, supply, row.Resource, stock, started)
		}
		if err != nil {
			return RoundsResourceResult{}, err
		}
		if !result.undispatched() || result.Plan != "" || result.NativeWorkTicks != 0 {
			return result, nil
		}
		if first == nil {
			first = &result
		}
	}
	if first == nil {
		return RoundsResourceResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	return *first, nil
}

// methodChoice reads the bench census and the recipes' ingredient stock and
// selects the bill that would produce resource (policy.SelectResourceMethod).
// tokens receives each census bench's write token.
func (r *RoundsResourcePlanner) methodChoice(call context.Context, state ControlState, identity *c.Identity, goal store.StandardState, review store.Rounds, resource policy.Resource, target int64, stock domain.Fact[[]policy.Amount], tokens map[string]string) (choice policy.ResourceMethod, supply []policy.Stock, err error) {
	seen := make([]domain.MethodID, 0, len(goal.Methods))
	for _, method := range goal.Methods {
		seen = append(seen, method.Method)
	}
	census, _, err := r.native.ReadGearBenches(call, identity)
	if err != nil {
		return policy.ResourceMethod{}, nil, err
	}
	benches := make([]policy.GearBench, 0, len(census))
	for _, row := range census {
		benches = append(benches, row.Bench)
		tokens[row.Bench.ID] = row.Token
	}
	if names := recipeIngredientNames(census, resource); len(names) > 0 {
		if supply, _, err = r.native.ReadSupplyStock(call, identity, names); err != nil {
			return policy.ResourceMethod{}, nil, err
		}
	}
	request := policy.ResourceMethodRequest{Resource: resource, Target: target, Seen: seen, Benches: domain.Known(benches), Stock: supply}
	snap.NoteResourceMethod(call, request)
	choice, err = policy.SelectResourceMethod(request)
	return choice, supply, err
}

// dispatchResourceConcern is the bill tail of the beer reserve: one (resource,
// absolute stock floor) pair is produced at a bench
// (policy.SelectResourceMethod), or, with no bill that fits, mined from native
// sources (acquireFromSources). MaintainResource floors other than beer run on
// the Round's supply plan (dispatchSupplied).
func (r *RoundsResourcePlanner) dispatchResourceConcern(call, epoch context.Context, state ControlState, goal store.StandardState, reviewTick domain.Tick, identity *c.Identity, resource policy.Resource, target int64, stock domain.Fact[[]policy.Amount], started time.Time) (RoundsResourceResult, error) {
	beer := resource == "Beer"
	if beer {
		items, err := r.reviewer.itemFacts(call, state.Snapshot)
		if err != nil {
			return RoundsResourceResult{}, err
		}
		if items.Wort == "" {
			return RoundsResourceResult{}, fmt.Errorf("%w: dispatchResourceConcern: the catalog names no wort def", ErrControl)
		}
		resource = items.Wort
	}
	review, err := r.reviewer.player.journal.LoadRounds(call)
	if err != nil {
		return RoundsResourceResult{}, err
	}
	tokens := map[string]string{}
	choice, _, err := r.methodChoice(call, state, identity, goal, review, resource, target, stock, tokens)
	if err != nil {
		return RoundsResourceResult{}, err
	}
	if choice.Kind != policy.ResourceMethodProduce {
		if beer && choice.Kind == policy.ResourceMethodWait {
			return RoundsResourceResult{Verdict: BuildingReasonExistingWork, NativeWorkTicks: stockWaitTicks}, nil
		}
		result, _, err := r.acquireFromSources(call, epoch, state, goal, reviewTick, identity, resource, target, stock, started, nil)
		return result, err
	}
	return r.commitBill(call, epoch, state, goal, choice, tokens, beer, started)
}

// commitBill commits the production-bill method of a chosen bench and recipe.
func (r *RoundsResourcePlanner) commitBill(call, epoch context.Context, state ControlState, goal store.StandardState, choice policy.ResourceMethod, tokens map[string]string, beer bool, started time.Time) (RoundsResourceResult, error) {
	p := r.reviewer.player
	if _, ok := tokens[choice.Bench]; !ok {
		return RoundsResourceResult{}, fmt.Errorf("%w: commitBill: !ok", ErrControl)
	}
	id := domain.MintPlanID()
	targetCount := int32(choice.Target)
	if int64(targetCount) != choice.Target {
		return RoundsResourceResult{}, fmt.Errorf("%w: commitBill: int64(targetCount) != choice.Target", ErrControl)
	}
	mode := domain.StockTarget
	if beer {
		mode = domain.BeerReserve
	}
	bill, err := domain.NewProductionBill(choice.Bench, choice.Recipe, mode, targetCount)
	if err != nil {
		return RoundsResourceResult{}, err
	}
	if choice.Replace != "" {
		bill, err = bill.ReplaceOwnedBill(choice.Replace)
		if err != nil {
			return RoundsResourceResult{}, err
		}
	}
	action, err := domain.NewProductionBillAction(domain.ActionID(fmt.Sprintf("%s-0", id)), bill)
	if err != nil {
		return RoundsResourceResult{}, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoundsResourceResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoundsResourceResult{}, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoundsResourceResult{}, fmt.Errorf("%w: commitBill: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge", ErrControl)
	}
	if _, err = p.journal.CommitMethod(call, goal.Standard.ID, goal.Revision, choice.ID, plan); err != nil {
		return RoundsResourceResult{}, err
	}
	return RoundsResourceResult{Verdict: BuildingReasonAdmitted, Plan: id}, nil
}

// dispatchSupplied executes what the Round's supply plan opened for one
// MaintainResource floor: a deposit to mine, a bench bill to produce at, in the
// plan's rank order. A floor the
// plan opened nothing for for this planner's kinds is left to the acquisition
// planner when it opened a chop, harvest or hunt, to the trade planner when it
// opened a caravan's offer, and otherwise runs the mine tail (a buried deposit to tunnel
// to, a designation to wait on).
func (r *RoundsResourcePlanner) dispatchSupplied(call, epoch context.Context, state ControlState, goal store.StandardState, reviewTick domain.Tick, identity *c.Identity, supply *resourceSupply, resource policy.Resource, stock domain.Fact[[]policy.Amount], started time.Time) (RoundsResourceResult, error) {
	row := supply.rows[resource]
	if row == nil {
		return RoundsResourceResult{Verdict: noResourceSource(resource)}, nil
	}
	if !row.selKnown && row.choice.Kind != policy.ResourceMethodProduce {
		return RoundsResourceResult{Verdict: noResourceSource(resource)}, nil
	}
	mines := supply.plan.OpenedIDs(resource, policy.CandidateMining)
	for _, e := range supply.plan.Opened(resource) {
		switch e.Candidate.Kind {
		case policy.CandidateProduce:
			// The bill funds the units the plan admitted, which ingredient
			// draws may hold below the floor's deficit.
			choice := row.choice
			for _, credit := range e.Credit {
				if credit.Good.Def == resource {
					choice.Target = min(choice.Target, policy.StockReader{Resources: stock}.Units(resource)+int64(credit.Amount))
				}
			}
			return r.commitBill(call, epoch, state, goal, choice, supply.tokens, false, started)
		case policy.CandidateMining:
			selection := row.sel
			selection.selected = nil
			for _, source := range row.sel.selected {
				if mines[source.ThingID] {
					selection.selected = append(selection.selected, source)
				}
			}
			result, dispatched, err := r.acquireFromSources(call, epoch, state, goal, reviewTick, identity, resource, row.target, stock, started, &selection)
			if err != nil || dispatched {
				return result, err
			}
		}
	}
	if len(supply.acquisitions(resource)) > 0 || len(supply.plan.OpenedIDs(resource, policy.CandidateTrade)) > 0 {
		return RoundsResourceResult{Verdict: claimHeld(string(resource))}, nil
	}
	if row.busy {
		return RoundsResourceResult{Verdict: BuildingReasonExistingWork, NativeWorkTicks: stockWaitTicks}, nil
	}
	tail := row.sel
	tail.selected = nil
	result, _, err := r.acquireFromSources(call, epoch, state, goal, reviewTick, identity, resource, row.target, stock, started, &tail)
	return result, err
}

type sourceSelection struct {
	selected []policy.ResourceSource
	storage  policy.ResourceStorage
	// designated: a mine source already carries a designation that no
	// colonist has finished yet.
	designated bool
}

// acquireFromSources is the mine/harvest branch of dispatchResourceConcern:
// select sources for the deficit (or use pre, already selected), build
// storage when hauling them needs it, then dispatch a mine source.
// dispatched is true when a zone or a mine method was admitted or refused.
func (r *RoundsResourcePlanner) acquireFromSources(call, epoch context.Context, state ControlState, goal store.StandardState, reviewTick domain.Tick, identity *c.Identity, resource policy.Resource, target int64, stock domain.Fact[[]policy.Amount], started time.Time, pre *sourceSelection) (RoundsResourceResult, bool, error) {
	if pre == nil {
		remote, err := r.miningReach(call, state, reviewTick)
		if err != nil {
			return RoundsResourceResult{}, false, err
		}
		sel, ok := r.sourcesForDeficit(call, identity, resource, target, stock, remote)
		if !ok {
			return RoundsResourceResult{Verdict: noResourceSource(resource)}, false, nil
		}
		pre = &sel
	}
	selected := pre.selected
	zoneResult, handled, err := materialStorageBlocked(selected, pre.storage)
	if err != nil {
		return RoundsResourceResult{}, false, err
	}
	if handled {
		zoneResult.Sources = selected
		return zoneResult, true, nil
	}
	result, dispatched, err := r.dispatchMineSource(call, epoch, state, goal, resource, selected, started)
	if err != nil {
		return RoundsResourceResult{}, false, err
	}
	if !dispatched {
		result, dispatched, err = r.tunnelToBuriedOre(call, epoch, state, goal, reviewTick, identity, resource)
		if err != nil {
			return RoundsResourceResult{}, false, err
		}
	}
	if dispatched {
		result.Sources = selected
		return result, true, nil
	}
	// A deposit designated by an earlier, completed mine method is mined
	// by a colonist on game time alone: lend a window rather than
	// park the clock on no_work beside it.
	if pre.designated {
		return RoundsResourceResult{Verdict: BuildingReasonExistingWork, NativeWorkTicks: stockWaitTicks, Sources: selected}, false, nil
	}
	return RoundsResourceResult{Verdict: noResourceSource(resource), Sources: selected}, false, nil
}

// sourcesForDeficit reads the resource's fresh native mine/harvest sources
// (and its always-populated StorageCapacity payload) and applies
// policy.SelectResourceSources against the outstanding deficit -- the first
// half of the resource method (its bill-listing
// fallback, which SelectResourceMethod above already covers, is only reached
// once this source loop finds nothing to select). Neither this method nor
// materialStorageBlocked dispatches the mine itself --
// dispatchMineSource is what actually acts on the selection once storage is
// adequate -- so a native read failure here is deliberately swallowed
// (ok=false) rather than surfaced, preserving the bench/recipe outcome the
// caller already computed.
func (r *RoundsResourcePlanner) sourcesForDeficit(ctx context.Context, identity *c.Identity, resource policy.Resource, target int64, stock domain.Fact[[]policy.Amount], remote policy.RemoteWorkRequest) (sourceSelection, bool) {
	rows, known := stock.Value()
	if !known {
		return sourceSelection{}, false
	}
	var have int64
	for _, row := range rows {
		if row.Resource == resource {
			have = row.Count
			break
		}
	}
	if have >= target {
		return sourceSelection{}, true
	}
	sources, storage, _, err := r.native.ReadResourceSources(ctx, identity, string(resource))
	if err != nil {
		return sourceSelection{}, false
	}
	remote.Reach.StorageHeadroom = domain.Known(storage.Capacity)
	selected, _ := policy.SelectReachableResourceSources(sources, target, have, remote)
	out := sourceSelection{selected: selected, storage: storage}
	for _, s := range sources {
		out.designated = out.designated || s.Method == policy.ResourceSourceMine && s.Designated
	}
	return out, true
}

// Mining uses the same observed readiness, colony extent and urgent-work
// competition as remote loot, with destination capacity from the exact
// resource's fresh source census.
func (r *RoundsResourcePlanner) miningReach(ctx context.Context, state ControlState, tick domain.Tick) (policy.RemoteWorkRequest, error) {
	last, _, err := r.reviewer.native.Identity(ctx)
	if err != nil {
		return policy.RemoteWorkRequest{}, err
	}
	expected, err := observation.DecodeIdentity(last)
	if err != nil {
		return policy.RemoteWorkRequest{}, err
	}
	if !roundsBuildingBoundary(expected, state.Snapshot, tick) {
		return policy.RemoteWorkRequest{}, fmt.Errorf("%w: miningReach: !roundsBuildingBoundary(expected, state.Snapshot, tick)", ErrControl)
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
	_, f.UrgentPatients = policy.AmputationNeeds(emergency, f.MedicalPawns, domain.Unknown[int64](), f.UrgentPatients)
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

// materialStorageBlocked is the resource method's storage check: once the
// bench/recipe path can't fund the dynamically-selected resource, a fresh
// source selection that includes a "mine" source is checked against the same
// read's StorageCapacity payload (NativeResourceSourcesTool.Storage). Where
// no hauler or free storage cell can take the yield it refuses no_space; the
// warehouse and materials yard hold the stock otherwise, so no zone is made.
func materialStorageBlocked(selected []policy.ResourceSource, storage policy.ResourceStorage) (RoundsResourceResult, bool, error) {
	_, _, blocked, err := policy.SelectResourceStorageZone(selected, 0, storage)
	if err != nil || !blocked {
		return RoundsResourceResult{}, false, err
	}
	return RoundsResourceResult{Verdict: noSpace("material_storage")}, true, nil
}

// dispatchMineSource actually dispatches a domain.MineAcquisitionAction
// against the selection's mine source, if any -- the second,
// independently-registered mine-acquisition vertical this planner's mine
// dispatch needs, since a mined resource can never appear in the generic
// vertical's AcquisitionFacts census. Only a mine
// method source carries the cell the
// acquisition Designate needs (policy.SelectResourceSources populates them for
// "mine" rows only); any other selected method is left to the caller's
// observability-only Sources reporting. The caller only reaches this once
// materialStorageBlocked reports handled=false. dispatched is
// false, with a zero result and nil error, when there is nothing to dispatch
// -- the caller then falls back to its own WaitMethodUsed reporting.
func (r *RoundsResourcePlanner) dispatchMineSource(call, epoch context.Context, state ControlState, goal store.StandardState, resource policy.Resource, sources []policy.ResourceSource, started time.Time) (RoundsResourceResult, bool, error) {
	for _, source := range sources {
		if source.Method != policy.ResourceSourceMine {
			continue
		}
		p := r.reviewer.player
		acquisitionValue, err := domain.NewAcquisition(source.ThingID, string(resource), source.Cell)
		if err != nil {
			return RoundsResourceResult{}, false, err
		}
		// A rock the selection offers is not designated: an earlier method that
		// bound it dispatched and the mark is gone (native cancelled it, or it
		// was released), so a bound method id means a dead plan, not a live one.
		// Each retry binds under a fresh id; a rock whose earlier plans native
		// refused is left under the shared refusal budget and the next
		// source is tried.
		base := fmt.Sprintf("%s/%d/mine/%s/%d/%d", goal.Standard.ID, goal.Standard.Episode, source.ThingID, source.Cell.X, source.Cell.Z)
		mineMethod := func(attempt int) domain.MethodID {
			key := base
			if attempt > 0 {
				key = fmt.Sprintf("%s/%d", key, attempt)
			}
			digest := sha256.Sum256([]byte(key))
			return domain.MethodID(fmt.Sprintf("resource-mine-%x", digest[:16]))
		}
		plansByMethod := map[domain.MethodID]domain.PlanID{}
		for _, m := range goal.History {
			if m.Episode == goal.Standard.Episode {
				plansByMethod[m.Method] = m.Plan
			}
		}
		var earlier []domain.PlanID
		attempt := 0
		for ; ; attempt++ {
			plan, bound := plansByMethod[mineMethod(attempt)]
			if !bound {
				break
			}
			earlier = append(earlier, plan)
		}
		if _, ok, err := admitSubject(call, p.journal, base, earlier, state.Snapshot); err != nil {
			return RoundsResourceResult{}, false, err
		} else if !ok {
			continue
		}
		id := domain.MintPlanID()
		methodID := mineMethod(attempt)
		action, err := domain.NewMineAcquisitionAction(domain.ActionID(fmt.Sprintf("%s-0", id)), acquisitionValue)
		if err != nil {
			return RoundsResourceResult{}, false, err
		}
		plan, err := domain.NewPlan(id, 1, []domain.Action{action})
		if err != nil {
			return RoundsResourceResult{}, false, err
		}
		if err = p.current(call, epoch); err != nil {
			return RoundsResourceResult{}, false, err
		}
		elapsed := r.reviewer.clock.Now().Sub(started)
		if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
			return RoundsResourceResult{}, false, fmt.Errorf("%w: dispatchMineSource: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge", ErrControl)
		}
		_, err = p.journal.CommitMethod(call, goal.Standard.ID, goal.Revision, methodID, plan)
		if errors.Is(err, store.ErrMethodBound) {
			continue
		}
		if err != nil {
			return RoundsResourceResult{}, false, err
		}
		return RoundsResourceResult{Verdict: BuildingReasonAdmitted, Plan: id}, true, nil
	}
	return RoundsResourceResult{}, false, nil
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
