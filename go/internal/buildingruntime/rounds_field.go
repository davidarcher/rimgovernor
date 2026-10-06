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
	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
)

type RoundsFieldPlanner struct {
	reviewer *Rounder
	native   FieldNative
}
type RoundsFieldResult struct {
	NativeWorkTicks uint32
	Verdict
	Plan domain.PlanID
}

type FieldNative interface {
	PreviewZone(context.Context, *c.Identity, domain.ZoneCreate) (*op.ZonePreviewReply, bridge.Result, error)
}

func NewRoundsFieldPlanner(reviewer *Rounder, native FieldNative) (*RoundsFieldPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, fmt.Errorf("%w: NewRoundsFieldPlanner: reviewer == nil || native == nil", ErrControl)
	}
	return &RoundsFieldPlanner{reviewer: reviewer, native: native}, nil
}
func (r *RoundsFieldPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoundsFieldResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoundsFieldResult{Verdict: BuildingReasonDisabled}, nil
	}
	if !state.ObservationKnown {
		return RoundsFieldResult{}, fmt.Errorf("%w: step: !state.ObservationKnown", ErrControl)
	}
	review, err := p.journal.LoadRounds(call)
	if err != nil {
		return RoundsFieldResult{}, err
	}
	if !review.Enabled || review.Snapshot != state.Snapshot {
		return RoundsFieldResult{Verdict: BuildingReasonNoReview}, nil
	}
	goal, workable, err := p.journal.Workable(call, review, policy.EnsureFoodSupply)
	if err != nil {
		return RoundsFieldResult{}, err
	}
	idle := BuildingReasonNoDeficit
	if workable && goal.Standard.Priority >= 3 {
		selected := false
		for _, row := range review.Development.Rows {
			selected = selected || row.Concern == policy.EnsureFoodSupply && row.Selected
		}
		if !selected {
			workable, idle = false, awaitingSlot(string(policy.EnsureFoodSupply))
		}
	}
	// Open field work is budgeted against the food plan below. Infrastructure
	// with unknown output keeps its barrier; completed growers may be recropped.
	blocked := false
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoundsFieldResult{}, err
		}
		blocked = blocked || fieldBlockingWork(plan.Progress)
	}
	plans, err := p.journal.LoadPlans(call, 256)
	if err != nil {
		return RoundsFieldResult{}, err
	}
	playerPlans, err := p.journal.PlayerPlans(call, playerWorld(state.Snapshot))
	if err != nil {
		return RoundsFieldResult{}, err
	}
	definitions := append(roundsProjectDefinitions(plans, state.Snapshot, playerPlans), "Plant_Rice", "Plant_Potato", "Plant_Corn", "Plant_Strawberry", "Plant_Toxipotato", "Plant_Nutrifungus", "Plant_Haygrass", "Plant_Hops", "Plant_Smokeleaf", "SunLamp", "HydroponicsBasin", "Heater")
	definitions = uniqueFieldDefinitions(definitions)
	expected, err := stepScope(call, r.reviewer.native)
	if err != nil {
		return RoundsFieldResult{}, err
	}
	if !roundsBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoundsFieldResult{}, fmt.Errorf("%w: step: !roundsBuildingBoundary(expected, state.Snapshot, review.Tick)", ErrControl)
	}
	claims, err := p.journal.ConstructionClaims(call, state.Snapshot, expected.Tick)
	if err != nil {
		return RoundsFieldResult{}, err
	}
	read, err := r.reviewer.observeOwned(call, r.reviewer.native, expected, claims, definitions...)
	if err != nil {
		return RoundsFieldResult{}, err
	}
	projection := read.Projection
	recordStepRead("field", policy.EnsureFoodSupply, state.Snapshot, projection)
	held, err := p.journal.BuildingReservations(call, state.Snapshot)
	if err != nil {
		return RoundsFieldResult{}, err
	}
	var protected []domain.Cell
	for _, h := range held {
		protected = append(protected, h.Footprint...)
	}
	var shells []store.PlanState
	for _, binding := range review.Standards {
		if binding.Concern != policy.MaintainHousing {
			continue
		}
		shelter, err := p.journal.LoadStandard(call, binding.Standard)
		if err != nil {
			return RoundsFieldResult{}, err
		}
		for _, method := range shelter.Methods {
			plan, err := p.journal.LoadPlan(call, method.Plan)
			if err != nil {
				return RoundsFieldResult{}, err
			}
			shells = append(shells, plan)
		}
	}
	claimed, _ := claims.Value()
	protected = append(protected, shellInteriors(shells, claimed)...)
	ring, err := firebreakRing(projection)
	if err != nil {
		return RoundsFieldResult{}, err
	}
	protected = append(protected, ring...)
	// The cross-crop ledger (#1308): hay and social shortfalls compete with
	// the food block for the plan's field patches in one ranked order.
	others, err := r.otherFieldShortfalls(call, review, projection)
	if err != nil {
		return RoundsFieldResult{}, err
	}
	anchor, planned := fieldAnchor(projection)
	if !planned {
		return RoundsFieldResult{Verdict: BuildingNoLayoutPlan}, nil
	}
	placeOthers := func(wait uint32, reason Verdict) (RoundsFieldResult, error) {
		result, tried, err := r.placeLedger(call, epoch, state, projection, read, wait, others, anchor, protected)
		if err != nil || tried {
			return result, err
		}
		return RoundsFieldResult{Verdict: reason, NativeWorkTicks: wait}, nil
	}
	if !workable {
		return placeOthers(0, idle)
	}
	if result, handled, err := r.fishing(call, epoch, state, goal, read); err != nil || handled {
		return result, err
	}
	if blocked {
		openPlans := []store.PlanState{}
		for _, method := range goal.Methods {
			plan, err := p.journal.LoadPlan(call, method.Plan)
			if err != nil {
				return RoundsFieldResult{}, err
			}
			openPlans = append(openPlans, plan)
		}
		blocked = !foodPlanFieldRoom(projection, openPlans)
	}
	wait, err := r.fieldAllowance(call, goal.Standard, state.Snapshot, projection)
	if err != nil {
		return RoundsFieldResult{}, err
	}
	reserveDays := r.reviewer.seasonal(projection.Facts).FoodTargetDays
	request, choices := fieldSiteRequest(projection, protected, reserveDays)
	selection, known := policy.PlanSiteType(request)
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
		return placeOthers(wait, BuildingReasonExistingWork)
	}
	// A crop whose zones outgrew its full target gives up bare cells
	// first (#1309): the shortfall planners go quiet once covered.
	if result, handled, err := r.shrink(call, epoch, state, goal, projection, read, request.Field); err != nil || handled {
		return result, err
	}
	if plan, planned := projection.Facts.FoodPlan.Value(); !planned || !foodPlanOpensField(plan) {
		return placeOthers(wait, awaitingFoodPlan("new-field"))
	}
	if !known {
		return placeOthers(wait, fieldUnavailable("field_plan"))
	}
	// The winner's cells: basin kinds carry them on the candidate, not a site plan.
	telemetry.Decide(call, fieldsSelectDecision(selection))
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
		var result RoundsFieldResult
		var tried bool
		var err error
		if candidate.Kind == policy.SiteOutdoor && len(candidate.Buildings) == 0 {
			food := fieldShortfall{Standard: goal, Options: fieldBlockOptions(candidate, selection.Candidates), What: "food"}
			result, tried, err = r.placeLedger(call, epoch, state, projection, read, wait, append([]fieldShortfall{food}, others...), request.Field.Site.Anchor, protected)
			others = nil
		} else {
			result, tried, err = r.enact(call, epoch, state, goal, projection, read, wait, candidate)
		}
		if err != nil || tried {
			return result, err
		}
		fieldEdit(call, "refused", "candidate_refused", candidate.Crop.Name, map[string]any{"kind": candidate.Kind, "cells": candidate.Cells, "refusal": result.Verdict})
	}
	return placeOthers(wait, noSpace("field_candidates"))
}

// otherFieldShortfalls is the hay and social field demand of this step's
// read (#1308), each under its own goal: hay under MaintainAnimalFeed,
// social crops under MaintainResource once brewing is researched. A goal
// with open work waits for it.
func (r *RoundsFieldPlanner) otherFieldShortfalls(call context.Context, review store.Rounds, projection observation.ColonyProjection) ([]fieldShortfall, error) {
	p := r.reviewer.player
	ready := func(kind policy.ConcernID) (store.StandardState, bool, error) {
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
			out = append(out, fieldShortfall{Standard: goal, Options: []policy.FieldBlockOption{opt}, What: "hay"})
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
					out = append(out, fieldShortfall{Standard: goal, Options: []policy.FieldBlockOption{opt}, What: "social"})
				}
			}
		}
	}
	return out, nil
}

// placeLedger places the next field block of the heaviest shortfall that
// still fits (#1308); tried reports a block reached commitment.
func (r *RoundsFieldPlanner) placeLedger(call, epoch context.Context, state ControlState, projection observation.ColonyProjection, read observation.RoundsReading, wait uint32, ledger []fieldShortfall, anchor domain.Cell, protected []domain.Cell) (RoundsFieldResult, bool, error) {
	result := RoundsFieldResult{Verdict: BuildingReasonNoDeficit, NativeWorkTicks: wait}
	for _, s := range rankFieldShortfalls(ledger) {
		if len(s.Options) == 0 {
			continue
		}
		lead := s.Options[0]
		var tried bool
		var err error
		result, tried, err = r.enactBlock(call, epoch, state, s.Standard, projection, read, wait, policy.SiteTypeCandidate{Kind: policy.SiteOutdoor, Crop: lead.Crop, Needed: lead.Needed}, s.Options, anchor, protected)
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
func (r *RoundsFieldPlanner) enact(call, epoch context.Context, state ControlState, goal store.StandardState, projection observation.ColonyProjection, read observation.RoundsReading, wait uint32, candidate policy.SiteTypeCandidate) (RoundsFieldResult, bool, error) {
	p := r.reviewer.player
	crop := candidate.Crop
	hash := sha256.New()
	fmt.Fprintf(hash, "%s/%s/%v/%v", candidate.Kind, crop.Name, candidate.Sites.Patches, candidate.Buildings)
	method := domain.MethodID(fmt.Sprintf("fields-%x", hash.Sum(nil)[:16]))
	if _, err := p.journal.LoadMethod(call, goal.Standard.ID, goal.Standard.Episode, method); err == nil {
		return RoundsFieldResult{Verdict: waitFor(WaitMethodUsed, "field_method"), NativeWorkTicks: wait}, true, nil
	} else if !errors.Is(err, store.ErrNotFound) {
		return RoundsFieldResult{}, false, err
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
			return RoundsFieldResult{Verdict: fieldUnavailable("building_preview"), NativeWorkTicks: wait}, false, nil
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
				return RoundsFieldResult{}, false, err
			}
			action, err := domain.NewBuildingAction(domain.ActionID(fmt.Sprintf("%s-%d", id, i)), building)
			if err != nil {
				return RoundsFieldResult{}, false, err
			}
			preview, _, err := native.PreviewBuilding(call, action, snapshot)
			if err != nil {
				return RoundsFieldResult{}, false, err
			}
			v := preview.Preview
			legal, lk := v.CanPlace.Value()
			safe, sk := v.SafeToPlace.Value()
			if !lk || !sk {
				return RoundsFieldResult{Verdict: fieldUnavailable("field_preview"), NativeWorkTicks: wait}, false, nil
			}
			if !legal || !safe {
				return RoundsFieldResult{Verdict: noSpace("field_building_site"), NativeWorkTicks: wait}, false, nil
			}
			if err = mergeRoundsStock(&stock, preview.Stock, i == 0); err != nil {
				return RoundsFieldResult{}, false, err
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
				return RoundsFieldResult{}, false, err
			}
			action, err := domain.NewZoneCreateAction(domain.ActionID(fmt.Sprintf("%s-%d", id, i)), value)
			if err != nil {
				return RoundsFieldResult{}, false, err
			}
			reply, refused, err := previewZone(call, r.native, boundary.Identity(snapshot), value)
			if err != nil {
				return RoundsFieldResult{}, false, err
			}
			if refused != "" {
				fieldEdit(call, "refused", "preview_refused", crop.Name, map[string]any{"detail": refused})
				return RoundsFieldResult{Verdict: siteBlocked("field_zone", "preview_refused"), NativeWorkTicks: wait}, false, nil
			}
			v := reply.GetEvaluated()
			if _, err = boundary.Context(v.Context, snapshot); err != nil || domain.Tick(v.Context.GetTick()) < projection.Identity.Tick {
				return RoundsFieldResult{}, false, fmt.Errorf("%w: enact: err != nil || domain.Tick(v.Context.GetTick()) < projection.Identity.Tick", ErrControl)
			}
			actions = append(actions, action)
			previews = append(previews, policy.Preview{Action: action, Snapshot: snapshot, Tick: projection.Identity.Tick, CanPlace: domain.Known(true), SafeToPlace: domain.Known(true), MadeFromStuff: domain.Known(false), WatchCellsAccessible: domain.Known(true), Footprint: domain.Known(cells), Costs: domain.Known([]policy.Amount{})})
		}
	}
	return r.admit(call, epoch, state, goal, projection, read, wait, method, id, snapshot, stock, actions, previews, string(candidate.Kind)+" "+crop.Name)
}

// admit admits one field method's actions with their previews.
func (r *RoundsFieldPlanner) admit(call, epoch context.Context, state ControlState, goal store.StandardState, projection observation.ColonyProjection, read observation.RoundsReading, wait uint32, method domain.MethodID, id domain.PlanID, snapshot domain.GenerationSnapshot, stock policy.StockObservation, actions []domain.Action, previews []policy.Preview, what string) (RoundsFieldResult, bool, error) {
	p := r.reviewer.player
	plan, err := domain.NewPlan(id, 1, actions)
	if err != nil {
		return RoundsFieldResult{}, false, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoundsFieldResult{}, false, err
	}
	if p.session.State() != state {
		return RoundsFieldResult{}, false, fmt.Errorf("%w: enact: p.session.State() != state", ErrControl)
	}
	actual, err := stepScope(call, r.reviewer.native)
	if err != nil || !roundsBuildingBoundary(actual, state.Snapshot, projection.Identity.Tick) {
		return RoundsFieldResult{}, false, fmt.Errorf("%w: enact: err != nil || !roundsBuildingBoundary(actual, state.Snapshot, projection.Identity.Tick)", ErrControl)
	}
	now := r.reviewer.clock.Now()
	if now.Before(read.StartedAt) || now.Sub(read.StartedAt) > r.reviewer.maxAge {
		return RoundsFieldResult{}, false, observation.ErrStale
	}
	decision, err := admitMethod(call, p.journal, store.BuildingMethodRequest{Owner: goal, Method: method, Plan: plan, Current: snapshot, Tick: projection.Identity.Tick, Bounds: domain.Known(projection.Bounds), Stock: stock, Previews: previews, Purpose: policy.Rounds})
	if err != nil {
		return RoundsFieldResult{}, false, err
	}
	if !decision.Admitted {
		return RoundsFieldResult{Verdict: admissionRefused(decision), NativeWorkTicks: wait}, false, nil
	}
	return RoundsFieldResult{Verdict: BuildingReasonAdmitted, Plan: id}, true, nil
}

const fieldBatchPatches = 6

// enactBlock takes the next plan field block step (#1223): create the
// block's growing zone, or grow it with add-cells until the block is full.
// No plan field blocks is a refusal with its reason; nothing is sited
// outside the plan.
func (r *RoundsFieldPlanner) enactBlock(call, epoch context.Context, state ControlState, goal store.StandardState, projection observation.ColonyProjection, read observation.RoundsReading, wait uint32, candidate policy.SiteTypeCandidate, options []policy.FieldBlockOption, anchor domain.Cell, protected []domain.Cell) (RoundsFieldResult, bool, error) {
	p := r.reviewer.player
	edit, reason, ok := planFieldBlock(projection, anchor, options, protected)
	if !ok {
		clockEvent(call, "layout", "fields", "outdoor field refused: "+reason, "crop", candidate.Crop.Name)
		return RoundsFieldResult{Verdict: noSpace("field_block"), NativeWorkTicks: wait}, false, nil
	}
	hash := sha256.New()
	fmt.Fprintf(hash, "block/%s/%s/%v", edit.Zone, edit.Crop, edit.Cells)
	method := domain.MethodID(fmt.Sprintf("fields-%x", hash.Sum(nil)[:16]))
	if _, err := p.journal.LoadMethod(call, goal.Standard.ID, goal.Standard.Episode, method); err == nil {
		return RoundsFieldResult{Verdict: waitFor(WaitMethodUsed, "field_block_method"), NativeWorkTicks: wait}, true, nil
	} else if !errors.Is(err, store.ErrNotFound) {
		return RoundsFieldResult{}, false, err
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
			return RoundsFieldResult{}, false, err
		}
		action, err := domain.NewZoneCellEditAction(domain.ActionID(fmt.Sprintf("%s-0", id)), value)
		if err != nil {
			return RoundsFieldResult{}, false, err
		}
		plan, err := domain.NewPlan(id, 1, []domain.Action{action})
		if err != nil {
			return RoundsFieldResult{}, false, err
		}
		if err = p.current(call, epoch); err != nil {
			return RoundsFieldResult{}, false, err
		}
		now := r.reviewer.clock.Now()
		if p.session.State() != state || now.Before(read.StartedAt) || now.Sub(read.StartedAt) > r.reviewer.maxAge {
			return RoundsFieldResult{}, false, fmt.Errorf("%w: enactBlock: p.session.State() != state || now.Before(read.StartedAt) || now.Sub(read.StartedAt) > r.reviewer.maxAge", ErrControl)
		}
		if _, err = p.journal.CommitMethod(call, goal.Standard.ID, goal.Revision, method, plan); err != nil {
			return RoundsFieldResult{}, false, err
		}
		clockEvent(call, "layout", "fields", "field block grown", "zone", edit.Zone, "crop", edit.Crop, "cells", len(edit.Cells), "plan", string(id))
		return RoundsFieldResult{Verdict: BuildingReasonAdmitted, Plan: id}, true, nil
	}
	value, err := domain.NewZoneCreate(domain.GrowingZone, edit.Crop, edit.Cells)
	if err != nil {
		return RoundsFieldResult{}, false, err
	}
	action, err := domain.NewZoneCreateAction(domain.ActionID(fmt.Sprintf("%s-0", id)), value)
	if err != nil {
		return RoundsFieldResult{}, false, err
	}
	reply, refused, err := previewZone(call, r.native, boundary.Identity(snapshot), value)
	if err != nil {
		return RoundsFieldResult{}, false, err
	}
	if refused != "" {
		fieldEdit(call, "refused", "preview_refused", edit.Crop, map[string]any{"detail": refused})
		return RoundsFieldResult{Verdict: siteBlocked("field_zone", "preview_refused"), NativeWorkTicks: wait}, false, nil
	}
	v := reply.GetEvaluated()
	if _, err = boundary.Context(v.Context, snapshot); err != nil || domain.Tick(v.Context.GetTick()) < projection.Identity.Tick {
		return RoundsFieldResult{}, false, fmt.Errorf("%w: enactBlock: err != nil || domain.Tick(v.Context.GetTick()) < projection.Identity.Tick", ErrControl)
	}
	stock := policy.StockObservation{Snapshot: snapshot, Tick: projection.Identity.Tick}
	preview := policy.Preview{Action: action, Snapshot: snapshot, Tick: projection.Identity.Tick, CanPlace: domain.Known(true), SafeToPlace: domain.Known(true), MadeFromStuff: domain.Known(false), WatchCellsAccessible: domain.Known(true), Footprint: domain.Known(edit.Cells), Costs: domain.Known([]policy.Amount{})}
	return r.admit(call, epoch, state, goal, projection, read, wait, method, id, snapshot, stock, []domain.Action{action}, []policy.Preview{preview}, "block "+edit.Crop)
}

// recrop commits one grower's crop change as a one-shot
// GrowerCrop plan, once per grower per Episode: a method that already
// ran this epoch (the patch was refused, or a player changed the crop back)
// is not retried until the next epoch. tried reports whether the grower
// reached commitment; a grower whose native read refuses is not
// tried so the caller moves on to the next one.
func (r *RoundsFieldPlanner) recrop(call, epoch context.Context, state ControlState, goal store.StandardState, projection observation.ColonyProjection, read observation.RoundsReading, wait uint32, choice policy.GrowerCropChoice, arbiter *stepArbiter) (RoundsFieldResult, bool, error) {
	native, ok := r.native.(interface {
		ReadGrowerCropTarget(context.Context, *c.Identity, string) (bridge.GrowerCropTarget, bridge.Result, error)
	})
	if !ok {
		return RoundsFieldResult{Verdict: fieldUnavailable("grower_crop_target"), NativeWorkTicks: wait}, false, nil
	}
	p := r.reviewer.player
	method := domain.MethodID("fields-recrop-" + choice.Grower)
	if _, err := p.journal.LoadMethod(call, goal.Standard.ID, goal.Standard.Episode, method); err == nil {
		return RoundsFieldResult{Verdict: waitFor(WaitMethodUsed, "recrop_method"), NativeWorkTicks: wait}, false, nil
	} else if !errors.Is(err, store.ErrNotFound) {
		return RoundsFieldResult{}, false, err
	}
	target, _, err := native.ReadGrowerCropTarget(call, boundary.Identity(state.Snapshot), choice.Grower)
	if err != nil {
		return RoundsFieldResult{}, false, err
	}
	if _, err = boundary.Context(target.Context, state.Snapshot); err != nil || target.Context.GetTick() < int64(projection.Identity.Tick) {
		return RoundsFieldResult{}, false, fmt.Errorf("%w: recrop: err != nil || target.Context.GetTick() < int64(projection.Identity.Tick)", ErrControl)
	}
	if target.Crop != choice.Current {
		return RoundsFieldResult{Verdict: fieldUnavailable("grower_crop"), NativeWorkTicks: wait}, false, nil
	}
	patch, err := domain.NewGrowerCrop(choice.Grower, choice.Crop.Name)
	if err != nil {
		return RoundsFieldResult{}, false, err
	}
	// Native checks the grower and crop live when the BuildingPatchIntent
	// applies (#940); a refusal comes back on the plan, not here.
	if !arbiter.tryClaim(nil, "grower:"+choice.Grower) {
		return RoundsFieldResult{Verdict: waitFor(WaitMethodUsed, "grower_claim"), NativeWorkTicks: wait}, false, nil
	}
	id := domain.MintPlanID()
	action, err := domain.NewGrowerCropAction(domain.ActionID(fmt.Sprintf("%s-0", id)), patch)
	if err != nil {
		return RoundsFieldResult{}, false, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoundsFieldResult{}, false, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoundsFieldResult{}, false, err
	}
	now := r.reviewer.clock.Now()
	if p.session.State() != state || now.Before(read.StartedAt) || now.Sub(read.StartedAt) > r.reviewer.maxAge {
		return RoundsFieldResult{}, false, fmt.Errorf("%w: recrop: p.session.State() != state || now.Before(read.StartedAt) || now.Sub(read.StartedAt) > r.reviewer.maxAge", ErrControl)
	}
	fieldEdit(call, "admitted", "recrop", choice.Grower, map[string]any{"from": choice.Current, "to": choice.Crop.Name, "detail": choice.Reason})
	if _, err = p.journal.CommitMethod(call, goal.Standard.ID, goal.Revision, method, plan); err != nil {
		return RoundsFieldResult{}, false, err
	}
	return RoundsFieldResult{Verdict: BuildingReasonAdmitted, Plan: id}, true, nil
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

// Growth time belongs to a zone an applied zone_create of this Episode
// created, while the farm census still lists it growing the crop. Its
// deadline starts at durable creation and cannot be renewed by polling or
// restarting.
func (r *RoundsFieldPlanner) fieldAllowance(ctx context.Context, goal domain.Standard, current domain.GenerationSnapshot, facts observation.ColonyProjection) (uint32, error) {
	methods, err := r.reviewer.player.journal.LoadMethods(ctx, goal.ID, goal.Episode)
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
// fieldRequest is the crop side of a field decision, without a site: the
// plan prices new-field candidates from it and the executor adds the site.
func fieldRequest(projection observation.ColonyProjection, reserveDays float64) (policy.FieldRequest, []policy.CropChoice) {
	var choices []policy.CropChoice
	for _, d := range projection.Definitions {
		// Only plant definitions are crops; buildings in the same census
		// are infrastructure choices below.
		if _, isPlant := d.GrowDays.Value(); !isPlant {
			continue
		}
		choices = append(choices, policy.CropChoice{Name: d.Name, Available: d.Available, Edible: d.Edible, GrowDays: d.GrowDays, FertilityMin: d.FertilityMin, FertilitySensitivity: d.FertilitySensitivity, HarvestNutrition: d.HarvestNutrition, Demand: d.NutritionDemandPerDay, SowTags: d.SowTags, MinGlow: d.GrowMinGlow, HarvestWork: d.HarvestWork, RawPreferred: d.RawPreferred, DietAllowed: d.DietAllowed, RequiresPollution: d.RequiresPollution, RequiresCleanSoil: d.RequiresCleanSoil, RotDays: d.HarvestRotDays, Perishable: d.HarvestPerishable})
	}
	coverage := policy.FieldCoverage(projection.Facts.Colonists, projection.FieldCapacityCrops, reserveDays)
	growers, cooks := policy.CropWorkers(projection.WorkPawns)
	return policy.FieldRequest{Growers: growers, Cooks: cooks, Calendar: projection.Facts.Calendar, Conditions: projection.Facts.DisasterConditions, Choices: choices, Climate: projection.CropClimate, Runway: projection.Facts.FoodDays, Colonists: projection.Facts.Colonists, ReserveDays: reserveDays, Coverage: coverage}, choices
}

func fieldSiteRequest(projection observation.ColonyProjection, protected []domain.Cell, reserveDays float64) (policy.SiteTypeRequest, []policy.CropChoice) {
	field, choices := fieldRequest(projection, reserveDays)
	anchor, _ := fieldAnchor(projection)
	field.Site = policy.FarmSiteRequest{Bounds: projection.Bounds, Anchor: anchor, Cells: projection.Cells, Protected: protected}
	if fields, ok := layoutFieldCells(projection); ok {
		field.Site.Fields = fields
	}
	request := policy.SiteTypeRequest{Field: field, Environment: projection.Environment, LampGrowthRadius: fieldLampGrowthRadius, Items: projection.Facts.Items}
	for _, d := range projection.Definitions {
		infrastructure := domain.Known(policy.Infrastructure{Name: d.Name, Available: d.Available, PowerW: d.PowerW, Fertility: d.GrowerFertility, Costs: d.CheapestCosts()})
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

// fieldEdit files a layout_edit row for a field decision: a candidate or
// zone the game refused, or a grower re-cropped. The facts go in attrs;
// reason stays a stable word.
func fieldEdit(ctx context.Context, verdict, reason, target string, attrs map[string]any) {
	attrs["family"] = "field"
	telemetry.Decide(ctx, telemetry.Decision{Kind: "layout_edit", Component: "clock-scheduler", Verdict: verdict, Reason: reason, Target: target, Attrs: attrs})
}
