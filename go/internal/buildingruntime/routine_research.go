package buildingruntime

import (
	"context"
	"crypto/sha256"
	"errors"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

// RoutineResearchSource is the native research census RoutineResearchPlanner
// reads to find the next prerequisite-ordered project toward
// RoutinePolicy.ResearchTarget. Laboratory/researcher usability
// (policy.UsableResearchLaboratories/EligibleResearchers) is deliberately not
// re-derived here: unlike ResearchProjectFacts.{Hidden,Prerequisites,...},
// the wire ResearchProject message's lab-requirement/CanStart fields are not
// yet decoded into bridge.ResearchRead (an open item alongside the
// Hidden-field approximation ResearchRead's own doc comment discloses), so
// the native SelectResearch preview -- run later, at dispatch-inspection
// time, by researchSelectBoundary.InspectResearchSelect -- remains the
// authoritative admission gate. RoutineGearPlanner draws the same line
// against its own native preview.
type RoutineResearchSource interface {
	ReadResearch(context.Context, *c.Identity) (bridge.ResearchRead, bridge.Result, error)
}
type RoutineResearchPlanner struct {
	reviewer *RoutineReviewer
	native   RoutineResearchSource
}
type RoutineResearchResult struct {
	Reason RoutineBuildingReason
	Plan   domain.PlanID
}

func NewRoutineResearchPlanner(reviewer *RoutineReviewer, native RoutineResearchSource) (*RoutineResearchPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, ErrControl
	}
	return &RoutineResearchPlanner{reviewer, native}, nil
}
func (r *RoutineResearchPlanner) Step(ctx context.Context) (RoutineResearchResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, false)
	if err != nil {
		return RoutineResearchResult{}, err
	}
	defer done()
	return r.step(call, epoch)
}
func (r *RoutineResearchPlanner) step(call, epoch context.Context) (RoutineResearchResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutineResearchResult{Reason: BuildingMethodDisabled}, nil
	}
	target := r.reviewer.policy.ResearchTarget
	if target == "" {
		return RoutineResearchResult{Reason: BuildingMethodDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoutineResearchResult{}, ErrControl
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutineResearchResult{}, err
	}
	if !review.Enabled || review.Snapshot != state.Snapshot {
		return RoutineResearchResult{Reason: BuildingMethodNoReview}, nil
	}
	var goal store.GoalState
	found := false
	for _, binding := range review.Goals {
		if binding.Need == policy.EnsureResearch {
			goal, err = p.journal.LoadGoal(call, binding.Goal)
			found = true
			break
		}
	}
	if err != nil {
		return RoutineResearchResult{}, err
	}
	if !found || goal.Goal.Status != domain.GoalActive || goal.Goal.Need != domain.NeedDeficit {
		return RoutineResearchResult{Reason: BuildingMethodNoDeficit}, nil
	}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoutineResearchResult{}, err
		}
		if domain.GoalWorkOpen(plan.Progress) {
			return RoutineResearchResult{Reason: BuildingMethodExistingWork}, nil
		}
	}
	identity := boundary.Identity(state.Snapshot)
	read, _, err := r.native.ReadResearch(call, identity)
	if err != nil {
		return RoutineResearchResult{}, err
	}
	if _, err = boundary.Context(read.Context, state.Snapshot); err != nil || read.Context.GetTick() < int64(review.Tick) {
		return RoutineResearchResult{}, ErrControl
	}
	if read.CurrentProject != "" {
		return RoutineResearchResult{Reason: BuildingMethodUsed}, nil
	}
	if _, ok := read.Projects[target]; !ok {
		return RoutineResearchResult{Reason: BuildingMethodUnknown}, nil
	}
	finished := make([]policy.ResearchProjectID, len(read.Finished))
	for i, name := range read.Finished {
		finished[i] = policy.ResearchProjectID(name)
	}
	projects := make(map[policy.ResearchProjectID]policy.ResearchProjectFacts, len(read.Projects))
	for name, facts := range read.Projects {
		projects[policy.ResearchProjectID(name)] = facts
	}
	queue, err := policy.ResearchPrerequisiteQueue(projects, finished, []policy.ResearchProjectID{policy.ResearchProjectID(target)})
	if err != nil || len(queue) == 0 {
		return RoutineResearchResult{Reason: BuildingMethodUnknown}, nil
	}
	next := string(queue[0])
	digestNext := sha256.Sum256([]byte(next))
	method := domain.MethodID(fmt.Sprintf("research-%x", digestNext[:16]))
	if _, err = p.journal.LoadGoalMethod(call, goal.Goal.ID, goal.Goal.Epoch, method); err == nil {
		return RoutineResearchResult{Reason: BuildingMethodUsed}, nil
	} else if !errors.Is(err, store.ErrNotFound) {
		return RoutineResearchResult{}, err
	}
	digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%d/%s", goal.Goal.ID, goal.Goal.Epoch, method)))
	id := domain.PlanID(fmt.Sprintf("routine-research-%x", digest[:16]))
	value, err := domain.NewResearchSelect(next)
	if err != nil {
		return RoutineResearchResult{}, err
	}
	action, err := domain.NewResearchSelectAction(domain.ActionID(fmt.Sprintf("%s-0", id)), value)
	if err != nil {
		return RoutineResearchResult{}, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoutineResearchResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutineResearchResult{}, err
	}
	if p.session.State() != state {
		return RoutineResearchResult{}, ErrControl
	}
	if _, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, method, plan); err != nil {
		return RoutineResearchResult{}, err
	}
	return RoutineResearchResult{Reason: BuildingMethodAdmitted, Plan: id}, nil
}
