package buildingruntime

import (
	"context"
	"crypto/sha256"
	"encoding/json"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

// RoutineProductionPolicySource is the fresh native production-policy read
// RoutineProductionPolicyPlanner performs immediately before proposing a
// method: only ReadProductionPolicy is needed, since the desired
// floors/stopped replacement combines controller defaults and explicit directives
// (explicit directives override each resource in ResourceReserves/
// StoppedResources), without importing native restrictions as intent. The
// native SetProductionPolicy preview, run later by
// productionPolicyBoundary.InspectProductionPolicy at dispatch-inspection
// time, remains the authoritative admission gate -- the same line
// RoutineResearchPlanner/RoutineGearPlanner draw against their own native
// previews.
type RoutineProductionPolicySource interface {
	ReadProductionPolicy(context.Context, *c.Identity) (bridge.ProductionPolicyRead, bridge.Result, error)
}
type RoutineProductionPolicyPlanner struct {
	reviewer *RoutineReviewer
	native   RoutineProductionPolicySource
}
type RoutineProductionPolicyResult struct {
	Reason RoutineBuildingReason
	Plan   domain.PlanID
}

func NewRoutineProductionPolicyPlanner(reviewer *RoutineReviewer, native RoutineProductionPolicySource) (*RoutineProductionPolicyPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, ErrControl
	}
	return &RoutineProductionPolicyPlanner{reviewer, native}, nil
}
func (r *RoutineProductionPolicyPlanner) Step(ctx context.Context) (RoutineProductionPolicyResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, false)
	if err != nil {
		return RoutineProductionPolicyResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

// productionPolicyMatches reports whether the native floors/stopped rows
// already equal the desired replacement, mirroring
// policy.EvaluateProductionPolicy's own comparison: proposing a method whose
// content already matches live state would only be refused later
// (ProductionPolicySatisfied) at real dispatch cost, so the planner checks
// first, the same way RoutineResearchPlanner checks read.CurrentProject.
func productionPolicyMatches(read bridge.ProductionPolicyRead, value domain.ProductionPolicy) bool {
	floors := value.Floors()
	if len(read.Floors) != len(floors) {
		return false
	}
	for _, row := range floors {
		if read.Floors[policy.Resource(row.Resource)] != row.Floor {
			return false
		}
	}
	stopped := value.Stopped()
	if len(read.Stopped) != len(stopped) {
		return false
	}
	seen := make(map[string]bool, len(read.Stopped))
	for _, name := range read.Stopped {
		seen[string(name)] = true
	}
	for _, name := range stopped {
		if !seen[name] {
			return false
		}
	}
	return true
}

func (r *RoutineProductionPolicyPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoutineProductionPolicyResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutineProductionPolicyResult{Reason: BuildingMethodDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoutineProductionPolicyResult{}, ErrControl
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutineProductionPolicyResult{}, err
	}
	if !review.Enabled || review.Snapshot != state.Snapshot {
		return RoutineProductionPolicyResult{Reason: BuildingMethodNoReview}, nil
	}
	var goal store.GoalState
	found := false
	for _, binding := range review.Goals {
		if binding.Need == policy.ProductionPolicy {
			goal, err = p.journal.LoadGoal(call, binding.Goal)
			found = true
			break
		}
	}
	if err != nil {
		return RoutineProductionPolicyResult{}, err
	}
	if !found || goal.Goal.Status != domain.GoalActive || goal.Goal.Need != domain.NeedDeficit {
		return RoutineProductionPolicyResult{Reason: BuildingMethodNoDeficit}, nil
	}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoutineProductionPolicyResult{}, err
		}
		if domain.GoalWorkOpen(plan.Progress) {
			return RoutineProductionPolicyResult{Reason: BuildingMethodExistingWork}, nil
		}
	}
	identity := boundary.Identity(state.Snapshot)
	read, _, err := r.native.ReadProductionPolicy(call, identity)
	if err != nil {
		return RoutineProductionPolicyResult{}, err
	}
	if _, err = boundary.Context(read.Context, state.Snapshot); err != nil || read.Context.GetTick() < int64(review.Tick) {
		return RoutineProductionPolicyResult{}, ErrControl
	}
	floors, stopped, err := policy.ProductionFloors(r.reviewer.policy.ResourceReserves, r.reviewer.policy.StoppedResources)
	if err != nil {
		return RoutineProductionPolicyResult{}, err
	}
	rows := make([]domain.ResourceFloor, 0, len(floors))
	for name, floor := range floors {
		rows = append(rows, domain.ResourceFloor{Resource: string(name), Floor: floor})
	}
	stoppedNames := make([]string, 0, len(stopped))
	for _, name := range stopped {
		stoppedNames = append(stoppedNames, string(name))
	}
	value, err := domain.NewProductionPolicy(rows, stoppedNames)
	if err != nil {
		return RoutineProductionPolicyResult{}, err
	}
	directives, err := p.journal.ResourcePolicies(call, store.World{Colony: state.Snapshot.Colony, Load: state.Snapshot.Load, Map: state.Snapshot.Map})
	if err != nil {
		return RoutineProductionPolicyResult{}, err
	}
	value, err = domain.ResolveProductionPolicy(value, directives)
	if err != nil {
		return RoutineProductionPolicyResult{}, err
	}
	if productionPolicyMatches(read, value) {
		return RoutineProductionPolicyResult{Reason: BuildingMethodUsed}, nil
	}
	digestInput, err := json.Marshal(struct {
		Floors  []domain.ResourceFloor
		Stopped []string
	}{value.Floors(), value.Stopped()})
	if err != nil {
		return RoutineProductionPolicyResult{}, err
	}
	digest := sha256.Sum256(digestInput)
	// A settled write does not own native state forever. Open work above
	// prevents duplicates; the durable method count permits each later repair,
	// including identical drift at the same paused tick/snapshot token.
	methodID := domain.MethodID(fmt.Sprintf("production-policy-%x-%d", digest[:16], goal.Admitted))
	planDigest := sha256.Sum256([]byte(fmt.Sprintf("%s/%d/%s", goal.Goal.ID, goal.Goal.Epoch, methodID)))
	id := domain.PlanID(fmt.Sprintf("routine-production-policy-%x", planDigest[:16]))
	action, err := domain.NewProductionPolicyAction(domain.ActionID(fmt.Sprintf("%s-0", id)), value)
	if err != nil {
		return RoutineProductionPolicyResult{}, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoutineProductionPolicyResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutineProductionPolicyResult{}, err
	}
	if p.session.State() != state {
		return RoutineProductionPolicyResult{}, ErrControl
	}
	if _, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, methodID, plan); err != nil {
		return RoutineProductionPolicyResult{}, err
	}
	return RoutineProductionPolicyResult{Reason: BuildingMethodAdmitted, Plan: id}, nil
}
