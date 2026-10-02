package buildingruntime

import (
	"context"
	"crypto/sha256"
	"errors"
	"fmt"
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
)

type RoutineFieldPlanner struct {
	reviewer *RoutineReviewer
	native   FieldNative
}
type RoutineFieldResult struct {
	NativeWorkTicks uint32
	Reason          RoutineBuildingReason
	Plan            domain.PlanID
}

type FieldNative interface {
	PreviewZone(context.Context, *c.Identity, domain.ZoneCreate) (*op.ZonePreviewReply, bridge.Result, error)
}

func NewRoutineFieldPlanner(reviewer *RoutineReviewer, native FieldNative) (*RoutineFieldPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, fmt.Errorf("%w: NewRoutineFieldPlanner: reviewer == nil || native == nil", ErrControl)
	}
	return &RoutineFieldPlanner{reviewer: reviewer, native: native}, nil
}
func (r *RoutineFieldPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoutineFieldResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutineFieldResult{Reason: BuildingMethodDisabled}, nil
	}
	if !state.ObservationKnown {
		return RoutineFieldResult{}, fmt.Errorf("%w: step: !state.ObservationKnown", ErrControl)
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutineFieldResult{}, err
	}
	if !review.Enabled || review.Snapshot != state.Snapshot {
		return RoutineFieldResult{Reason: BuildingMethodNoReview}, nil
	}
	goal, workable, err := p.journal.Workable(call, review, policy.EnsureFoodSupply)
	if err != nil {
		return RoutineFieldResult{}, err
	}
	idle := BuildingMethodNoDeficit
	if workable && goal.Goal.Priority >= 3 {
		selected := false
		for _, row := range review.Development.Rows {
			selected = selected || row.Goal == policy.EnsureFoodSupply && row.Selected
		}
		if !selected {
			workable, idle = false, BuildingMethodRefused
		}
	}
	// Open field work is budgeted against the food plan below. Infrastructure
	// with unknown output keeps its barrier; completed growers may be recropped.
	blocked := false
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoutineFieldResult{}, err
		}
		blocked = blocked || fieldBlockingWork(plan.Progress)
	}
	plans, err := p.journal.LoadPlans(call, 256)
	if err != nil {
		return RoutineFieldResult{}, err
	}
	playerPlans, err := p.journal.PlayerPlans(call, playerWorld(state.Snapshot))
	if err != nil {
		return RoutineFieldResult{}, err
	}
	definitions := append(routineProjectDefinitions(plans, state.Snapshot, playerPlans), "Plant_Rice", "Plant_Potato", "Plant_Corn", "Plant_Strawberry", "Plant_Toxipotato", "Plant_Nutrifungus", "Plant_Haygrass", "Plant_Hops", "Plant_Smokeleaf", "SunLamp", "HydroponicsBasin", "Heater")
	definitions = uniqueFieldDefinitions(definitions)
	expected, err := routineScope(call, r.reviewer.native)
	if err != nil {
		return RoutineFieldResult{}, err
	}
	if !routineBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoutineFieldResult{}, fmt.Errorf("%w: step: !routineBuildingBoundary(expected, state.Snapshot, review.Tick)", ErrControl)
	}
	claims, err := p.journal.ConstructionClaims(call, state.Snapshot, expected.Tick)
	if err != nil {
		return RoutineFieldResult{}, err
	}
	read, err := r.reviewer.observeOwned(call, r.reviewer.native, expected, claims, definitions...)
	if err != nil {
		return RoutineFieldResult{}, err
	}
	projection := read.Projection
	recordStepRead("field", policy.EnsureFoodSupply, state.Snapshot, projection)
	held, err := p.journal.BuildingReservations(call, state.Snapshot)
	if err != nil {
		return RoutineFieldResult{}, err
	}
	var protected []domain.Cell
	for _, h := range held {
		protected = append(protected, h.Footprint...)
	}
	var shells []store.PlanState
	for _, binding := range review.Goals {
		if binding.Need != policy.MaintainHousing {
			continue
		}
		shelter, err := p.journal.LoadGoal(call, binding.Goal)
		if err != nil {
			return RoutineFieldResult{}, err
		}
		for _, method := range shelter.Methods {
			plan, err := p.journal.LoadPlan(call, method.Plan)
			if err != nil {
				return RoutineFieldResult{}, err
			}
			shells = append(shells, plan)
		}
	}
	claimed, _ := claims.Value()
	protected = append(protected, shellInteriors(shells, claimed)...)
	ring, err := firebreakRing(projection)
	if err != nil {
		return RoutineFieldResult{}, err
	}
	protected = append(protected, ring...)
	// The cross-crop ledger (#1308): hay and social shortfalls compete with
	// the food block for the plan's field patches in one ranked order.
	others, err := r.otherFieldShortfalls(call, review, projection)
	if err != nil {
		return RoutineFieldResult{}, err
	}
	anchor := layoutAnchor(projection, policy.DistrictFields)
	placeOthers := func(wait uint32, reason RoutineBuildingReason) (RoutineFieldResult, error) {
		result, tried, err := r.placeLedger(call, epoch, state, projection, read, wait, others, anchor, protected)
		if err != nil || tried {
			return result, err
		}
		return RoutineFieldResult{Reason: reason, NativeWorkTicks: wait}, nil
	}
	if !workable {
		return placeOthers(0, idle)
	}
	if result, handled, err := r.fishing(call, epoch, state, goal, read); err != nil || handled {
		return result, err
	}
	if !foodPlanSupport(projection.Facts.FoodPlan, policy.FoodCrop, "field-capacity") {
		return placeOthers(0, BuildingMethodUnknown)
	}
	if blocked {
		openPlans := []store.PlanState{}
		for _, method := range goal.Methods {
			plan, err := p.journal.LoadPlan(call, method.Plan)
			if err != nil {
				return RoutineFieldResult{}, err
			}
			openPlans = append(openPlans, plan)
		}
		blocked = !foodPlanAdditionalField(projection, openPlans)
	}
	wait, err := r.fieldAllowance(call, goal.Goal, state.Snapshot, projection)
	if err != nil {
		return RoutineFieldResult{}, err
	}
	reserveDays := r.reviewer.seasonal(projection.Facts).FoodTargetDays
	request, choices := fieldSiteRequest(projection, protected, reserveDays)
	selection, known := policy.PlanSiteType(request)
	if env, known := projection.Environment.Value(); known {
		clockSchedulerLog("Fields environment: lights=%d growers=%d rooms=%d networks=%d daylight=%v outdoorC=%v", len(env.Lights), len(env.Growers), len(env.Rooms), len(env.Networks), env.Daylight, env.OutdoorTemperatureC)
	} else {
		clockSchedulerLog("Fields environment: unknown")
	}
	// Existing growers first: a basin sows its definition's default crop
	// when built, so the crop the candidate scored is applied here once the
	// game reports the grower, and a better crop re-crops it the same way.
	if env, ok := projection.Environment.Value(); ok {
		recrops := policy.PlanGrowerCrops(policy.GrowerCropRequest{Choices: choices, Growers: env.Growers, Urgent: selection.Urgent, Field: request.Field})
		for _, recrop := range recrops {
			result, tried, err := r.recrop(call, epoch, state, goal, projection, read, wait, recrop, arbiter)
			if err != nil || tried {
				return result, err
			}
		}
	}
	if blocked {
		return placeOthers(wait, BuildingMethodExistingWork)
	}
	// A crop whose zones outgrew its full target gives up bare cells
	// first (#1309): the shortfall planners go quiet once covered.
	if result, handled, err := r.shrink(call, epoch, state, goal, projection, read, request.Field); err != nil || handled {
		return result, err
	}
	if !known {
		clockSchedulerLog("Fields: no plan (cells=%d choices=%d climate=%+v runway=%+v colonists=%+v coverage=%+v zones=%d): %s", len(projection.Cells), len(choices), projection.CropClimate, projection.Facts.FoodDays, projection.Facts.Colonists, request.Field.Coverage, len(projection.Farms), selection.Explain())
		return placeOthers(wait, BuildingMethodUnknown)
	}
	// The winner's cells: basin kinds carry them on the candidate, not a site plan.
	clockSchedulerLog("Fields select: kind=%s crop=%s cells=%d buildings=%d | %s", selection.Kind, selection.Crop.Name, selection.Candidates[0].Cells, len(selection.Buildings), selection.Explain())
	// Candidates are tried in score order; a construction kind whose
	// placements the game refuses, or whose costs the store cannot reserve,
	// falls through to the next plantable candidate within the same step.
	attempts := 0
	for _, candidate := range selection.Candidates {
		if candidate.Cells == 0 || attempts >= fieldCandidateAttempts {
			break
		}
		attempts++
		// Outdoor soil fields fill the plan's field blocks (#1223), ranked
		// against the hay and social shortfalls (#1308).
		var result RoutineFieldResult
		var tried bool
		var err error
		if candidate.Kind == policy.SiteOutdoor && len(candidate.Buildings) == 0 {
			food := fieldShortfall{Goal: goal, Options: fieldBlockOptions(candidate, selection.Candidates), What: "food"}
			result, tried, err = r.placeLedger(call, epoch, state, projection, read, wait, append([]fieldShortfall{food}, others...), request.Field.Site.Anchor, protected)
			others = nil
		} else {
			result, tried, err = r.enact(call, epoch, state, goal, projection, read, wait, candidate)
		}
		if err != nil || tried {
			return result, err
		}
		clockSchedulerLog("Fields: %s %s refused (%s), trying next candidate", candidate.Kind, candidate.Crop.Name, result.Reason)
	}
	return placeOthers(wait, BuildingMethodRefused)
}

// otherFieldShortfalls is the hay and social field demand of this step's
// read (#1308), each under its own goal: hay under MaintainAnimalFeed,
// social crops under MaintainResource once brewing is researched. A goal
// with open work waits for it.
func (r *RoutineFieldPlanner) otherFieldShortfalls(call context.Context, review store.RoutineReview, projection observation.ColonyProjection) ([]fieldShortfall, error) {
	p := r.reviewer.player
	ready := func(kind policy.GoalID) (store.GoalState, bool, error) {
		goal, workable, err := p.journal.Workable(call, review, kind)
		if err != nil || !workable {
			return goal, false, err
		}
		for _, method := range goal.Methods {
			plan, err := p.journal.LoadPlan(call, method.Plan)
			if err != nil {
				return goal, false, err
			}
			if store.PlanOpen(plan) {
				return goal, false, nil
			}
		}
		return goal, true, nil
	}
	var out []fieldShortfall
	if opt, ok := hayShortfall(projection); ok {
		goal, ready, err := ready(policy.MaintainAnimalFeed)
		if err != nil {
			return nil, err
		}
		if ready {
			out = append(out, fieldShortfall{Goal: goal, Options: []policy.FieldBlockOption{opt}, What: "hay"})
		}
	}
	if review.BrewingFinished && policy.BrewingFinished(projection.Facts.Research) {
		if opts, known := socialShortfalls(projection); known && len(opts) > 0 {
			goal, ready, err := ready(policy.MaintainResource)
			if err != nil {
				return nil, err
			}
			for _, opt := range opts {
				if ready {
					out = append(out, fieldShortfall{Goal: goal, Options: []policy.FieldBlockOption{opt}, What: "social"})
				}
			}
		}
	}
	return out, nil
}

// placeLedger places the next field block of the heaviest shortfall that
// still fits (#1308); tried reports a block reached commitment.
func (r *RoutineFieldPlanner) placeLedger(call, epoch context.Context, state ControlState, projection observation.ColonyProjection, read observation.RoutineReading, wait uint32, ledger []fieldShortfall, anchor domain.Cell, protected []domain.Cell) (RoutineFieldResult, bool, error) {
	result := RoutineFieldResult{Reason: BuildingMethodNoDeficit, NativeWorkTicks: wait}
	for _, s := range rankFieldShortfalls(ledger) {
		if len(s.Options) == 0 {
			continue
		}
		lead := s.Options[0]
		clockSchedulerLog("Fields ledger: %s %s needs %d (priority %d)", s.What, lead.Crop.Name, lead.Needed, s.Goal.Goal.Priority)
		var tried bool
		var err error
		result, tried, err = r.enactBlock(call, epoch, state, s.Goal, projection, read, wait, policy.SiteTypeCandidate{Kind: policy.SiteOutdoor, Crop: lead.Crop, Needed: lead.Needed}, s.Options, anchor, protected)
		if err != nil || tried {
			return result, tried, err
		}
	}
	return result, false, nil
}

const (
	// fieldLampGrowthRadius is the native Building_SunLamp growth radius.
	fieldLampGrowthRadius = 5.8
	// fieldCandidateAttempts bounds the native previews one step spends
	// falling through refused candidates.
	fieldCandidateAttempts = 3
)

// enact previews and admits one candidate. tried reports whether the
// candidate reached admission (admitted or refused by the store); a candidate
// the game refuses to place is not tried so the caller can move on.
func (r *RoutineFieldPlanner) enact(call, epoch context.Context, state ControlState, goal store.GoalState, projection observation.ColonyProjection, read observation.RoutineReading, wait uint32, candidate policy.SiteTypeCandidate) (RoutineFieldResult, bool, error) {
	p := r.reviewer.player
	crop := candidate.Crop
	hash := sha256.New()
	fmt.Fprintf(hash, "%s/%s/%v/%v", candidate.Kind, crop.Name, candidate.Sites.Patches, candidate.Buildings)
	method := domain.MethodID(fmt.Sprintf("fields-%x", hash.Sum(nil)[:16]))
	if _, err := p.journal.LoadGoalMethod(call, goal.Goal.ID, goal.Goal.Epoch, method); err == nil {
		return RoutineFieldResult{Reason: BuildingMethodUsed, NativeWorkTicks: wait}, true, nil
	} else if !errors.Is(err, store.ErrNotFound) {
		return RoutineFieldResult{}, false, err
	}
	id := domain.MintPlanID()
	snapshot := state.Snapshot
	snapshot.Plan = id
	snapshot.Revision = 1
	stock := policy.StockObservation{Snapshot: snapshot, Tick: projection.Identity.Tick}
	var actions []domain.Action
	var previews []policy.Preview
	if len(candidate.Buildings) > 0 {
		// Infrastructure first: the lamp or basins are the whole batch, and
		// the lit soil is planted by a later step once the game reports it.
		native, ok := r.native.(interface {
			PreviewBuilding(context.Context, domain.Action, domain.GenerationSnapshot) (bridge.BuildingPreview, bridge.Result, error)
		})
		if !ok {
			return RoutineFieldResult{Reason: BuildingMethodRefused, NativeWorkTicks: wait}, false, nil
		}
		buildings := candidate.Buildings
		if len(buildings) > fieldBatchPatches {
			buildings = buildings[:fieldBatchPatches]
		}
		// The batch stops at what the observed stock can pay for: admission
		// reserves the whole batch or none, and a basin count sized by lit
		// floor and night headroom usually outruns the colony's steel.
		spent := map[policy.Resource]int64{}
		for i, b := range buildings {
			building, err := domain.NewBuilding(b.Definition, b.Cell, b.Rotation, "")
			if err != nil {
				return RoutineFieldResult{}, false, err
			}
			action, err := domain.NewBuildingAction(domain.ActionID(fmt.Sprintf("%s-%d", id, i)), building)
			if err != nil {
				return RoutineFieldResult{}, false, err
			}
			preview, _, err := native.PreviewBuilding(call, action, snapshot)
			if err != nil {
				return RoutineFieldResult{}, false, err
			}
			v := preview.Preview
			legal, lk := v.CanPlace.Value()
			safe, sk := v.SafeToPlace.Value()
			if !lk || !sk {
				return RoutineFieldResult{Reason: BuildingMethodUnknown, NativeWorkTicks: wait}, false, nil
			}
			if !legal || !safe {
				return RoutineFieldResult{Reason: BuildingMethodNoSpace, NativeWorkTicks: wait}, false, nil
			}
			if err = mergeRoutineStock(&stock, preview.Stock, i == 0); err != nil {
				return RoutineFieldResult{}, false, err
			}
			if i > 0 && !fieldAffordable(spent, v, preview.Stock) {
				break
			}
			for _, cost := range fieldCosts(v) {
				spent[cost.Resource] += cost.Count
			}
			actions = append(actions, action)
			previews = append(previews, v)
		}
	} else {
		// Each patch costs one native preview inside the shared step budget,
		// so a large plan is committed over several batches; the next batch
		// starts once this one's zone work has completed and the coverage
		// facts have moved.
		patches := candidate.Sites.Patches
		if len(patches) > fieldBatchPatches {
			patches = patches[:fieldBatchPatches]
		}
		for i, patch := range patches {
			var cells []domain.Cell
			for x := patch.X; x < patch.X+patch.Width; x++ {
				for z := patch.Z; z < patch.Z+patch.Height; z++ {
					cells = append(cells, domain.Cell{X: x, Z: z})
				}
			}
			value, err := domain.NewZoneCreate(domain.GrowingZone, crop.Name, cells)
			if err != nil {
				return RoutineFieldResult{}, false, err
			}
			action, err := domain.NewZoneCreateAction(domain.ActionID(fmt.Sprintf("%s-%d", id, i)), value)
			if err != nil {
				return RoutineFieldResult{}, false, err
			}
			reply, refused, err := previewZone(call, r.native, boundary.Identity(snapshot), value)
			if err != nil {
				return RoutineFieldResult{}, false, err
			}
			if refused != "" {
				clockSchedulerLog("Fields: %s patch %+v refused: %s", crop.Name, patch, refused)
				return RoutineFieldResult{Reason: BuildingMethodRefused, NativeWorkTicks: wait}, false, nil
			}
			v := reply.GetEvaluated()
			if _, err = boundary.Context(v.Context, snapshot); err != nil || domain.Tick(v.Context.GetTick()) < projection.Identity.Tick {
				return RoutineFieldResult{}, false, fmt.Errorf("%w: enact: err != nil || domain.Tick(v.Context.GetTick()) < projection.Identity.Tick", ErrControl)
			}
			actions = append(actions, action)
			previews = append(previews, policy.Preview{Action: action, Snapshot: snapshot, Tick: projection.Identity.Tick, CanPlace: domain.Known(true), SafeToPlace: domain.Known(true), MadeFromStuff: domain.Known(false), WatchCellsAccessible: domain.Known(true), Footprint: domain.Known(cells), Costs: domain.Known([]policy.Amount{})})
		}
	}
	return r.admit(call, epoch, state, goal, projection, read, wait, method, id, snapshot, stock, actions, previews, string(candidate.Kind)+" "+crop.Name)
}

// admit admits one field method's actions with their previews.
func (r *RoutineFieldPlanner) admit(call, epoch context.Context, state ControlState, goal store.GoalState, projection observation.ColonyProjection, read observation.RoutineReading, wait uint32, method domain.MethodID, id domain.PlanID, snapshot domain.GenerationSnapshot, stock policy.StockObservation, actions []domain.Action, previews []policy.Preview, what string) (RoutineFieldResult, bool, error) {
	p := r.reviewer.player
	plan, err := domain.NewPlan(id, 1, actions)
	if err != nil {
		return RoutineFieldResult{}, false, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutineFieldResult{}, false, err
	}
	if p.session.State() != state {
		return RoutineFieldResult{}, false, fmt.Errorf("%w: enact: p.session.State() != state", ErrControl)
	}
	actual, err := routineScope(call, r.reviewer.native)
	if err != nil || !routineBuildingBoundary(actual, state.Snapshot, projection.Identity.Tick) {
		return RoutineFieldResult{}, false, fmt.Errorf("%w: enact: err != nil || !routineBuildingBoundary(actual, state.Snapshot, projection.Identity.Tick)", ErrControl)
	}
	now := r.reviewer.clock.Now()
	if now.Before(read.StartedAt) || now.Sub(read.StartedAt) > r.reviewer.maxAge {
		return RoutineFieldResult{}, false, observation.ErrStale
	}
	decision, err := admitMethod(call, p.journal, store.BuildingMethodRequest{Goal: goal.Goal.ID, Revision: goal.Revision, Method: method, Plan: plan, Current: snapshot, Tick: projection.Identity.Tick, Bounds: domain.Known(projection.Bounds), Stock: stock, Previews: previews, Purpose: policy.Routine})
	if err != nil {
		return RoutineFieldResult{}, false, err
	}
	if !decision.Admitted {
		clockSchedulerLog("Fields: %s not admitted: %+v", what, decision.Refused)
		return RoutineFieldResult{Reason: BuildingMethodRefused, NativeWorkTicks: wait}, false, nil
	}
	return RoutineFieldResult{Reason: BuildingMethodAdmitted, Plan: id}, true, nil
}

const fieldBatchPatches = 6

// enactBlock takes the next plan field block step (#1223): create the
// block's growing zone, or grow it with add-cells until the block is full.
// No plan field blocks is a refusal with its reason; nothing is sited
// outside the plan.
func (r *RoutineFieldPlanner) enactBlock(call, epoch context.Context, state ControlState, goal store.GoalState, projection observation.ColonyProjection, read observation.RoutineReading, wait uint32, candidate policy.SiteTypeCandidate, options []policy.FieldBlockOption, anchor domain.Cell, protected []domain.Cell) (RoutineFieldResult, bool, error) {
	p := r.reviewer.player
	edit, reason, ok := planFieldBlock(projection, anchor, options, protected)
	if !ok {
		clockEvent(call, "layout", "fields", "outdoor field refused: "+reason, "crop", candidate.Crop.Name)
		return RoutineFieldResult{Reason: BuildingMethodNoSpace, NativeWorkTicks: wait}, false, nil
	}
	hash := sha256.New()
	fmt.Fprintf(hash, "block/%s/%s/%v", edit.Zone, edit.Crop, edit.Cells)
	method := domain.MethodID(fmt.Sprintf("fields-%x", hash.Sum(nil)[:16]))
	if _, err := p.journal.LoadGoalMethod(call, goal.Goal.ID, goal.Goal.Epoch, method); err == nil {
		return RoutineFieldResult{Reason: BuildingMethodUsed, NativeWorkTicks: wait}, true, nil
	} else if !errors.Is(err, store.ErrNotFound) {
		return RoutineFieldResult{}, false, err
	}
	id := domain.MintPlanID()
	snapshot := state.Snapshot
	snapshot.Plan = id
	snapshot.Revision = 1
	if edit.Zone != "" {
		// Growing a standing zone needs no worker or reservation, like a
		// stockpile grow: committed directly.
		value, err := domain.NewZoneCellEdit(edit.Zone, domain.AddZoneCells, edit.Cells)
		if err != nil {
			return RoutineFieldResult{}, false, err
		}
		action, err := domain.NewZoneCellEditAction(domain.ActionID(fmt.Sprintf("%s-0", id)), value)
		if err != nil {
			return RoutineFieldResult{}, false, err
		}
		plan, err := domain.NewPlan(id, 1, []domain.Action{action})
		if err != nil {
			return RoutineFieldResult{}, false, err
		}
		if err = p.current(call, epoch); err != nil {
			return RoutineFieldResult{}, false, err
		}
		now := r.reviewer.clock.Now()
		if p.session.State() != state || now.Before(read.StartedAt) || now.Sub(read.StartedAt) > r.reviewer.maxAge {
			return RoutineFieldResult{}, false, fmt.Errorf("%w: enactBlock: p.session.State() != state || now.Before(read.StartedAt) || now.Sub(read.StartedAt) > r.reviewer.maxAge", ErrControl)
		}
		if _, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, method, plan); err != nil {
			return RoutineFieldResult{}, false, err
		}
		clockEvent(call, "layout", "fields", "field block grown", "zone", edit.Zone, "crop", edit.Crop, "cells", len(edit.Cells), "plan", string(id))
		return RoutineFieldResult{Reason: BuildingMethodAdmitted, Plan: id}, true, nil
	}
	value, err := domain.NewZoneCreate(domain.GrowingZone, edit.Crop, edit.Cells)
	if err != nil {
		return RoutineFieldResult{}, false, err
	}
	action, err := domain.NewZoneCreateAction(domain.ActionID(fmt.Sprintf("%s-0", id)), value)
	if err != nil {
		return RoutineFieldResult{}, false, err
	}
	reply, refused, err := previewZone(call, r.native, boundary.Identity(snapshot), value)
	if err != nil {
		return RoutineFieldResult{}, false, err
	}
	if refused != "" {
		clockSchedulerLog("Fields: %s block zone refused: %s", edit.Crop, refused)
		return RoutineFieldResult{Reason: BuildingMethodRefused, NativeWorkTicks: wait}, false, nil
	}
	v := reply.GetEvaluated()
	if _, err = boundary.Context(v.Context, snapshot); err != nil || domain.Tick(v.Context.GetTick()) < projection.Identity.Tick {
		return RoutineFieldResult{}, false, fmt.Errorf("%w: enactBlock: err != nil || domain.Tick(v.Context.GetTick()) < projection.Identity.Tick", ErrControl)
	}
	stock := policy.StockObservation{Snapshot: snapshot, Tick: projection.Identity.Tick}
	preview := policy.Preview{Action: action, Snapshot: snapshot, Tick: projection.Identity.Tick, CanPlace: domain.Known(true), SafeToPlace: domain.Known(true), MadeFromStuff: domain.Known(false), WatchCellsAccessible: domain.Known(true), Footprint: domain.Known(edit.Cells), Costs: domain.Known([]policy.Amount{})}
	return r.admit(call, epoch, state, goal, projection, read, wait, method, id, snapshot, stock, []domain.Action{action}, []policy.Preview{preview}, "block "+edit.Crop)
}

// recrop commits one grower's crop change as a one-shot
// GrowerCrop plan, once per grower per goal epoch: a method that already
// ran this epoch (the patch was refused, or a player changed the crop back)
// is not retried until the next epoch. tried reports whether the grower
// reached commitment; a grower whose native read refuses is not
// tried so the caller moves on to the next one.
func (r *RoutineFieldPlanner) recrop(call, epoch context.Context, state ControlState, goal store.GoalState, projection observation.ColonyProjection, read observation.RoutineReading, wait uint32, choice policy.GrowerCropChoice, arbiter *stepArbiter) (RoutineFieldResult, bool, error) {
	native, ok := r.native.(interface {
		ReadGrowerCropTarget(context.Context, *c.Identity, string) (bridge.GrowerCropTarget, bridge.Result, error)
	})
	if !ok {
		return RoutineFieldResult{Reason: BuildingMethodRefused, NativeWorkTicks: wait}, false, nil
	}
	p := r.reviewer.player
	method := domain.MethodID("fields-recrop-" + choice.Grower)
	if _, err := p.journal.LoadGoalMethod(call, goal.Goal.ID, goal.Goal.Epoch, method); err == nil {
		return RoutineFieldResult{Reason: BuildingMethodUsed, NativeWorkTicks: wait}, false, nil
	} else if !errors.Is(err, store.ErrNotFound) {
		return RoutineFieldResult{}, false, err
	}
	target, _, err := native.ReadGrowerCropTarget(call, boundary.Identity(state.Snapshot), choice.Grower)
	if err != nil {
		return RoutineFieldResult{}, false, err
	}
	if _, err = boundary.Context(target.Context, state.Snapshot); err != nil || target.Context.GetTick() < int64(projection.Identity.Tick) {
		return RoutineFieldResult{}, false, fmt.Errorf("%w: recrop: err != nil || target.Context.GetTick() < int64(projection.Identity.Tick)", ErrControl)
	}
	if target.Crop != choice.Current {
		clockSchedulerLog("Fields: grower %s crop moved (%s -> %s) since the census", choice.Grower, choice.Current, target.Crop)
		return RoutineFieldResult{Reason: BuildingMethodUnknown, NativeWorkTicks: wait}, false, nil
	}
	patch, err := domain.NewGrowerCrop(choice.Grower, choice.Crop.Name)
	if err != nil {
		return RoutineFieldResult{}, false, err
	}
	// Native checks the grower and crop live when the BuildingPatchIntent
	// applies (#940); a refusal comes back on the plan, not here.
	if !arbiter.tryClaim(nil, "grower:"+choice.Grower) {
		return RoutineFieldResult{Reason: BuildingMethodUsed, NativeWorkTicks: wait}, false, nil
	}
	id := domain.MintPlanID()
	action, err := domain.NewGrowerCropAction(domain.ActionID(fmt.Sprintf("%s-0", id)), patch)
	if err != nil {
		return RoutineFieldResult{}, false, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoutineFieldResult{}, false, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutineFieldResult{}, false, err
	}
	now := r.reviewer.clock.Now()
	if p.session.State() != state || now.Before(read.StartedAt) || now.Sub(read.StartedAt) > r.reviewer.maxAge {
		return RoutineFieldResult{}, false, fmt.Errorf("%w: recrop: p.session.State() != state || now.Before(read.StartedAt) || now.Sub(read.StartedAt) > r.reviewer.maxAge", ErrControl)
	}
	clockSchedulerLog("Fields recrop: grower=%s %s -> %s | %s", choice.Grower, choice.Current, choice.Crop.Name, choice.Reason)
	if _, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, method, plan); err != nil {
		return RoutineFieldResult{}, false, err
	}
	return RoutineFieldResult{Reason: BuildingMethodAdmitted, Plan: id}, true, nil
}

func fieldCosts(v policy.Preview) []policy.Amount {
	costs, _ := v.Costs.Value()
	return costs
}

// fieldAffordable reports whether the preview's costs fit the stock its own
// scan observed once the batch's earlier placements are paid for; an unknown
// availability never refuses here, admission decides that.
func fieldAffordable(spent map[policy.Resource]int64, v policy.Preview, stock policy.StockObservation) bool {
	available := map[policy.Resource]domain.Fact[int64]{}
	for _, s := range stock.Values {
		available[s.Resource] = s.Available
	}
	for _, cost := range fieldCosts(v) {
		if have, known := available[cost.Resource].Value(); known && spent[cost.Resource]+cost.Count > have {
			return false
		}
	}
	return true
}

// fieldBlockingWork is open zone or farm-infrastructure work under the goal:
// a lamp still under construction must light its soil before the next batch.
func fieldBlockingWork(progress []domain.Progress) bool {
	for _, p := range progress {
		_, zone := p.Action().ZoneCreate()
		_, grow := p.Action().ZoneCellEdit()
		zone = zone || grow
		building, isBuilding := p.Action().Building()
		// The butcher spot shares the goal but not the field (#260).
		if isBuilding && building.Definition() == "ButcherSpot" {
			continue
		}
		if (zone || isBuilding) && pendingWork(p) {
			return true
		}
	}
	return false
}

func uniqueFieldDefinitions(values []string) []string {
	seen := map[string]bool{}
	out := []string{}
	for _, v := range values {
		if !seen[v] {
			seen[v] = true
			out = append(out, v)
		}
	}
	return out
}

// Growth time belongs to a zone an applied zone_create of this goal epoch
// created, while the farm census still lists it growing the crop. Its
// deadline starts at durable creation and cannot be renewed by polling or
// restarting.
func (r *RoutineFieldPlanner) fieldAllowance(ctx context.Context, goal domain.Goal, current domain.GenerationSnapshot, facts observation.ColonyProjection) (uint32, error) {
	methods, err := r.reviewer.player.journal.LoadGoalMethods(ctx, goal.ID, goal.Epoch)
	if err != nil {
		return 0, err
	}
	var remaining uint32
	for _, method := range methods {
		plan, err := r.reviewer.player.journal.LoadPlan(ctx, method.Plan)
		if err != nil {
			return 0, err
		}
		snapshot := current
		snapshot.Plan = plan.Spec.ID()
		snapshot.Revision = plan.Spec.Revision()
		for _, progress := range plan.Progress {
			v := progress.View()
			zone, ok := progress.Action().ZoneCreate()
			id, created := v.Zone.Value()
			if !ok || !created || v.Stage != domain.Completed || v.Unresolved || !v.Snapshot.Matches(snapshot) || v.Tick > facts.Identity.Tick {
				continue
			}
			var budget domain.Tick
			for _, d := range facts.Definitions {
				if d.Name == zone.Crop() {
					days, known := d.GrowDays.Value()
					if known && days > 0 && days <= 24 {
						budget = domain.Tick(math.Ceil(min(60, days*2.5+r.reviewer.seasonal(facts.Facts).FoodTargetDays) * 60000))
					}
				}
			}
			if budget == 0 || facts.Identity.Tick-v.Tick >= budget {
				continue
			}
			for _, farm := range facts.Farms {
				if farm.ID == id && farm.Crop == zone.Crop() {
					remaining = max(remaining, uint32(budget-(facts.Identity.Tick-v.Tick)))
				}
			}
		}
	}
	return remaining, nil
}

// fieldSiteRequest is the site-type request a field step plans over its
// own colony read: crop choices from the plant definitions, the layout
// plan's field blocks, and the infrastructure a controlled grower needs.
func fieldSiteRequest(projection observation.ColonyProjection, protected []domain.Cell, reserveDays float64) (policy.SiteTypeRequest, []policy.CropChoice) {
	var choices []policy.CropChoice
	for _, d := range projection.Definitions {
		// Only plant definitions are crops; buildings in the same census
		// are infrastructure choices below.
		if _, isPlant := d.GrowDays.Value(); !isPlant {
			continue
		}
		choices = append(choices, policy.CropChoice{Name: d.Name, Available: d.Available, Edible: d.Edible, GrowDays: d.GrowDays, FertilityMin: d.FertilityMin, FertilitySensitivity: d.FertilitySensitivity, HarvestNutrition: d.HarvestNutrition, Demand: d.NutritionDemandPerDay, SowTags: d.SowTags, MinGlow: d.GrowMinGlow, HarvestWork: d.HarvestWork, RawPreferred: d.RawPreferred, DietAllowed: d.DietAllowed, RequiresPollution: d.RequiresPollution, RequiresCleanSoil: d.RequiresCleanSoil})
	}
	coverage := policy.FieldCoverage(projection.Facts.Colonists, projection.FieldCapacityCrops, reserveDays)
	site := policy.FarmSiteRequest{Bounds: projection.Bounds, Anchor: layoutAnchor(projection, policy.DistrictFields), Cells: projection.Cells, Protected: protected}
	if fields, ok := layoutFieldCells(projection); ok {
		site.Fields = fields
	}
	growers, cooks := policy.CropWorkers(projection.WorkPawns)
	request := policy.SiteTypeRequest{Field: policy.FieldRequest{Growers: growers, Cooks: cooks, Calendar: projection.Facts.Calendar, Conditions: projection.Facts.DisasterConditions, Choices: choices, Climate: projection.CropClimate, Runway: projection.Facts.FoodDays, Colonists: projection.Facts.Colonists, ReserveDays: reserveDays, Coverage: coverage, Site: site}, Environment: projection.Environment, LampGrowthRadius: fieldLampGrowthRadius}
	for _, d := range projection.Definitions {
		infrastructure := domain.Known(policy.Infrastructure{Name: d.Name, Available: d.Available, PowerW: d.PowerW, Fertility: d.GrowerFertility, Costs: d.Costs})
		switch d.Name {
		case "SunLamp":
			request.Lamp = infrastructure
		case "HydroponicsBasin":
			request.Basin = infrastructure
		case "Heater":
			request.Heater = infrastructure
		}
	}
	return request, choices
}

// firebreakRing is the firebreak band around the base and its growing zones
// (#1550): new fields never take it, whatever its treatment. An unknown ring
// protects nothing.
func firebreakRing(projection observation.ColonyProjection) ([]domain.Cell, error) {
	ring, err := policy.FirebreakRing(firebreakRequest(projection))
	cells, _ := ring.Value()
	return cells, err
}
