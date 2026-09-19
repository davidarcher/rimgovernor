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
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	receipts "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
	"math"
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
	PreviewZone(context.Context, *c.Identity, bridge.ZoneTarget) (*op.PreviewReply, bridge.Result, error)
}

func NewRoutineFieldPlanner(reviewer *RoutineReviewer, native FieldNative) (*RoutineFieldPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, ErrControl
	}
	return &RoutineFieldPlanner{reviewer: reviewer, native: native}, nil
}
func (r *RoutineFieldPlanner) Step(ctx context.Context) (RoutineFieldResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, false)
	if err != nil {
		return RoutineFieldResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}
func (r *RoutineFieldPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoutineFieldResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutineFieldResult{Reason: BuildingMethodDisabled}, nil
	}
	if !state.ObservationKnown {
		return RoutineFieldResult{}, ErrControl
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutineFieldResult{}, err
	}
	if !review.Enabled || review.Snapshot != state.Snapshot {
		return RoutineFieldResult{Reason: BuildingMethodNoReview}, nil
	}
	var goal store.GoalState
	for _, binding := range review.Goals {
		if binding.Need == policy.EnsureFoodSupply {
			goal, err = p.journal.LoadGoal(call, binding.Goal)
			break
		}
	}
	if err != nil {
		return RoutineFieldResult{}, err
	}
	if goal.Goal.Status != domain.GoalActive || goal.Goal.Need != domain.NeedDeficit {
		return RoutineFieldResult{Reason: BuildingMethodNoDeficit}, nil
	}
	if goal.Goal.Priority >= 3 {
		selected := false
		for _, row := range review.Development.Rows {
			selected = selected || row.Goal == policy.EnsureFoodSupply && row.Selected
		}
		if !selected {
			return RoutineFieldResult{Reason: BuildingMethodRefused}, nil
		}
	}
	// Only open field work blocks another field batch. Hunting and
	// foraging plans under the same goal share no cells or resources
	// with a growing zone and would otherwise starve crops indefinitely.
	// A batch of basins stays open until its last basin stands, so a
	// grower that finished under it is re-cropped below without waiting
	// for the batch; without one, the step ends here without observing.
	blocked, grown := false, false
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoutineFieldResult{}, err
		}
		blocked = blocked || fieldBlockingWork(plan.Progress)
		grown = grown || fieldGrowerBuilt(plan.Progress)
	}
	if blocked && !grown {
		return RoutineFieldResult{Reason: BuildingMethodExistingWork}, nil
	}
	plans, err := p.journal.LoadPlans(call, 256)
	if err != nil {
		return RoutineFieldResult{}, err
	}
	playerPlans, err := p.journal.PlayerPlans(call, playerWorld(state.Snapshot))
	if err != nil {
		return RoutineFieldResult{}, err
	}
	definitions := append(routineProjectDefinitions(plans, state.Snapshot, playerPlans), "Plant_Rice", "Plant_Potato", "Plant_Corn", "SunLamp", "HydroponicsBasin", "Heater")
	definitions = uniqueFieldDefinitions(definitions)
	identity, _, err := r.reviewer.native.Identity(call)
	if err != nil {
		return RoutineFieldResult{}, err
	}
	expected, err := observation.DecodeIdentity(identity)
	if err != nil {
		return RoutineFieldResult{}, err
	}
	if !routineBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoutineFieldResult{}, ErrControl
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
	wait, managed, err := r.fieldAllowance(call, goal.Goal, state.Snapshot, projection)
	if err != nil {
		return RoutineFieldResult{}, err
	}
	token, known := projection.ZoneMapToken.Value()
	if !known {
		clockSchedulerLog("Fields: zone map token unknown")
		return RoutineFieldResult{Reason: BuildingMethodUnknown, NativeWorkTicks: wait}, nil
	}
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
		if binding.Need != policy.EnsureInitialShelter {
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
	var choices []policy.CropChoice
	for _, d := range projection.Definitions {
		// Only plant definitions are crops; buildings in the same census
		// are infrastructure choices below.
		if _, isPlant := d.GrowDays.Value(); !isPlant {
			continue
		}
		choices = append(choices, policy.CropChoice{Name: d.Name, Available: d.Available, Edible: d.Edible, GrowDays: d.GrowDays, FertilityMin: d.FertilityMin, FertilitySensitivity: d.FertilitySensitivity, HarvestNutrition: d.HarvestNutrition, Demand: d.NutritionDemandPerDay, SowTags: d.SowTags, MinGlow: d.GrowMinGlow})
	}
	reserveDays := r.reviewer.seasonal(projection.Facts).FoodTargetDays
	coverage := policy.FieldCoverage(projection.Facts.Colonists, projection.FieldCapacityCrops, reserveDays)
	var zones []policy.FarmZone
	for _, farm := range projection.Farms {
		zones = append(zones, policy.FarmZone{ID: farm.ID, Crop: farm.Crop, Managed: managed[farm.ID]})
	}
	site := policy.FarmSiteRequest{Bounds: projection.Bounds, Anchor: projection.Center, Storage: domain.Unknown[domain.Cell](), Cells: projection.Cells, Protected: protected, Zones: zones}
	request := policy.SiteTypeRequest{Field: policy.FieldRequest{Choices: choices, Climate: projection.CropClimate, Runway: projection.Facts.FoodDays, Colonists: projection.Facts.Colonists, ReserveDays: reserveDays, Coverage: coverage, Site: site}, Environment: projection.Environment, LampGrowthRadius: fieldLampGrowthRadius}
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
		recrops := policy.PlanGrowerCrops(policy.GrowerCropRequest{Choices: choices, Growers: env.Growers, Urgent: selection.Urgent})
		for _, recrop := range recrops {
			result, tried, err := r.recrop(call, epoch, state, goal, projection, read, wait, recrop, arbiter)
			if err != nil || tried {
				return result, err
			}
		}
	}
	if blocked {
		return RoutineFieldResult{Reason: BuildingMethodExistingWork, NativeWorkTicks: wait}, nil
	}
	if !known {
		clockSchedulerLog("Fields: no plan (cells=%d choices=%d climate=%+v runway=%+v colonists=%+v coverage=%+v zones=%d): %s", len(projection.Cells), len(choices), projection.CropClimate, projection.Facts.FoodDays, projection.Facts.Colonists, coverage, len(zones), selection.Explain())
		return RoutineFieldResult{Reason: BuildingMethodUnknown, NativeWorkTicks: wait}, nil
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
		result, tried, err := r.enact(call, epoch, state, goal, projection, read, wait, candidate, token)
		if err != nil || tried {
			return result, err
		}
		clockSchedulerLog("Fields: %s %s refused (%s), trying next candidate", candidate.Kind, candidate.Crop.Name, result.Reason)
	}
	return RoutineFieldResult{Reason: BuildingMethodRefused, NativeWorkTicks: wait}, nil
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
func (r *RoutineFieldPlanner) enact(call, epoch context.Context, state ControlState, goal store.GoalState, projection observation.ColonyProjection, read observation.RoutineReading, wait uint32, candidate policy.SiteTypeCandidate, token string) (RoutineFieldResult, bool, error) {
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
	digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%d/%s", goal.Goal.ID, goal.Goal.Epoch, method)))
	id := domain.PlanID(fmt.Sprintf("routine-fields-%x", digest[:16]))
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
			if v.Action != action || !v.Snapshot.Matches(snapshot) || !v.Tick.FreshFor(projection.Identity.Tick) {
				return RoutineFieldResult{}, false, ErrControl
			}
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
			reply, refused, err := previewZone(call, r.native, boundary.Identity(snapshot), bridge.ZoneTarget{Zone: value, Token: token})
			if err != nil {
				return RoutineFieldResult{}, false, err
			}
			if refused != "" {
				clockSchedulerLog("Fields: %s patch %+v refused: %s", crop.Name, patch, refused)
				return RoutineFieldResult{Reason: BuildingMethodRefused, NativeWorkTicks: wait}, false, nil
			}
			v := reply.GetEvaluated()
			if _, err = boundary.Context(v.Context, snapshot); err != nil || domain.Tick(v.Context.GetTick()) != projection.Identity.Tick {
				return RoutineFieldResult{}, false, ErrControl
			}
			actions = append(actions, action)
			previews = append(previews, policy.Preview{Action: action, Snapshot: snapshot, Tick: projection.Identity.Tick, CanPlace: domain.Known(true), SafeToPlace: domain.Known(true), MadeFromStuff: domain.Known(false), WatchCellsAccessible: domain.Known(true), Footprint: domain.Known(cells), Costs: domain.Known([]policy.Amount{})})
		}
	}
	plan, err := domain.NewPlan(id, 1, actions)
	if err != nil {
		return RoutineFieldResult{}, false, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutineFieldResult{}, false, err
	}
	if p.session.State() != state {
		return RoutineFieldResult{}, false, ErrControl
	}
	last, _, err := r.reviewer.native.Identity(call)
	if err != nil {
		return RoutineFieldResult{}, false, err
	}
	actual, err := observation.DecodeIdentity(last)
	if err != nil || !routineBuildingBoundary(actual, state.Snapshot, projection.Identity.Tick) {
		return RoutineFieldResult{}, false, ErrControl
	}
	now := r.reviewer.clock.Now()
	if now.Before(read.StartedAt) || now.Sub(read.StartedAt) > r.reviewer.maxAge {
		return RoutineFieldResult{}, false, observation.ErrStale
	}
	decision, err := p.journal.AdmitBuildingMethod(call, store.BuildingMethodRequest{Goal: goal.Goal.ID, Revision: goal.Revision, Method: method, Plan: plan, Current: snapshot, Tick: projection.Identity.Tick, Bounds: domain.Known(projection.Bounds), Stock: stock, Rules: r.reviewer.rules, Previews: previews, Purpose: policy.Routine})
	if err != nil {
		return RoutineFieldResult{}, false, err
	}
	if !decision.Admitted {
		clockSchedulerLog("Fields: %s %s not admitted: %+v", candidate.Kind, crop.Name, decision.Refused)
		return RoutineFieldResult{Reason: BuildingMethodRefused, NativeWorkTicks: wait}, false, nil
	}
	return RoutineFieldResult{Reason: BuildingMethodAdmitted, Plan: id}, true, nil
}

const fieldBatchPatches = 6

// recrop previews and commits one grower's crop change as a one-shot
// GrowerCrop plan, once per grower per goal epoch: a method that already
// ran this epoch (the patch was refused, or a player changed the crop back)
// is not retried until the next epoch. tried reports whether the grower
// reached commitment; a grower whose native read or preview refuses is not
// tried so the caller moves on to the next one.
func (r *RoutineFieldPlanner) recrop(call, epoch context.Context, state ControlState, goal store.GoalState, projection observation.ColonyProjection, read observation.RoutineReading, wait uint32, choice policy.GrowerCropChoice, arbiter *stepArbiter) (RoutineFieldResult, bool, error) {
	native, ok := r.native.(interface {
		ReadGrowerCropTarget(context.Context, *c.Identity, string) (bridge.GrowerCropTarget, bridge.Result, error)
		PreviewGrowerCrop(context.Context, *c.Identity, domain.GrowerCrop) (*op.PreviewReply, bridge.Result, error)
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
		return RoutineFieldResult{}, false, ErrControl
	}
	if target.Crop != choice.Current {
		clockSchedulerLog("Fields: grower %s crop moved (%s -> %s) since the census", choice.Grower, choice.Current, target.Crop)
		return RoutineFieldResult{Reason: BuildingMethodUnknown, NativeWorkTicks: wait}, false, nil
	}
	patch, err := domain.NewGrowerCrop(choice.Grower, choice.Crop.Name, target.Token)
	if err != nil {
		return RoutineFieldResult{}, false, err
	}
	preview, _, err := native.PreviewGrowerCrop(call, boundary.Identity(state.Snapshot), patch)
	if err != nil {
		return RoutineFieldResult{}, false, err
	}
	evaluated := preview.GetEvaluated()
	if evaluated == nil || !evaluated.GetAccepted() {
		clockSchedulerLog("Fields: grower %s crop %s refused at preview: %s", choice.Grower, choice.Crop.Name, preview.GetFailure().GetDetail())
		return RoutineFieldResult{Reason: BuildingMethodRefused, NativeWorkTicks: wait}, false, nil
	}
	if _, err = boundary.Context(evaluated.Context, state.Snapshot); err != nil {
		return RoutineFieldResult{}, false, ErrControl
	}
	if !arbiter.tryClaim(nil, "grower:"+choice.Grower) {
		return RoutineFieldResult{Reason: BuildingMethodUsed, NativeWorkTicks: wait}, false, nil
	}
	digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%d/%s", goal.Goal.ID, goal.Goal.Epoch, method)))
	id := domain.PlanID(fmt.Sprintf("routine-fields-%x", digest[:16]))
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
		return RoutineFieldResult{}, false, ErrControl
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
		building, isBuilding := p.Action().Building()
		// The butcher spot shares the goal but not the field (#260).
		if isBuilding && building.Definition() == "ButcherSpot" {
			continue
		}
		if (zone || isBuilding) && domain.GoalWorkOpen([]domain.Progress{p}) {
			return true
		}
	}
	return false
}

// fieldGrowerBuilt reports a completed plant-grower construction in the
// plan: the built grower sows its definition's default crop until re-cropped.
func fieldGrowerBuilt(progress []domain.Progress) bool {
	for _, p := range progress {
		b, ok := p.Action().Building()
		if ok && b.Definition() == "HydroponicsBasin" && p.View().Stage == domain.Completed {
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

// Growth time belongs to an exact completed zone in this goal epoch. Its deadline
// starts at durable creation and cannot be renewed by polling or restarting.
// The native zone ids of every observed controller-created growing zone are
// returned so expansion can treat them as managed farms.
func (r *RoutineFieldPlanner) fieldAllowance(ctx context.Context, goal domain.Goal, current domain.GenerationSnapshot, facts observation.ColonyProjection) (uint32, map[string]bool, error) {
	native, ok := r.native.(interface {
		LookupZone(context.Context, bridge.ZoneAttempt) (*receipts.LookupReply, bridge.Result, error)
		ObserveZone(context.Context, bridge.ZoneAttempt, *receipts.Receipt) (*receipts.ProgressReply, bridge.Result, error)
	})
	if !ok {
		return 0, nil, nil
	}
	methods, err := r.reviewer.player.journal.LoadGoalMethods(ctx, goal.ID, goal.Epoch)
	if err != nil {
		return 0, nil, err
	}
	namespace, err := r.reviewer.player.journal.Identity(ctx)
	if err != nil {
		return 0, nil, err
	}
	var remaining uint32
	managed := map[string]bool{}
	for _, method := range methods {
		plan, err := r.reviewer.player.journal.LoadPlan(ctx, method.Plan)
		if err != nil {
			return 0, nil, err
		}
		snapshot := current
		snapshot.Plan = plan.Spec.ID()
		snapshot.Revision = plan.Spec.Revision()
		for _, progress := range plan.Progress {
			v := progress.View()
			zone, ok := progress.Action().ZoneCreate()
			if !ok || v.Stage != domain.Completed || v.Unresolved || !v.Snapshot.Matches(snapshot) || v.Tick > facts.Identity.Tick {
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
			token := ""
			for _, row := range plan.ZoneAdmissions {
				if row.Action == v.Action && row.Admission.Snapshot == v.Snapshot {
					token = row.Admission.SnapshotToken
				}
			}
			if token == "" {
				continue
			}
			attempt := bridge.ZoneAttempt{Identity: boundary.Identity(snapshot), Attempt: &c.AttemptKey{ControllerSessionId: proto.String(string(namespace)), ActionId: proto.String(string(v.Action)), AttemptId: proto.Uint64(uint64(v.Attempt))}, Generation: uint64(snapshot.Native), Token: token, Zone: zone}
			lookup, _, err := native.LookupZone(ctx, attempt)
			if err != nil {
				return 0, nil, err
			}
			if lookup.GetReceipt() == nil {
				continue
			}
			observed, _, err := native.ObserveZone(ctx, attempt, lookup.GetReceipt())
			if err != nil {
				return 0, nil, err
			}
			p := observed.GetProgress()
			if p == nil || p.GetCompleted() == nil || !p.GetCompleteInspection() || domain.Tick(p.Context.GetTick()) != facts.Identity.Tick {
				continue
			}
			matches, err := bridge.ZoneMatches(p.GetCompleted().GetEvidence(), zone, token)
			if err != nil || !matches {
				continue
			}
			managed[p.GetCompleted().GetEvidence().GetZone().GetZoneId()] = true
			remaining = max(remaining, uint32(budget-(facts.Identity.Tick-v.Tick)))
		}
	}
	return remaining, managed, nil
}
