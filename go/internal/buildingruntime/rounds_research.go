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

// RoundsResearchSource is the native research census RoundsResearchPlanner
// reads to find the next prerequisite-ordered project toward the goal
// policy.ResearchGoal resolves: the
// project the workshop ladder recorded as gating a MaintainResource bench,
// else the next unfinished rung of RoundsPolicy.ResearchLadder.
// Laboratory/researcher usability
// (policy.UsableResearchLaboratories/EligibleResearchers) is deliberately not
// re-derived here: unlike ResearchProjectFacts.{Hidden,Prerequisites,...},
// the wire ResearchProject message's lab-requirement/CanStart fields are not
// yet decoded into bridge.ResearchRead (an open item alongside the
// Hidden-field approximation ResearchRead's own doc comment discloses), so
// native validation of the ResearchIntent when it applies remains the
// authoritative gate. RoundsGearPlanner draws the same line
// against its own native preview.
type RoundsResearchSource interface {
	ReadResearch(context.Context, *c.Identity) (bridge.ResearchRead, bridge.Result, error)
}
type RoundsResearchPlanner struct {
	reviewer *Rounder
	native   RoundsResearchSource
	// building is the facility ladder the planner walks when the next rung
	// is locked only for lack of a research bench: furnish a hosting
	// room with the simple bench, else stage a starter shell first. Nil
	// when the source cannot preview placements; the hold is then reported
	// as BuildingResearchBench instead of built around.
	building *RoundsBuildingPlanner
}

type RoundsResearchResult struct {
	Verdict
	Plan domain.PlanID
	// NativeWorkTicks asks the clock for ticks while the selected project
	// is researched: nothing else owes the window a plan meanwhile.
	NativeWorkTicks uint32
}

// researchNativeWorkTicks is the window a current research project asks
// for per step; the review re-reads progress between windows.
const researchNativeWorkTicks = 2500

func NewRoundsResearchPlanner(reviewer *Rounder, native RoundsResearchSource) (*RoundsResearchPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, fmt.Errorf("%w: NewRoundsResearchPlanner: reviewer == nil || native == nil", ErrControl)
	}
	planner := &RoundsResearchPlanner{reviewer: reviewer, native: native}
	if source, ok := native.(RoundsBuildingSource); ok {
		if _, rooms := native.(observation.RoundsSource); rooms {
			planner.building = &RoundsBuildingPlanner{reviewer: reviewer, native: source, concern: policy.EnsureResearch}
		}
	}
	return planner, nil
}

// selectResearchBench resolves the building ladder's definition: the simple
// research bench, placed indoors in a Laboratory-hosting room, when the
// planning census lists it buildable.
func (r *RoundsBuildingPlanner) selectResearchBench(facts observation.ColonyProjection) (*RoundsBuildingPlanner, Verdict, error) {
	bench := facts.Shapes.Furniture.BenchFor(policy.RoomRoleLaboratory)
	if r.advancedLab {
		// The advanced step builds only what is buildable now: nothing owed is
		// no deficit, never a missing field.
		owed, ok := advancedLabOwed(facts)
		if !ok {
			return nil, BuildingReasonNoDeficit, nil
		}
		bench = owed
	}
	var definition *observation.PlanningDefinition
	for i := range facts.Definitions {
		if facts.Definitions[i].Name == bench {
			definition = &facts.Definitions[i]
		}
	}
	if definition == nil {
		return nil, fieldUnavailable("research_bench_definition"), nil
	}
	available, known := definition.Available.Value()
	if !known {
		return nil, fieldUnavailable("research_bench_availability"), nil
	}
	if !available {
		return nil, BuildingResearchBenchUnavailable, nil
	}
	facility, err := policy.Facility(policy.RoomRoleLaboratory)
	if err != nil {
		return nil, Verdict{}, err
	}
	resolved := *r
	resolved.definition = bench
	resolved.environment = policy.PlacementIndoors
	resolved.facility = &facility
	resolved.stuff = facts.BulkBuildStuff(bench, 1)
	return &resolved, Verdict{}, nil
}

// advancedLabOwed is the high-tech bench, then its analyzer, whichever is
// buildable now and not yet standing: the lab's hi-tech width benches and the
// analyzer slot the planned laboratory reserves (policy.RoomFurniture.AdvancedLab
// and Analyzer). The analyzer follows the bench because the project that
// unlocks it requires the bench. False when the catalog has neither, both
// stand, or the next is not yet buildable.
func advancedLabOwed(facts observation.ColonyProjection) (string, bool) {
	furniture := facts.Shapes.Furniture
	if furniture.AdvancedLab == "" {
		return "", false
	}
	standing := map[string]bool{}
	for _, c := range facts.Cells {
		if d := c.PlayerEdifice(); d != "" {
			standing[d] = true
		}
	}
	next := furniture.AdvancedLab
	if standing[next] {
		next = furniture.Analyzer.Def
	}
	if standing[next] {
		return "", false
	}
	for _, d := range facts.Definitions {
		if d.Name == next {
			available, known := d.Available.Value()
			if known && available {
				return next, true
			}
			return "", false
		}
	}
	return "", false
}

// bench walks the building ladder for the research bench, or reports the
// hold when none is composed. advanced walks it for the high-tech bench and
// analyzer instead.
func (r *RoundsResearchPlanner) bench(call, epoch context.Context, arbiter *stepArbiter, advanced bool) (RoundsResearchResult, error) {
	if r.building == nil {
		return RoundsResearchResult{Verdict: BuildingResearchBench}, nil
	}
	building := *r.building
	building.advancedLab = advanced
	result, err := building.step(call, epoch, arbiter)
	if err != nil {
		return RoundsResearchResult{}, err
	}
	return RoundsResearchResult{Verdict: result.Verdict, NativeWorkTicks: result.NativeWorkTicks}, nil
}

func (r *RoundsResearchPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoundsResearchResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoundsResearchResult{Verdict: BuildingReasonDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoundsResearchResult{}, fmt.Errorf("%w: step: !state.ObservationKnown || state.Snapshot.Validate() != nil", ErrControl)
	}
	review, err := p.journal.LoadRounds(call)
	if err != nil {
		return RoundsResearchResult{}, err
	}
	if !review.Enabled || review.Snapshot != state.Snapshot {
		return RoundsResearchResult{Verdict: BuildingReasonNoReview}, nil
	}
	call, recorded := recordPlannerStep(call, policy.EnsureResearch, state.Snapshot, review.Tick)
	defer recorded()
	// The ladder is paced by the colony stage: a Foothold colony
	// walks its first rungs, a Development colony the whole ladder.
	staged := r.reviewer.staged()
	items, err := r.reviewer.itemFacts(call, state.Snapshot)
	if err != nil {
		return RoundsResearchResult{}, err
	}
	needs, err := roundsResearchNeeds(call, p.journal, staged, items, state.Snapshot)
	if err != nil {
		return RoundsResearchResult{}, err
	}
	needs = r.reviewer.censusResearchNeeds(needs)
	needs = policy.DeepDrillingResearch(needs, review.ResourceRunwayState(), items)
	if len(needs) == 0 && len(staged.ResearchLadder) == 0 {
		return RoundsResearchResult{Verdict: BuildingReasonDisabled}, nil
	}
	goal, deficit, err := p.journal.WorkableProject(call, review, policy.EnsureResearch)
	if err != nil {
		return RoundsResearchResult{}, err
	}
	if deficit {
		for _, method := range goal.Methods {
			plan, err := p.journal.LoadPlan(call, method.Plan)
			if err != nil {
				return RoundsResearchResult{}, err
			}
			if store.PlanOpen(plan) {
				return RoundsResearchResult{Verdict: BuildingReasonExistingWork}, nil
			}
		}
	}
	identity := boundary.Identity(state.Snapshot)
	read, _, err := r.native.ReadResearch(call, identity)
	if err != nil {
		return RoundsResearchResult{}, err
	}
	if _, err = boundary.Context(read.Context, state.Snapshot); err != nil || read.Context.GetTick() < int64(review.Tick) {
		return RoundsResearchResult{}, fmt.Errorf("%w: step: err != nil || read.Context.GetTick() < int64(review.Tick)", ErrControl)
	}
	inputs := snap.ResearchCall{Policy: policy.ArmorResearchPolicy(staged, review.Latches.Soldiers), Needs: needs, Read: read}
	if deficit {
		snap.NoteResearch(call, inputs)
		// A knowledge project fills its category's slot beside the ordinary
		// one, so it is judged before the ordinary slot's current project.
		knowledge, reason := researchKnowledgeNext(inputs)
		if !reason.IsZero() {
			return RoundsResearchResult{Verdict: reason}, nil
		}
		if knowledge != "" {
			return r.admit(call, epoch, state, goal, knowledge)
		}
	}
	if read.CurrentProject != "" {
		// A current project finishes on native ticks alone: a derived or
		// roadmap goal owes the window ticks until it does, whether the
		// review still shows a deficit (a workshop need) or the current
		// project recovered it (a ladder rung, or a player's own choice).
		// Unless no bench stands to research it at: the game lets a project
		// that names no bench be selected, but nobody progresses it, so the
		// bench is owed first.
		if deficit && policy.ResearchBenchNeeded(read.Projects[read.CurrentProject]) {
			if result, admitted, err := r.buildAhead(call, epoch, arbiter); admitted || err != nil {
				return result, err
			}
			return r.bench(call, epoch, arbiter, false)
		}
		// Native locks a project only when it names a bench, yet none is
		// researched without one: with no bench standing the bench is owed
		// whatever the project; a standing bench is the ordinary wait.
		if deficit && r.building != nil {
			result, err := r.bench(call, epoch, arbiter, false)
			if err != nil || result.Verdict != BuildingReasonNoDeficit {
				return result, err
			}
			// Research goes on while the hi-tech bench and analyzer are
			// built: only an admitted plan preempts the wait, and any hold
			// falls through to it.
			if result, admitted, err := r.buildAhead(call, epoch, arbiter); admitted || err != nil {
				return result, err
			}
		}
		return RoundsResearchResult{Verdict: waitFor(WaitMethodUsed, "current_research_project"), NativeWorkTicks: researchNativeWorkTicks}, nil
	}
	if !deficit {
		return RoundsResearchResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	next, reason := researchNext(inputs)
	if !reason.IsZero() {
		return RoundsResearchResult{Verdict: reason}, nil
	}
	// A rung locked only for lack of a bench is a building need, not a
	// selection: native refuses the ResearchIntent until the bench stands.
	// The ladder's plans are this goal's methods, so an open bench
	// build reads as existing work above and the selection follows it.
	if policy.ResearchBenchNeeded(read.Projects[next]) {
		if result, admitted, err := r.buildAhead(call, epoch, arbiter); admitted || err != nil {
			return result, err
		}
		return r.bench(call, epoch, arbiter, false)
	}
	if result, admitted, err := r.buildAhead(call, epoch, arbiter); admitted || err != nil {
		return result, err
	}
	return r.admit(call, epoch, state, goal, next)
}

// buildAhead walks the building ladder for the high-tech bench and then the
// analyzer, whichever is buildable and owed. admitted is true only when it
// admitted a building plan; every other outcome (nothing owed, no space, a
// funding or placement hold) is not a verdict for the research step, which
// goes on to select or wait.
func (r *RoundsResearchPlanner) buildAhead(call, epoch context.Context, arbiter *stepArbiter) (RoundsResearchResult, bool, error) {
	if r.building == nil {
		return RoundsResearchResult{}, false, nil
	}
	result, err := r.bench(call, epoch, arbiter, true)
	if err != nil {
		return RoundsResearchResult{}, false, err
	}
	return result, result.Verdict == BuildingReasonAdmitted, nil
}

// admit records the one-action plan that selects next, unless this goal
// epoch already tried it.
func (r *RoundsResearchPlanner) admit(call, epoch context.Context, state ControlState, goal store.ProjectState, next string) (RoundsResearchResult, error) {
	p := r.reviewer.player
	digestNext := sha256.Sum256([]byte(next))
	method := domain.MethodID(fmt.Sprintf("research-%x", digestNext[:16]))
	if _, err := p.journal.LoadOwnerMethod(call, goal, method); err == nil {
		return RoundsResearchResult{Verdict: waitFor(WaitMethodUsed, "research_selection")}, nil
	} else if !errors.Is(err, store.ErrNotFound) {
		return RoundsResearchResult{}, err
	}
	id := domain.MintPlanID()
	value, err := domain.NewResearchSelect(next)
	if err != nil {
		return RoundsResearchResult{}, err
	}
	action, err := domain.NewResearchSelectAction(domain.ActionID(fmt.Sprintf("%s-0", id)), value)
	if err != nil {
		return RoundsResearchResult{}, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoundsResearchResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoundsResearchResult{}, err
	}
	if p.session.State() != state {
		return RoundsResearchResult{}, fmt.Errorf("%w: step: p.session.State() != state", ErrControl)
	}
	if _, err = p.journal.CommitProjectMethod(call, goal.Project.ID, goal.Revision, method, "", plan); err != nil {
		return RoundsResearchResult{}, err
	}
	return RoundsResearchResult{Verdict: BuildingReasonAdmitted, Plan: id}, nil
}

// researchKnowledgeNext is the knowledge project the step selects into its
// category's slot: the head of the goal's prerequisite queue when
// that head is a knowledge project, else the project an empty knowledge slot
// should fund (policy.KnowledgePick). Empty with a zero reason when no
// knowledge selection is owed; a head whose slot already holds another
// project waits (the slot's current project is never replaced) and a head
// native reports locked is an unavailable field, not a selection.
func researchKnowledgeNext(in snap.ResearchCall) (string, Verdict) {
	read := in.Read
	if head, reason := researchNext(in); reason.IsZero() {
		row := read.Projects[head]
		if row.KnowledgeCategory != "" {
			for _, slot := range read.Knowledge {
				if slot.Category != row.KnowledgeCategory {
					continue
				}
				if slot.Current != "" {
					return "", waitFor(WaitMethodUsed, "knowledge_slot")
				}
				if !row.Census || len(row.LockReasons) != 0 {
					return "", fieldUnavailable("research_knowledge_project")
				}
				return head, Verdict{}
			}
			return "", fieldUnavailable("research_knowledge_slot")
		}
	}
	return policy.KnowledgePick(read.Projects, read.Finished, read.Knowledge), Verdict{}
}

// researchNext is the project a research step selects from its fresh
// census: the goal is the first recorded need the census lists and has not
// finished, else the first such ladder rung, and the step selects the first
// unfinished prerequisite on the way to it. A non-empty reason is the
// step's outcome instead.
func researchNext(in snap.ResearchCall) (string, Verdict) {
	read := in.Read
	finished := make([]policy.ResearchProjectID, len(read.Finished))
	for i, name := range read.Finished {
		finished[i] = policy.ResearchProjectID(name)
	}
	facts := policy.ResearchFacts{Current: policy.ResearchProjectID(read.CurrentProject), Finished: finished}
	for name := range read.Projects {
		facts.Projects = append(facts.Projects, policy.ResearchProjectID(name))
	}
	target, _ := policy.ResearchConcern(in.Policy, in.Needs, domain.Known(facts))
	if target == "" {
		return "", BuildingReasonNoDeficit
	}
	if _, ok := read.Projects[target]; !ok {
		return "", fieldUnavailable("research_project")
	}
	projects := make(map[policy.ResearchProjectID]policy.ResearchProjectFacts, len(read.Projects))
	for name, facts := range read.Projects {
		projects[policy.ResearchProjectID(name)] = facts
	}
	queue, err := policy.ResearchPrerequisiteQueue(projects, finished, []policy.ResearchProjectID{policy.ResearchProjectID(target)})
	if err != nil || len(queue) == 0 {
		return "", fieldUnavailable("research_prerequisites")
	}
	return string(queue[0]), Verdict{}
}
