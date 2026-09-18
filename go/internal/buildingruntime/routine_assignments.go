package buildingruntime

import (
	"context"
	"crypto/sha256"
	"encoding/json"
	"errors"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
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
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoutineWorkResult{}, err
		}
		if domain.GoalWorkOpen(plan.Progress) {
			return RoutineWorkResult{Reason: BuildingMethodExistingWork}, nil
		}
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
	identity, _, err := r.reviewer.native.Identity(call)
	if err != nil {
		return RoutineWorkResult{}, err
	}
	expected, err := observation.DecodeIdentity(identity)
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
		return RoutineWorkResult{Reason: BuildingMethodUnknown}, nil
	}
	required, known := routineProjectWork(definitions, read.Projection.Definitions).Value()
	if !known {
		return RoutineWorkResult{Reason: BuildingMethodUnknown}, nil
	}
	benchWork, err := routineBenchWork(call, r.benches, state.Snapshot, plans, playerPlans, r.reviewer.policy.ResourceTargets, deficit)
	if err != nil {
		return RoutineWorkResult{}, err
	}
	if rows, known := benchWork.Value(); !known {
		return RoutineWorkResult{Reason: BuildingMethodUnknown}, nil
	} else {
		required = mergeWorkRequirements(required, rows)
	}
	needs, err := routineResearchNeeds(call, p.journal, r.reviewer.policy, state.Snapshot)
	if err != nil {
		return RoutineWorkResult{}, err
	}
	required = mergeWorkRequirements(required, routineResearchWork(r.reviewer.policy, needs, read.Projection.Facts.Research))
	decision, err := policy.AssignWork(pawns, required, preferences.Overrides)
	if err != nil {
		return RoutineWorkResult{}, err
	}
	_, known = decision.Capacity.Value()
	if !known {
		return RoutineWorkResult{Reason: BuildingMethodUnknown}, nil
	}
	byID := map[policy.PawnID]policy.WorkPawn{}
	for _, pawn := range pawns {
		byID[pawn.ID] = pawn
	}
	var work []domain.WorkAssignment
	for _, assignment := range decision.Assignments {
		pawn := byID[assignment.Pawn]
		token, tk := pawn.SnapshotToken.Value()
		manual, mk := pawn.Manual.Value()
		current, ck := pawn.Work.Value()
		if !tk || !mk || !ck {
			continue
		}
		values := map[policy.WorkType]int{}
		for _, row := range current {
			values[row.Work] = row.Priority
		}
		var changed []domain.WorkSetting
		for _, setting := range assignment.Priorities {
			old, ok := values[setting.Work]
			if !ok {
				return RoutineWorkResult{}, ErrControl
			}
			// Checkbox mode (manual priorities off) only knows enabled (3)
			// or disabled (0): the numbered ranks the policy chooses collapse
			// to that pair, matching how policy.AssignWork judges Matches and
			// what domain.NewWorkAssignment admits for a non-manual pawn.
			want := setting.Priority
			if !manual && want > 0 {
				want = 3
			}
			if old != want {
				changed = append(changed, domain.WorkSetting{Definition: string(setting.Work), Priority: int32(want)})
			}
		}
		if len(changed) == 0 {
			continue
		}
		w, err := domain.NewWorkAssignment(domain.PawnID(assignment.Pawn), token, manual, changed)
		if err != nil {
			return RoutineWorkResult{}, err
		}
		work = append(work, w)
		if len(work) == 8 {
			break
		}
	}
	if len(work) == 0 {
		return RoutineWorkResult{Reason: BuildingMethodUnknown}, nil
	}
	hash := sha256.New()
	for _, w := range work {
		data, _ := json.Marshal(w.Settings())
		fmt.Fprintf(hash, "%s/%t/%s\n", w.Pawn(), w.Manual(), data)
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
