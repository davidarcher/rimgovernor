package buildingruntime

import (
	"context"
	"crypto/sha256"
	"errors"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

type SleepingMethodSource interface {
	observation.ColonySource
	PreviewBuilding(context.Context, domain.Action, domain.GenerationSnapshot) (bridge.BuildingPreview, bridge.Result, error)
}

type SleepingMethodReason string

const (
	SleepingDisabled     SleepingMethodReason = "disabled"
	SleepingNoReview     SleepingMethodReason = "no_current_review"
	SleepingNoDeficit    SleepingMethodReason = "no_active_deficit"
	SleepingExistingWork SleepingMethodReason = "existing_work"
	SleepingUnknown      SleepingMethodReason = "unknown_prerequisite"
	SleepingNoSpace      SleepingMethodReason = "insufficient_verified_space"
	SleepingMethodUsed   SleepingMethodReason = "method_already_used"
	SleepingRefused      SleepingMethodReason = "shared_admission_refused"
	SleepingAdmitted     SleepingMethodReason = "admitted"
)

type SleepingMethodResult struct {
	Reason   SleepingMethodReason
	Decision store.BuildingMethodDecision
}

// RoutineSleepingPlanner compiles one bounded indoor sleeping method under an
// existing reviewed player direction. It creates shared pending work, never
// acquires a lease, dispatches an action or advances the game.
type RoutineSleepingPlanner struct {
	reviewer *RoutineReviewer
	native   SleepingMethodSource
}

func NewRoutineSleepingPlanner(reviewer *RoutineReviewer, native SleepingMethodSource) (*RoutineSleepingPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, ErrControl
	}
	return &RoutineSleepingPlanner{reviewer: reviewer, native: native}, nil
}

func (r *RoutineSleepingPlanner) Step(ctx context.Context) (SleepingMethodResult, error) {
	p := r.reviewer.player
	call, epoch, done, err := p.enter(ctx, false)
	if err != nil {
		return SleepingMethodResult{}, err
	}
	defer done()
	return r.step(call, epoch)
}

// step is also used by the scheduler already holding the same player gate.
func (r *RoutineSleepingPlanner) step(call, epoch context.Context) (SleepingMethodResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return SleepingMethodResult{Reason: SleepingDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil || state.Snapshot.Native == 0 {
		return SleepingMethodResult{}, ErrControl
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return SleepingMethodResult{}, err
	}
	if !review.Enabled || !review.Snapshot.Matches(state.Snapshot) {
		return SleepingMethodResult{Reason: SleepingNoReview}, nil
	}
	var goal store.GoalState
	for _, binding := range review.Goals {
		if binding.Need == policy.EnsureInitialShelter {
			goal, err = p.journal.LoadGoal(call, binding.Goal)
			break
		}
	}
	if err != nil {
		return SleepingMethodResult{}, err
	}
	if goal.Goal.Status != domain.GoalActive || goal.Goal.Need != domain.NeedDeficit {
		return SleepingMethodResult{Reason: SleepingNoDeficit}, nil
	}
	for _, m := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, m.Plan)
		if err != nil {
			return SleepingMethodResult{}, err
		}
		if domain.GoalWorkOpen(plan.Progress) {
			return SleepingMethodResult{Reason: SleepingExistingWork}, nil
		}
	}
	identity, _, err := r.native.Identity(call)
	if err != nil {
		return SleepingMethodResult{}, err
	}
	expected, err := observation.DecodeIdentity(identity)
	if err != nil {
		return SleepingMethodResult{}, err
	}
	if !sleepingBoundary(expected, state.Snapshot, review.Tick) {
		return SleepingMethodResult{}, ErrControl
	}
	reading, err := observation.ObserveColony(call, r.native, r.reviewer.clock, expected, r.reviewer.maxAge, true, []string{"SleepingSpot"})
	if err != nil {
		return SleepingMethodResult{}, err
	}
	facts := reading.Projection
	count, known := facts.Facts.Colonists.Value()
	capacity, capacityKnown := facts.Facts.IndoorCapacity.Value()
	if !known || !capacityKnown || count <= 0 {
		return SleepingMethodResult{Reason: SleepingUnknown}, nil
	}
	if target, known := facts.Facts.HousingTarget.Value(); known {
		count = max(count, target)
	}
	missing := count - capacity
	if missing <= 0 {
		return SleepingMethodResult{Reason: SleepingNoDeficit}, nil
	}
	if missing > 64 {
		return SleepingMethodResult{Reason: SleepingNoSpace}, nil
	}
	available := false
	for _, def := range facts.Definitions {
		if def.Name != "SleepingSpot" {
			continue
		}
		ready, known := def.Available.Value()
		skill, skillKnown := def.ConstructionSkill.Value()
		available = known && ready && skillKnown && skill == 0
	}
	if !available {
		return SleepingMethodResult{Reason: SleepingUnknown}, nil
	}
	method := domain.MethodID(fmt.Sprintf("indoor-sleeping-%d-%d", count, missing))
	if _, err = p.journal.LoadGoalMethod(call, goal.Goal.ID, goal.Goal.Epoch, method); err == nil {
		return SleepingMethodResult{Reason: SleepingMethodUsed}, nil
	} else if !errors.Is(err, store.ErrNotFound) {
		return SleepingMethodResult{}, err
	}
	digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%d/%s", goal.Goal.ID, goal.Goal.Epoch, method)))
	planID := domain.PlanID(fmt.Sprintf("routine-sleep-%x", digest[:16]))
	snapshot := state.Snapshot
	snapshot.Plan = planID
	snapshot.Revision = 1
	held, err := p.journal.BuildingReservations(call, snapshot)
	if err != nil {
		return SleepingMethodResult{}, err
	}
	var protected []domain.Cell
	for _, h := range held {
		v := h.Progress.View()
		effect, known := v.Effect.Value()
		if !v.Unresolved && known && effect == domain.EffectCompleted && v.Tick <= facts.Identity.Tick {
			continue
		}
		protected = append(protected, h.Footprint...)
	}
	var cells []policy.SiteCell
	for _, c := range facts.Cells {
		if roofed, known := c.Roofed.Value(); known && roofed {
			cells = append(cells, c)
		}
	}
	searchRequest := policy.PlacementSearchRequest{Snapshot: snapshot, Tick: facts.Identity.Tick, Bounds: facts.Bounds, Center: facts.Center, Cells: cells, Protected: protected, Environment: policy.PlacementIndoors, Radius: 22, Limit: 64}
	search, err := policy.NewPlacementSearch(searchRequest)
	if err != nil {
		return SleepingMethodResult{}, err
	}
	var selected []policy.Preview
	usedCells := map[domain.Cell]bool{}
	stock := policy.StockObservation{Snapshot: snapshot, Tick: facts.Identity.Tick}
	resources := map[policy.Resource]domain.Fact[int64]{}
	for i, c := range search.Candidates() {
		if err = p.current(call, epoch); err != nil {
			return SleepingMethodResult{}, err
		}
		if p.session.State() != state {
			return SleepingMethodResult{}, ErrControl
		}
		b, err := domain.NewBuilding("SleepingSpot", c, domain.North, "")
		if err != nil {
			return SleepingMethodResult{}, err
		}
		a, err := domain.NewBuildingAction(domain.ActionID(fmt.Sprintf("%s-%d", planID, i)), b)
		if err != nil {
			return SleepingMethodResult{}, err
		}
		preview, _, err := r.native.PreviewBuilding(call, a, snapshot)
		if err != nil {
			return SleepingMethodResult{}, err
		}
		if err = p.current(call, epoch); err != nil {
			return SleepingMethodResult{}, err
		}
		if preview.Preview.Action != a || !preview.Stock.Snapshot.Matches(snapshot) || preview.Stock.Tick != facts.Identity.Tick {
			return SleepingMethodResult{}, ErrControl
		}
		made, known := preview.Preview.MadeFromStuff.Value()
		if !known || made {
			return SleepingMethodResult{Reason: SleepingUnknown}, nil
		}
		choice, ok, err := search.Select("SleepingSpot", "", []policy.Preview{preview.Preview})
		if err != nil {
			return SleepingMethodResult{}, err
		}
		if !ok {
			continue
		}
		footprint, _ := choice.Footprint.Value()
		overlaps := false
		for _, c := range footprint {
			overlaps = overlaps || usedCells[c]
		}
		if overlaps {
			continue
		}
		selected = append(selected, choice)
		for _, c := range footprint {
			usedCells[c] = true
		}
		if len(preview.Stock.Values) > 256 {
			return SleepingMethodResult{}, ErrControl
		}
		seenResources := map[policy.Resource]bool{}
		for _, v := range preview.Stock.Values {
			if seenResources[v.Resource] {
				return SleepingMethodResult{}, ErrControl
			}
			seenResources[v.Resource] = true
			if old, exists := resources[v.Resource]; exists && old != v.Available {
				return SleepingMethodResult{}, ErrControl
			}
			if _, exists := resources[v.Resource]; !exists {
				stock.Values = append(stock.Values, v)
				resources[v.Resource] = v.Available
			}
		}
		if int64(len(selected)) == missing {
			break
		}
	}
	if int64(len(selected)) != missing {
		return SleepingMethodResult{Reason: SleepingNoSpace}, nil
	}
	last, _, err := r.native.Identity(call)
	if err != nil {
		return SleepingMethodResult{}, err
	}
	actual, err := observation.DecodeIdentity(last)
	if err != nil || !sleepingBoundary(actual, state.Snapshot, facts.Identity.Tick) {
		return SleepingMethodResult{}, ErrControl
	}
	now := r.reviewer.clock.Now()
	if now.Before(reading.StartedAt) || now.Sub(reading.StartedAt) > r.reviewer.maxAge {
		return SleepingMethodResult{}, observation.ErrStale
	}
	if err = p.current(call, epoch); err != nil {
		return SleepingMethodResult{}, err
	}
	if p.session.State() != state {
		return SleepingMethodResult{}, ErrControl
	}
	latest, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return SleepingMethodResult{}, err
	}
	if latest.Revision != review.Revision || !latest.Enabled {
		return SleepingMethodResult{}, ErrControl
	}
	actions := make([]domain.Action, len(selected))
	for i, v := range selected {
		actions[i] = v.Action
	}
	plan, err := domain.NewPlan(planID, 1, actions)
	if err != nil {
		return SleepingMethodResult{}, err
	}
	decision, err := p.journal.AdmitBuildingMethod(call, store.BuildingMethodRequest{Goal: goal.Goal.ID, Revision: goal.Revision, Method: method, Plan: plan, Current: snapshot, Tick: facts.Identity.Tick, Bounds: domain.Known(facts.Bounds), Stock: stock, Previews: selected, Purpose: policy.Routine})
	if err != nil {
		return SleepingMethodResult{}, err
	}
	reason := SleepingRefused
	if decision.Admitted {
		reason = SleepingAdmitted
	}
	return SleepingMethodResult{Reason: reason, Decision: decision}, nil
}

func sleepingBoundary(actual observation.Identity, expected domain.GenerationSnapshot, tick domain.Tick) bool {
	paused, known := actual.Paused.Value()
	generation, generationKnown := actual.NativeGeneration.Value()
	return actual.Colony == expected.Colony && actual.Load == expected.Load && actual.Map == expected.Map && actual.Tick == tick && known && paused && generationKnown && generation == expected.Native
}
