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
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
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
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoutineFieldResult{}, err
		}
		if domain.GoalWorkOpen(plan.Progress) {
			return RoutineFieldResult{Reason: BuildingMethodExistingWork}, nil
		}
	}
	plans, err := p.journal.LoadPlans(call, 256)
	if err != nil {
		return RoutineFieldResult{}, err
	}
	definitions := append(routineProjectDefinitions(plans, state.Snapshot), "Plant_Rice", "Plant_Potato", "Plant_Corn")
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
	read, err := observation.ObserveRoutineOwned(call, r.reviewer.native, r.reviewer.clock, expected, r.reviewer.maxAge, claims, definitions...)
	if err != nil {
		return RoutineFieldResult{}, err
	}
	projection := read.Projection
	wait, err := r.fieldAllowance(call, goal.Goal, state.Snapshot, projection)
	if err != nil {
		return RoutineFieldResult{}, err
	}
	token, known := projection.ZoneMapToken.Value()
	if !known {
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
	var choices []policy.CropChoice
	for _, d := range projection.Definitions {
		choices = append(choices, policy.CropChoice{Name: d.Name, Available: d.Available, Edible: d.Edible, GrowDays: d.GrowDays, FertilityMin: d.FertilityMin, FertilitySensitivity: d.FertilitySensitivity, HarvestNutrition: d.HarvestNutrition, Demand: d.NutritionDemandPerDay})
	}
	crop, known := policy.ChooseCrop(choices, projection.CropClimate, projection.Facts.FoodDays, projection.Cells, protected)
	if !known {
		return RoutineFieldResult{Reason: BuildingMethodUnknown, NativeWorkTicks: wait}, nil
	}
	coverage := policy.FieldCoverage(projection.Facts.Colonists, projection.FieldCapacityCrops, r.reviewer.policy.FoodTargetDays)
	patches := policy.GrowthFields(projection.Bounds, projection.Center, projection.Cells, protected, crop, policy.FieldTarget(projection.Facts.Colonists, crop, r.reviewer.policy.FoodTargetDays), coverage)
	if len(patches) == 0 {
		return RoutineFieldResult{Reason: BuildingMethodUsed, NativeWorkTicks: wait}, nil
	}
	hash := sha256.New()
	fmt.Fprintf(hash, "%s/%v", crop.Name, patches)
	method := domain.MethodID(fmt.Sprintf("fields-%x", hash.Sum(nil)[:16]))
	if _, err = p.journal.LoadGoalMethod(call, goal.Goal.ID, goal.Goal.Epoch, method); err == nil {
		return RoutineFieldResult{Reason: BuildingMethodUsed, NativeWorkTicks: wait}, nil
	} else if !errors.Is(err, store.ErrNotFound) {
		return RoutineFieldResult{}, err
	}
	digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%d/%s", goal.Goal.ID, goal.Goal.Epoch, method)))
	id := domain.PlanID(fmt.Sprintf("routine-fields-%x", digest[:16]))
	snapshot := state.Snapshot
	snapshot.Plan = id
	snapshot.Revision = 1
	var actions []domain.Action
	var previews []policy.Preview
	for i, patch := range patches {
		var cells []domain.Cell
		for x := patch.X; x < patch.X+patch.Width; x++ {
			for z := patch.Z; z < patch.Z+patch.Height; z++ {
				cells = append(cells, domain.Cell{X: x, Z: z})
			}
		}
		value, err := domain.NewZoneCreate(domain.GrowingZone, crop.Name, cells)
		if err != nil {
			return RoutineFieldResult{}, err
		}
		action, err := domain.NewZoneCreateAction(domain.ActionID(fmt.Sprintf("%s-%d", id, i)), value)
		if err != nil {
			return RoutineFieldResult{}, err
		}
		reply, _, err := r.native.PreviewZone(call, boundary.Identity(snapshot), bridge.ZoneTarget{Zone: value, Token: token})
		if err != nil {
			return RoutineFieldResult{}, err
		}
		v := reply.GetEvaluated()
		if v == nil || !v.GetAccepted() {
			return RoutineFieldResult{Reason: BuildingMethodRefused}, nil
		}
		if _, err = boundary.Context(v.Context, snapshot); err != nil || domain.Tick(v.Context.GetTick()) != projection.Identity.Tick {
			return RoutineFieldResult{}, ErrControl
		}
		actions = append(actions, action)
		previews = append(previews, policy.Preview{Action: action, Snapshot: snapshot, Tick: projection.Identity.Tick, CanPlace: domain.Known(true), SafeToPlace: domain.Known(true), MadeFromStuff: domain.Known(false), WatchCellsAccessible: domain.Known(true), Footprint: domain.Known(cells), Costs: domain.Known([]policy.Amount{})})
	}
	plan, err := domain.NewPlan(id, 1, actions)
	if err != nil {
		return RoutineFieldResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutineFieldResult{}, err
	}
	if p.session.State() != state {
		return RoutineFieldResult{}, ErrControl
	}
	last, _, err := r.reviewer.native.Identity(call)
	if err != nil {
		return RoutineFieldResult{}, err
	}
	actual, err := observation.DecodeIdentity(last)
	if err != nil || !routineBuildingBoundary(actual, state.Snapshot, projection.Identity.Tick) {
		return RoutineFieldResult{}, ErrControl
	}
	now := r.reviewer.clock.Now()
	if now.Before(read.StartedAt) || now.Sub(read.StartedAt) > r.reviewer.maxAge {
		return RoutineFieldResult{}, observation.ErrStale
	}
	decision, err := p.journal.AdmitBuildingMethod(call, store.BuildingMethodRequest{Goal: goal.Goal.ID, Revision: goal.Revision, Method: method, Plan: plan, Current: snapshot, Tick: projection.Identity.Tick, Bounds: domain.Known(projection.Bounds), Stock: policy.StockObservation{Snapshot: snapshot, Tick: projection.Identity.Tick}, Rules: r.reviewer.rules, Previews: previews, Purpose: policy.Routine})
	if err != nil {
		return RoutineFieldResult{}, err
	}
	reason := BuildingMethodRefused
	if decision.Admitted {
		reason = BuildingMethodAdmitted
	}
	return RoutineFieldResult{Reason: reason, Plan: id}, nil
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
func (r *RoutineFieldPlanner) fieldAllowance(ctx context.Context, goal domain.Goal, current domain.GenerationSnapshot, facts observation.ColonyProjection) (uint32, error) {
	native, ok := r.native.(interface {
		LookupZone(context.Context, bridge.ZoneAttempt) (*receipts.LookupReply, bridge.Result, error)
		ObserveZone(context.Context, bridge.ZoneAttempt, *receipts.Receipt) (*receipts.ProgressReply, bridge.Result, error)
	})
	if !ok {
		return 0, nil
	}
	methods, err := r.reviewer.player.journal.LoadGoalMethods(ctx, goal.ID, goal.Epoch)
	if err != nil {
		return 0, err
	}
	namespace, err := r.reviewer.player.journal.Identity(ctx)
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
			if !ok || v.Stage != domain.Completed || v.Unresolved || !v.Snapshot.Matches(snapshot) || v.Tick > facts.Identity.Tick {
				continue
			}
			var budget domain.Tick
			for _, d := range facts.Definitions {
				if d.Name == zone.Crop() {
					days, known := d.GrowDays.Value()
					if known && days > 0 && days <= 24 {
						budget = domain.Tick(math.Ceil(min(60, days*2.5+r.reviewer.policy.FoodTargetDays) * 60000))
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
			attempt := bridge.ZoneAttempt{Identity: boundary.Identity(snapshot), Attempt: &c.AttemptKey{ControllerSessionId: proto.String(string(namespace)), ActionId: proto.String(string(v.Action)), AttemptId: proto.Uint64(uint64(v.Attempt))}, Owner: &a.Owner{ControllerSessionId: proto.String(string(namespace)), PlayerDirection: proto.Uint64(uint64(snapshot.Direction))}, Generation: uint64(snapshot.Native), Token: token, Zone: zone}
			lookup, _, err := native.LookupZone(ctx, attempt)
			if err != nil {
				return 0, err
			}
			if lookup.GetReceipt() == nil {
				continue
			}
			observed, _, err := native.ObserveZone(ctx, attempt, lookup.GetReceipt())
			if err != nil {
				return 0, err
			}
			p := observed.GetProgress()
			if p == nil || p.GetCompleted() == nil || !p.GetCompleteInspection() || domain.Tick(p.Context.GetTick()) != facts.Identity.Tick {
				continue
			}
			matches, err := bridge.ZoneMatches(p.GetCompleted().GetEvidence(), zone, token)
			if err != nil || !matches {
				continue
			}
			remaining = max(remaining, uint32(budget-(facts.Identity.Tick-v.Tick)))
		}
	}
	return remaining, nil
}
