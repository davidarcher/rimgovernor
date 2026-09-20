package buildingruntime

import (
	"context"
	"crypto/sha256"
	"encoding/json"
	"errors"
	"fmt"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

// RoutineWorkBenchSource is the optional fresh bench census a work planner
// resolves open production bills against; without it bills contribute no
// work requirement (Construction alone, the pre-bill behaviour).
type RoutineWorkBenchSource interface {
	ReadGearBenches(context.Context, *c.Identity) ([]bridge.GearBenchRead, bridge.Result, error)
}

type RoutineWorkPlanner struct {
	reviewer *RoutineReviewer
	benches  RoutineWorkBenchSource
}
type RoutineWorkResult struct {
	Reason RoutineBuildingReason
	Plan   domain.PlanID
}

func NewRoutineWorkPlanner(reviewer *RoutineReviewer) (*RoutineWorkPlanner, error) {
	if reviewer == nil {
		return nil, ErrControl
	}
	benches, _ := reviewer.native.(RoutineWorkBenchSource)
	return &RoutineWorkPlanner{reviewer: reviewer, benches: benches}, nil
}
func (r *RoutineWorkPlanner) Step(ctx context.Context) (RoutineWorkResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, false)
	if err != nil {
		return RoutineWorkResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}
func (r *RoutineWorkPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoutineWorkResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutineWorkResult{Reason: BuildingMethodDisabled}, nil
	}
	if !state.ObservationKnown {
		return RoutineWorkResult{}, ErrControl
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutineWorkResult{}, err
	}
	if !review.Enabled || review.Snapshot != state.Snapshot {
		return RoutineWorkResult{Reason: BuildingMethodNoReview}, nil
	}
	var goal store.GoalState
	deficit := false
	for _, binding := range review.Goals {
		switch binding.Need {
		case policy.EnsureWorkAssignments:
			goal, err = p.journal.LoadGoal(call, binding.Goal)
		case policy.MaintainResource:
			// A resource deficit needs its bench work type covered before
			// the bill can be admitted natively (routineDeficitWork).
			var resource store.GoalState
			resource, err = p.journal.LoadGoal(call, binding.Goal)
			deficit = err == nil && resource.Goal.Status == domain.GoalActive && resource.Goal.Need == domain.NeedDeficit
		}
		if err != nil {
			return RoutineWorkResult{}, err
		}
	}
	if goal.Goal.Status != domain.GoalActive || goal.Goal.Need != domain.NeedDeficit {
		return RoutineWorkResult{Reason: BuildingMethodNoDeficit}, nil
	}
	// Open work no longer gates the fresh decision outright: an undispatched
	// action whose premise moved (the pawn's settings token, or what the
	// policy now wants for the pawn) is cancelled below so the goal can
	// re-plan instead of holding the stale plan forever (#305).
	var open []store.PlanState
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoutineWorkResult{}, err
		}
		if domain.GoalWorkOpen(plan.Progress) {
			open = append(open, plan)
		}
	}
	existing := func(unknown RoutineWorkResult) RoutineWorkResult {
		if len(open) > 0 {
			return RoutineWorkResult{Reason: BuildingMethodExistingWork}
		}
		return unknown
	}
	preferences, err := p.journal.LoadWorkPreferences(call, state.Snapshot.Plan)
	if errors.Is(err, store.ErrNotFound) {
		preferences = store.WorkPreferences{Plan: state.Snapshot.Plan, World: playerWorld(state.Snapshot)}
		err = nil
	}
	if err != nil {
		return RoutineWorkResult{}, err
	}
	if preferences.World != playerWorld(state.Snapshot) || preferences.Revision != review.WorkPreferenceRevision {
		return RoutineWorkResult{}, ErrControl
	}
	plans, err := p.journal.LoadPlans(call, 256)
	if err != nil {
		return RoutineWorkResult{}, err
	}
	playerPlans, err := p.journal.PlayerPlans(call, playerWorld(state.Snapshot))
	if err != nil {
		return RoutineWorkResult{}, err
	}
	definitions := routineProjectDefinitions(plans, state.Snapshot, playerPlans)
	expected, err := routineScope(call, r.reviewer.native)
	if err != nil {
		return RoutineWorkResult{}, err
	}
	if !routineBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoutineWorkResult{}, ErrControl
	}
	claims, err := p.journal.ConstructionClaims(call, state.Snapshot, expected.Tick)
	if err != nil {
		return RoutineWorkResult{}, err
	}
	read, err := r.reviewer.observeOwned(call, r.reviewer.native, expected, claims, definitions...)
	if err != nil {
		return RoutineWorkResult{}, err
	}
	pawns, known := read.Projection.WorkPawns.Value()
	if !known {
		return existing(RoutineWorkResult{Reason: BuildingMethodUnknown}), nil
	}
	required, known := routineProjectWork(definitions, read.Projection.Definitions).Value()
	if !known {
		return existing(RoutineWorkResult{Reason: BuildingMethodUnknown}), nil
	}
	resourceTargets, err := r.reviewer.resourceTargets(call, state.Snapshot, read.Projection.Facts.Resources)
	if err != nil {
		return RoutineWorkResult{}, err
	}
	targets := routineDeficitTargets(resourceTargets, deficit, read.Projection.Facts.Gear)
	benchWork, err := routineBenchWork(call, r.benches, state.Snapshot, plans, playerPlans, targets, len(targets) > 0)
	if err != nil {
		return RoutineWorkResult{}, err
	}
	if rows, known := benchWork.Value(); !known {
		return existing(RoutineWorkResult{Reason: BuildingMethodUnknown}), nil
	} else {
		required = mergeWorkRequirements(required, rows)
	}
	needs, err := routineResearchNeeds(call, p.journal, r.reviewer.policy, state.Snapshot)
	if err != nil {
		return RoutineWorkResult{}, err
	}
	needs = r.reviewer.fishingResearchNeeds(needs)
	required = mergeWorkRequirements(required, routineResearchWork(policy.ArmorResearchPolicy(r.reviewer.policy, review.Latches.Soldiers), needs, read.Projection.Facts.Research))
	required = mergeWorkRequirements(required, fishingWork(read.Projection))
	demand, err := routineDiseaseDemand(read.Projection, len(definitions) > 0, review, state.Snapshot)
	if err != nil {
		return RoutineWorkResult{}, err
	}
	decision, err := policy.PlanWork(pawns, required, preferences.Overrides, demand)
	if err != nil {
		return RoutineWorkResult{}, err
	}
	_, known = decision.Capacity.Value()
	if !known {
		return existing(RoutineWorkResult{Reason: BuildingMethodUnknown}), nil
	}
	byID := map[policy.PawnID]policy.WorkPawn{}
	for _, pawn := range pawns {
		byID[pawn.ID] = pawn
	}
	// The schedule planner rides the same PatchPawn write: a pawn whose
	// timetable differs from its role template gets the timetable in the
	// same assignment as its priorities (or alone), under the same token.
	schedules := map[policy.PawnID][]string{}
	for _, row := range policy.PlanSchedules(pawns).Schedules {
		if !row.Matches {
			schedules[row.Pawn] = row.Slots
		}
	}
	var work []domain.WorkAssignment
	for _, assignment := range decision.Assignments {
		pawn := byID[assignment.Pawn]
		token, tk := pawn.SnapshotToken.Value()
		manual, mk := pawn.Manual.Value()
		if _, ck := pawn.Work.Value(); !tk || !mk || !ck {
			continue
		}
		if defs := policy.FoodPolicyChanges(pawn); len(defs) > 0 {
			w, err := domain.NewFoodAssignment(domain.PawnID(pawn.ID), token, defs)
			if err != nil {
				return RoutineWorkResult{}, err
			}
			work = append(work, w)
			continue
		}
		changed, ok := policy.WorkChanges(pawn, assignment)
		if policy.DrugPolicyChange(pawn) {
			w, err := domain.NewDrugPolicyAssignment(domain.PawnID(pawn.ID), token, policy.SocialDrugPolicyName)
			if err != nil {
				return RoutineWorkResult{}, err
			}
			work = append(work, w)
			continue
		}
		if !ok {
			return RoutineWorkResult{}, ErrControl
		}
		schedule := schedules[assignment.Pawn]
		if len(changed) == 0 && len(schedule) == 0 {
			continue
		}
		var w domain.WorkAssignment
		if len(schedule) == 0 {
			w, err = domain.NewWorkAssignment(domain.PawnID(assignment.Pawn), token, manual, changed)
		} else {
			w, err = domain.NewScheduleAssignment(domain.PawnID(assignment.Pawn), token, manual, changed, schedule)
		}
		if err != nil {
			return RoutineWorkResult{}, err
		}
		work = append(work, w)
	}
	for _, plan := range open {
		if err := cancelStaleWorkActions(call, p.journal, plan, work); err != nil {
			return RoutineWorkResult{}, err
		}
		if plan, err = p.journal.LoadPlan(call, plan.Spec.ID()); err != nil {
			return RoutineWorkResult{}, err
		}
		if domain.GoalWorkOpen(plan.Progress) {
			return RoutineWorkResult{Reason: BuildingMethodExistingWork}, nil
		}
	}
	if len(work) == 0 {
		return RoutineWorkResult{Reason: BuildingMethodUnknown}, nil
	}
	// Staleness above judged every pawn; the plan itself carries at most eight.
	if len(work) > 8 {
		work = work[:8]
	}
	// The before-token is part of the identity: the same settings against a
	// pawn whose settings moved under a cancelled plan is a fresh method,
	// not the retired one.
	hash := sha256.New()
	for _, w := range work {
		data, _ := json.Marshal(w.Settings())
		fmt.Fprintf(hash, "drug:%s\n", w.DrugPolicy())
		fmt.Fprintf(hash, "%s/%s/%t/%s\n", w.Pawn(), w.BeforeToken(), w.Manual(), data)
		if defs := w.FoodAllow(); len(defs) > 0 {
			// An identical Manual edit after a completed repair needs another
			// method even when the settings token returns to its old value.
			fmt.Fprintf(hash, "food/%q/%d\n", defs, len(goal.Methods))
		}
	}
	method := domain.MethodID(fmt.Sprintf("work-%x", hash.Sum(nil)[:16]))
	if _, err = p.journal.LoadGoalMethod(call, goal.Goal.ID, goal.Goal.Epoch, method); err == nil {
		return RoutineWorkResult{Reason: BuildingMethodUsed}, nil
	} else if !errors.Is(err, store.ErrNotFound) {
		return RoutineWorkResult{}, err
	}
	digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%d/%s", goal.Goal.ID, goal.Goal.Epoch, method)))
	id := domain.PlanID(fmt.Sprintf("routine-work-%x", digest[:16]))
	var actions []domain.Action
	for i, w := range work {
		action, err := domain.NewWorkAssignmentAction(domain.ActionID(fmt.Sprintf("%s-%d", id, i)), w)
		if err != nil {
			return RoutineWorkResult{}, err
		}
		actions = append(actions, action)
	}
	plan, err := domain.NewPlan(id, 1, actions)
	if err != nil {
		return RoutineWorkResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutineWorkResult{}, err
	}
	if p.session.State() != state {
		return RoutineWorkResult{}, ErrControl
	}
	if _, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, method, plan); err != nil {
		return RoutineWorkResult{}, err
	}
	return RoutineWorkResult{Reason: BuildingMethodAdmitted, Plan: id}, nil
}

// cancelStaleWorkActions cancels every undispatched work assignment on the
// plan whose premise no longer holds against the fresh decision: the pawn's
// settings token or manual mode moved (WorkBoundary.InspectWork would hold
// the write forever), the pawn dropped out of the decision, or the policy
// now wants a different priority for a work type the action sets. A pending
// action the fresh decision still agrees with stays open.
func cancelStaleWorkActions(ctx context.Context, journal *store.Store, plan store.PlanState, fresh []domain.WorkAssignment) error {
	wanted := map[domain.PawnID]domain.WorkAssignment{}
	for _, w := range fresh {
		wanted[w.Pawn()] = w
	}
	assignments := map[domain.ActionID]domain.WorkAssignment{}
	for _, action := range plan.Spec.Actions() {
		if w, ok := action.WorkAssignment(); ok {
			assignments[action.ID()] = w
		}
	}
	for _, progress := range plan.Progress {
		v := progress.View()
		w, ok := assignments[v.Action]
		if !ok || v.Stage != domain.Pending && v.Stage != domain.Prepared {
			continue
		}
		if !workActionStale(w, wanted) {
			continue
		}
		if _, err := journal.Cancel(ctx, plan.Spec.ID(), v.Action); err != nil {
			return err
		}
	}
	return nil
}

func workActionStale(w domain.WorkAssignment, wanted map[domain.PawnID]domain.WorkAssignment) bool {
	now, ok := wanted[w.Pawn()]
	if !ok || now.BeforeToken() != w.BeforeToken() || now.Manual() != w.Manual() {
		return true
	}
	if !slices.Equal(w.FoodAllow(), now.FoodAllow()) || w.DrugPolicy() != now.DrugPolicy() {
		return true
	}
	values := map[string]int32{}
	for _, setting := range now.Settings() {
		values[setting.Definition] = setting.Priority
	}
	for _, setting := range w.Settings() {
		if want, ok := values[setting.Definition]; !ok || want != setting.Priority {
			return true
		}
	}
	return false
}
