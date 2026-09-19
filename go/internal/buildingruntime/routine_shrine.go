package buildingruntime

import (
	"context"
	"crypto/sha256"
	"fmt"
	"sort"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// RoutineShrineSource is the shrine census plus the readiness reads (#457)
// the breach goal judges from.
type RoutineShrineSource interface {
	observation.ColonySource
	observation.ShrineSource
	shrineReadinessNative
}

// RoutineShrinePlanner composes the ClearAncientShrine goal's one method
// (#458): when readiness reads Ready for a sealed shrine touching Home it
// drafts the squad to standing cells behind the trap line and designates
// the chosen wall for an in-place deconstruction. The wall falling is the
// method's end: the plan has no open work, the worker releases the owned
// drafts and ActiveCombat answers the guards. Ranged breaching is not
// composed; it needs an attack-building order.
type RoutineShrinePlanner struct {
	reviewer *RoutineReviewer
	native   RoutineShrineSource
}
type RoutineShrineResult struct {
	Reason RoutineBuildingReason
	Plan   domain.PlanID
	// Hold is the readiness reason the planner held on (BuildingMethodHeld)
	// and Shrine the shrine it judged.
	Hold, Shrine string
}

// BuildingMethodHeld is the shrine planner's answer while every target
// shrine holds; RoutineShrineResult.Hold carries the reason.
const BuildingMethodHeld RoutineBuildingReason = "breach_held"

func NewRoutineShrinePlanner(reviewer *RoutineReviewer, native RoutineShrineSource) (*RoutineShrinePlanner, error) {
	if reviewer == nil || native == nil {
		return nil, ErrControl
	}
	return &RoutineShrinePlanner{reviewer, native}, nil
}
func (r *RoutineShrinePlanner) Step(ctx context.Context) (RoutineShrineResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, false)
	if err != nil {
		return RoutineShrineResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}
func (r *RoutineShrinePlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoutineShrineResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutineShrineResult{Reason: BuildingMethodDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoutineShrineResult{}, ErrControl
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutineShrineResult{}, err
	}
	if !review.Enabled || !review.Snapshot.Matches(state.Snapshot) {
		return RoutineShrineResult{Reason: BuildingMethodNoReview}, nil
	}
	var goal store.GoalState
	found := false
	for _, binding := range review.Goals {
		if binding.Need == policy.ClearAncientShrine {
			goal, err = p.journal.LoadGoal(call, binding.Goal)
			found = true
			break
		}
	}
	if err != nil {
		return RoutineShrineResult{}, err
	}
	if !found || goal.Goal.Status != domain.GoalActive || goal.Goal.Need != domain.NeedDeficit {
		return RoutineShrineResult{Reason: BuildingMethodNoDeficit}, nil
	}
	selected := false
	for _, row := range review.Development.Rows {
		selected = selected || row.Goal == policy.ClearAncientShrine && row.Selected
	}
	if !selected {
		return RoutineShrineResult{Reason: BuildingMethodRefused}, nil
	}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoutineShrineResult{}, err
		}
		if domain.GoalWorkOpen(plan.Progress) {
			return RoutineShrineResult{Reason: BuildingMethodExistingWork}, nil
		}
	}
	started := r.reviewer.clock.Now()
	identity, _, err := r.native.Identity(call)
	if err != nil {
		return RoutineShrineResult{}, err
	}
	expected, err := observation.DecodeIdentity(identity)
	if err != nil {
		return RoutineShrineResult{}, err
	}
	if !routineBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoutineShrineResult{}, ErrControl
	}
	colony, err := r.reviewer.observeColony(call, r.native, expected, nil)
	if err != nil {
		return RoutineShrineResult{}, err
	}
	read, err := observation.ObserveShrines(call, r.native, expected)
	if err != nil {
		return RoutineShrineResult{}, err
	}
	shrines, known := read.Value()
	if !known {
		return RoutineShrineResult{Reason: BuildingMethodUnknown}, nil
	}
	targets := map[string]bool{}
	for _, id := range policy.ShrineClearanceTargets(shrines) {
		targets[id] = true
	}
	var candidates []policy.AncientShrine
	for _, shrine := range shrines {
		if targets[shrine.ID] {
			candidates = append(candidates, shrine)
		}
	}
	if len(candidates) == 0 {
		return RoutineShrineResult{Reason: BuildingMethodNoDeficit}, nil
	}
	sort.Slice(candidates, func(i, j int) bool { return candidates[i].ID < candidates[j].ID })
	reports, err := shrineReadiness(call, r.native, boundary.Identity(state.Snapshot), candidates, nil, colony.Projection.Threat.RaidPoints, colony.Projection.Center, colony.Projection.Bounds)
	if err != nil {
		return RoutineShrineResult{}, err
	}
	held := RoutineShrineResult{Reason: BuildingMethodHeld}
	for i, report := range reports {
		shrine := candidates[i]
		reason := policy.ShrineHoldReason(shrine, report.Readiness)
		if reason != policy.ShrineReady {
			if held.Hold == "" {
				held.Hold, held.Shrine = reason, shrine.ID
			}
			continue
		}
		return r.breach(call, epoch, state, goal, shrine, report, colony.Projection, started, arbiter)
	}
	return held, nil
}

// breach commits one sealed shrine's method: an owned draft and a move to a
// standing cell behind the trap line for each drafted defender, and the
// breach deconstruction of the chosen wall. One colonist is always left
// undrafted for the deconstruct job. The method is retried at most
// maxMedicalAttemptsPerPatient times per wall and goal epoch.
func (r *RoutineShrinePlanner) breach(call, epoch context.Context, state ControlState, goal store.GoalState, shrine policy.AncientShrine, report ShrineReadinessReport, projection observation.ColonyProjection, started time.Time, arbiter *stepArbiter) (RoutineShrineResult, error) {
	p := r.reviewer.player
	wall := report.Readiness.Wall
	colonists, known := projection.Facts.Colonists.Value()
	if !known {
		return RoutineShrineResult{Reason: BuildingMethodUnknown}, nil
	}
	drafted := policy.ShrineBreachDrafts(report.Readiness.Squad, int(colonists))
	if !arbiter.tryClaim(drafted) {
		return RoutineShrineResult{Reason: BuildingMethodUsed}, nil
	}
	positions := policy.ShrineBreachPositions(wall, drafted, report.Standing, report.Traps)
	prefix := fmt.Sprintf("breach-%s-%s-", shrine.ID, wall.EntityID)
	attempt := medicalAttemptCount(goal.Methods, goal.Goal.Epoch, prefix)
	if attempt >= maxMedicalAttemptsPerPatient {
		return RoutineShrineResult{Reason: BuildingMethodExhausted, Shrine: shrine.ID}, nil
	}
	method := domain.MethodID(fmt.Sprintf("%s%d", prefix, attempt))
	digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%d/%s", goal.Goal.ID, goal.Goal.Epoch, method)))
	id := domain.PlanID(fmt.Sprintf("routine-shrine-%x", digest[:16]))
	var actions []domain.Action
	for _, defender := range drafted {
		draftID := domain.ActionID(fmt.Sprintf("%s-draft-%s", id, defender))
		draft, err := domain.NewOwnedDraft(defender)
		if err != nil {
			return RoutineShrineResult{}, err
		}
		draftAction, err := domain.NewOwnedDraftAction(draftID, draft)
		if err != nil {
			return RoutineShrineResult{}, err
		}
		actions = append(actions, draftAction)
		cell, ok := positions[defender]
		if !ok {
			continue
		}
		movement, err := domain.NewMovement(defender, cell, draftID)
		if err != nil {
			return RoutineShrineResult{}, err
		}
		moveAction, err := domain.NewMovementAction(domain.ActionID(fmt.Sprintf("%s-move-%s", id, defender)), movement)
		if err != nil {
			return RoutineShrineResult{}, err
		}
		actions = append(actions, moveAction)
	}
	definition := wall.DefName
	if definition == "" {
		definition = "Wall"
	}
	value, err := domain.NewBreachDeconstruction(wall.EntityID, definition, wall.Cell)
	if err != nil {
		return RoutineShrineResult{}, err
	}
	breachID := domain.ActionID(fmt.Sprintf("%s-breach", id))
	breachAction, err := domain.NewDeconstructionAction(breachID, value)
	if err != nil {
		return RoutineShrineResult{}, err
	}
	actions = append(actions, breachAction)
	// The wall goes only once every drafted defender stands: the breach
	// waits on each draft (and each move when a cell was found).
	var dependencies []domain.ActionDependency
	for _, action := range actions[:len(actions)-1] {
		dependencies = append(dependencies, domain.ActionDependency{Action: breachID, Requires: action.ID()})
	}
	plan, err := domain.NewPlan(id, 1, actions, dependencies...)
	if err != nil {
		return RoutineShrineResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutineShrineResult{}, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoutineShrineResult{}, ErrControl
	}
	if _, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, method, plan); err != nil {
		return RoutineShrineResult{}, err
	}
	return RoutineShrineResult{Reason: BuildingMethodAdmitted, Plan: id, Shrine: shrine.ID}, nil
}
