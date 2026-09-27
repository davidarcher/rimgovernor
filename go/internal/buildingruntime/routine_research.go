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
	snap "github.com/davidarcher/RimGovernor/go/internal/snapshot"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

// RoutineResearchSource is the native research census RoutineResearchPlanner
// reads to find the next prerequisite-ordered project toward the goal
// policy.ResearchGoal resolves: the
// project the workshop ladder recorded as gating a MaintainResource bench,
// else the next unfinished rung of RoutinePolicy.ResearchLadder (#230).
// Laboratory/researcher usability
// (policy.UsableResearchLaboratories/EligibleResearchers) is deliberately not
// re-derived here: unlike ResearchProjectFacts.{Hidden,Prerequisites,...},
// the wire ResearchProject message's lab-requirement/CanStart fields are not
// yet decoded into bridge.ResearchRead (an open item alongside the
// Hidden-field approximation ResearchRead's own doc comment discloses), so
// native validation of the ResearchIntent when it applies remains the
// authoritative gate. RoutineGearPlanner draws the same line
// against its own native preview.
type RoutineResearchSource interface {
	ReadResearch(context.Context, *c.Identity) (bridge.ResearchRead, bridge.Result, error)
}
type RoutineResearchPlanner struct {
	reviewer *RoutineReviewer
	native   RoutineResearchSource
	// building is the facility ladder the planner walks when the next rung
	// is locked only for lack of a research bench (#254): furnish a hosting
	// room with the simple bench, else stage a starter shell first. Nil
	// when the source cannot preview placements; the hold is then reported
	// as BuildingResearchBench instead of built around.
	building *RoutineBuildingPlanner
}

// BuildingResearchBench: the next rung is selectable but for a research
// bench nobody has built, and this planner has no building ladder to
// stage one.
const BuildingResearchBench RoutineBuildingReason = "research_bench_needed"

// BuildingResearchBenchUnavailable: the simple research bench definition
// is not buildable in the planning census (research-gated, or a builder
// skill no colonist has).
const BuildingResearchBenchUnavailable RoutineBuildingReason = "research_bench_unavailable"

type RoutineResearchResult struct {
	Reason RoutineBuildingReason
	Plan   domain.PlanID
	// NativeWorkTicks asks the clock for ticks while the selected project
	// is researched: nothing else owes the window a plan meanwhile.
	NativeWorkTicks uint32
}

// researchNativeWorkTicks is the window a current research project asks
// for per step; the review re-reads progress between windows.
const researchNativeWorkTicks = 2500

func NewRoutineResearchPlanner(reviewer *RoutineReviewer, native RoutineResearchSource) (*RoutineResearchPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, fmt.Errorf("%w: NewRoutineResearchPlanner: reviewer == nil || native == nil", ErrControl)
	}
	planner := &RoutineResearchPlanner{reviewer: reviewer, native: native}
	if source, ok := native.(RoutineBuildingSource); ok {
		if _, rooms := native.(observation.RoutineSource); rooms {
			planner.building = &RoutineBuildingPlanner{reviewer: reviewer, native: source, goal: policy.EnsureResearch, definition: "Wall", shelter: true}
		}
	}
	return planner, nil
}

// selectResearchBench resolves the building ladder's definition: the simple
// research bench, placed indoors in a Laboratory-hosting room, when the
// planning census lists it buildable.
func (r *RoutineBuildingPlanner) selectResearchBench(facts observation.ColonyProjection) (*RoutineBuildingPlanner, RoutineBuildingReason, error) {
	var definition *observation.PlanningDefinition
	for i := range facts.Definitions {
		if facts.Definitions[i].Name == policy.ResearchBenchDefinition {
			definition = &facts.Definitions[i]
		}
	}
	if definition == nil {
		return nil, BuildingMethodUnknown, nil
	}
	available, known := definition.Available.Value()
	if !known {
		return nil, BuildingMethodUnknown, nil
	}
	if !available {
		return nil, BuildingResearchBenchUnavailable, nil
	}
	facility, err := policy.Facility(policy.RoomRoleLaboratory)
	if err != nil {
		return nil, "", err
	}
	resolved := *r
	resolved.definition = policy.ResearchBenchDefinition
	resolved.environment = policy.PlacementIndoors
	resolved.facility = &facility
	if stuff, known := definition.Stuff.Value(); known {
		resolved.stuff = stuff
	}
	return &resolved, "", nil
}

// bench walks the building ladder for the research bench, or reports the
// hold when none is composed.
func (r *RoutineResearchPlanner) bench(call, epoch context.Context, arbiter *stepArbiter) (RoutineResearchResult, error) {
	if r.building == nil {
		return RoutineResearchResult{Reason: BuildingResearchBench}, nil
	}
	result, err := r.building.step(call, epoch, arbiter)
	if err != nil {
		return RoutineResearchResult{}, err
	}
	return RoutineResearchResult{Reason: result.Reason, NativeWorkTicks: result.NativeWorkTicks}, nil
}

func (r *RoutineResearchPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoutineResearchResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutineResearchResult{Reason: BuildingMethodDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoutineResearchResult{}, fmt.Errorf("%w: step: !state.ObservationKnown || state.Snapshot.Validate() != nil", ErrControl)
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutineResearchResult{}, err
	}
	if !review.Enabled || review.Snapshot != state.Snapshot {
		return RoutineResearchResult{Reason: BuildingMethodNoReview}, nil
	}
	call, recorded := recordPlannerStep(call, policy.EnsureResearch, state.Snapshot, review.Tick)
	defer recorded()
	// The ladder is paced by the colony stage (#630): a Foothold colony
	// walks its first rungs, a Development colony the whole ladder.
	staged := r.reviewer.staged()
	needs, err := routineResearchNeeds(call, p.journal, staged, state.Snapshot)
	if err != nil {
		return RoutineResearchResult{}, err
	}
	needs = r.reviewer.fishingResearchNeeds(needs)
	needs = policy.DeepDrillingResearch(needs, review.ResourceRunwayState())
	if len(needs) == 0 && len(staged.ResearchLadder) == 0 {
		return RoutineResearchResult{Reason: BuildingMethodDisabled}, nil
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
	deficit := found && goal.Goal.Status == domain.GoalActive && goal.Goal.Need == domain.NeedDeficit && review.Veto(goal.Goal) == ""
	if deficit {
		for _, method := range goal.Methods {
			plan, err := p.journal.LoadPlan(call, method.Plan)
			if err != nil {
				return RoutineResearchResult{}, err
			}
			if store.PlanOpen(plan) {
				return RoutineResearchResult{Reason: BuildingMethodExistingWork}, nil
			}
		}
	}
	identity := boundary.Identity(state.Snapshot)
	read, _, err := r.native.ReadResearch(call, identity)
	if err != nil {
		return RoutineResearchResult{}, err
	}
	if _, err = boundary.Context(read.Context, state.Snapshot); err != nil || read.Context.GetTick() < int64(review.Tick) {
		return RoutineResearchResult{}, fmt.Errorf("%w: step: err != nil || read.Context.GetTick() < int64(review.Tick)", ErrControl)
	}
	if read.CurrentProject != "" {
		// A current project finishes on native ticks alone: a derived or
		// roadmap goal owes the window ticks until it does, whether the
		// review still shows a deficit (a workshop need) or the current
		// project recovered it (a ladder rung, or a player's own choice).
		// Unless no bench stands to research it at: the game lets a project
		// that names no bench be selected, but nobody progresses it, so the
		// bench is owed first (#254).
		if deficit && policy.ResearchBenchNeeded(read.Projects[read.CurrentProject]) {
			return r.bench(call, epoch, arbiter)
		}
		return RoutineResearchResult{Reason: BuildingMethodUsed, NativeWorkTicks: researchNativeWorkTicks}, nil
	}
	if !deficit {
		return RoutineResearchResult{Reason: BuildingMethodNoDeficit}, nil
	}
	inputs := snap.ResearchCall{Policy: policy.ArmorResearchPolicy(staged, review.Latches.Soldiers), Needs: needs, Read: read}
	snap.NoteResearch(call, inputs)
	next, reason := researchNext(inputs)
	if reason != "" {
		return RoutineResearchResult{Reason: reason}, nil
	}
	// A rung locked only for lack of a bench is a building need, not a
	// selection: native refuses the ResearchIntent until the bench stands
	// (#254). The ladder's plans are this goal's methods, so an open bench
	// build reads as existing work above and the selection follows it.
	if policy.ResearchBenchNeeded(read.Projects[next]) {
		return r.bench(call, epoch, arbiter)
	}
	digestNext := sha256.Sum256([]byte(next))
	method := domain.MethodID(fmt.Sprintf("research-%x", digestNext[:16]))
	if _, err = p.journal.LoadGoalMethod(call, goal.Goal.ID, goal.Goal.Epoch, method); err == nil {
		return RoutineResearchResult{Reason: BuildingMethodUsed}, nil
	} else if !errors.Is(err, store.ErrNotFound) {
		return RoutineResearchResult{}, err
	}
	id := domain.MintPlanID("routine-research")
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
		return RoutineResearchResult{}, fmt.Errorf("%w: step: p.session.State() != state", ErrControl)
	}
	if _, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, method, plan); err != nil {
		return RoutineResearchResult{}, err
	}
	return RoutineResearchResult{Reason: BuildingMethodAdmitted, Plan: id}, nil
}

// researchNext is the project a research step selects from its fresh
// census: the goal is the first recorded need the census lists and has not
// finished, else the first such ladder rung, and the step selects the first
// unfinished prerequisite on the way to it. A non-empty reason is the
// step's outcome instead.
func researchNext(in snap.ResearchCall) (string, RoutineBuildingReason) {
	read := in.Read
	finished := make([]policy.ResearchProjectID, len(read.Finished))
	for i, name := range read.Finished {
		finished[i] = policy.ResearchProjectID(name)
	}
	facts := policy.ResearchFacts{Current: policy.ResearchProjectID(read.CurrentProject), Finished: finished}
	for name := range read.Projects {
		facts.Projects = append(facts.Projects, policy.ResearchProjectID(name))
	}
	target, _ := policy.ResearchGoal(in.Policy, in.Needs, domain.Known(facts))
	if target == "" {
		return "", BuildingMethodNoDeficit
	}
	if _, ok := read.Projects[target]; !ok {
		return "", BuildingMethodUnknown
	}
	projects := make(map[policy.ResearchProjectID]policy.ResearchProjectFacts, len(read.Projects))
	for name, facts := range read.Projects {
		projects[policy.ResearchProjectID(name)] = facts
	}
	queue, err := policy.ResearchPrerequisiteQueue(projects, finished, []policy.ResearchProjectID{policy.ResearchProjectID(target)})
	if err != nil || len(queue) == 0 {
		return "", BuildingMethodUnknown
	}
	return string(queue[0]), ""
}
