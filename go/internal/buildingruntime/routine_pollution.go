package buildingruntime

import (
	"context"
	"crypto/sha256"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// RoutinePollutionPlanner composes ManagePollution's methods (#1683) from the
// reviewer's fresh routine census: allow forbidden wastepacks, haul exposed
// ones to storage through the wastepack-guarded HAUL designation, and put the
// polluted ground the window shows into the game's pollution-clear area for
// the cleanup crew. The goal settles on the game's verdicts (every pack
// frozen or atomized, no polluted cell outside the area), never on a plan.
// Where freezer storage stands is #1684's siting; a pack no stockpile takes
// stays exposed and is not re-ordered.
type RoutinePollutionPlanner struct {
	reviewer *RoutineReviewer
}
type RoutinePollutionResult struct {
	Verdict
	Plan domain.PlanID
}

func NewRoutinePollutionPlanner(reviewer *RoutineReviewer) (*RoutinePollutionPlanner, error) {
	if reviewer == nil || reviewer.native == nil {
		return nil, fmt.Errorf("%w: NewRoutinePollutionPlanner: reviewer == nil || reviewer.native == nil", ErrControl)
	}
	return &RoutinePollutionPlanner{reviewer}, nil
}

func (r *RoutinePollutionPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoutinePollutionResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutinePollutionResult{Verdict: BuildingReasonDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoutinePollutionResult{}, fmt.Errorf("%w: step: !state.ObservationKnown || state.Snapshot.Validate() != nil", ErrControl)
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutinePollutionResult{}, err
	}
	if !review.Enabled || !review.Snapshot.Matches(state.Snapshot) {
		return RoutinePollutionResult{Verdict: BuildingReasonNoReview}, nil
	}
	goal, workable, err := p.journal.Workable(call, review, policy.ManagePollution)
	if err != nil {
		return RoutinePollutionResult{}, err
	}
	if !workable {
		return RoutinePollutionResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	// ManagePollution competes for the bounded development capacity like
	// blight and the other priority>=3 autopilot goals.
	selected := false
	for _, row := range review.Development.Rows {
		selected = selected || row.Goal == policy.ManagePollution && row.Selected
	}
	if !selected {
		return RoutinePollutionResult{Verdict: awaitingSlot(string(policy.ManagePollution))}, nil
	}
	// A pack whose haul ended unsuccessful (no stockpile took it) is not
	// re-ordered while it stands.
	claimed := map[string]bool{}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoutinePollutionResult{}, err
		}
		if store.PlanOpen(plan) {
			return RoutinePollutionResult{Verdict: BuildingReasonExistingWork}, nil
		}
		for _, progress := range plan.Progress {
			if haul, ok := progress.Action().WastepackHaul(); ok && progress.View().Stage == domain.Unsuccessful {
				claimed[haul.Thing()] = true
			}
		}
	}
	started := r.reviewer.clock.Now()
	expected, err := stepScope(call, r.reviewer.native)
	if err != nil {
		return RoutinePollutionResult{}, err
	}
	if !routineBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoutinePollutionResult{}, fmt.Errorf("%w: step: !routineBuildingBoundary(expected, state.Snapshot, review.Tick)", ErrControl)
	}
	claims, err := p.journal.ConstructionClaims(call, state.Snapshot, expected.Tick)
	if err != nil {
		return RoutinePollutionResult{}, err
	}
	read, err := r.reviewer.observeOwned(call, r.reviewer.native, expected, claims)
	if err != nil {
		return RoutinePollutionResult{}, err
	}
	facts, known := read.Projection.Facts.Pollution.Value()
	if !known {
		return RoutinePollutionResult{Verdict: fieldUnavailable("pollution")}, nil
	}
	work := policy.SelectPollutionWork(facts, claimed, policy.PollutedWindowCells(read.Projection.Cells))
	if work.Empty() {
		return RoutinePollutionResult{Verdict: waitFor(WaitMethodUsed, "pollution_work")}, nil
	}
	hash := sha256.New()
	for _, w := range work.Allow {
		fmt.Fprintf(hash, "allow/%s\n", w.ID)
	}
	for _, w := range work.Haul {
		fmt.Fprintf(hash, "haul/%s\n", w.ID)
	}
	for _, c := range work.Area {
		fmt.Fprintf(hash, "cell/%d/%d\n", c.X, c.Z)
	}
	method := domain.MethodID(fmt.Sprintf("pollution-%x", hash.Sum(nil)[:16]))
	for _, seen := range goal.Methods {
		if seen.Method == method {
			return RoutinePollutionResult{Verdict: waitFor(WaitMethodUsed, "pollution_method")}, nil
		}
	}
	id := domain.MintPlanID()
	var actions []domain.Action
	next := func() domain.ActionID { return domain.ActionID(fmt.Sprintf("%s-%d", id, len(actions))) }
	if len(work.Area) > 0 {
		area, err := domain.NewPollutionClearArea(domain.AreaSetCells, work.Area)
		if err != nil {
			return RoutinePollutionResult{}, err
		}
		action, err := domain.NewAreaAction(next(), area)
		if err != nil {
			return RoutinePollutionResult{}, err
		}
		actions = append(actions, action)
	}
	for _, w := range work.Allow {
		supply, err := domain.NewSupplyAllow(w.ID, w.Definition, w.Cell)
		if err != nil {
			return RoutinePollutionResult{}, err
		}
		action, err := domain.NewSupplyAllowAction(next(), supply)
		if err != nil {
			return RoutinePollutionResult{}, err
		}
		actions = append(actions, action)
	}
	for _, w := range work.Haul {
		haul, err := domain.NewWastepackHaul(w.ID, w.Definition, w.Cell)
		if err != nil {
			return RoutinePollutionResult{}, err
		}
		action, err := domain.NewWastepackHaulAction(next(), haul)
		if err != nil {
			return RoutinePollutionResult{}, err
		}
		actions = append(actions, action)
	}
	plan, err := domain.NewPlan(id, 1, actions)
	if err != nil {
		return RoutinePollutionResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutinePollutionResult{}, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoutinePollutionResult{}, fmt.Errorf("%w: step: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge", ErrControl)
	}
	if _, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, method, plan); err != nil {
		return RoutinePollutionResult{}, err
	}
	return RoutinePollutionResult{Verdict: BuildingReasonAdmitted, Plan: id}, nil
}
