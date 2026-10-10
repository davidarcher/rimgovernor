package buildingruntime

import (
	"context"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// reviewGathering fills the reading's gathering plan: the party due now
// (policy.PlanGathering over the mood ledger, the census, the PartySpot and
// the calm census) and whether it holds HoldGatherings open. The party's mood
// effect is the catalog's AttendedParty thought; without it the plan is
// unknown.
func (r *Rounder) reviewGathering(reading *observation.RoundsReading, snapshot domain.GenerationSnapshot) {
	facts := &reading.Projection.Facts
	benefit := domain.Unknown[float64]()
	if catalog := reading.Frame.Catalog; catalog != nil {
		if thought, ok := catalog.ThoughtFacts(policy.PartyThought); ok && thought.WorstOffset > 0 {
			benefit = domain.Known(thought.WorstOffset)
		}
	}
	facts.GatheringPlan = policy.PlanGathering(policy.GatheringInput{
		Ledger:  facts.MoodLedger,
		Pawns:   facts.MoodPawns,
		Benefit: benefit,
		Calm:    r.roundsCalm(reading, snapshot),
		Census:  facts.CurrentConstruction,
	})
	facts.GatheringOwed = policy.GatheringOwed(facts.GatheringPlan)
}

// RoundsGatheringPlanner is HoldGatherings' planner: while a party is due
// (policy.PlanGathering) and none is running, it commits one `gathering`
// action for the organizer. The game chooses the spot (the PartySpot that
// EnsureComfort keeps) and refuses what it cannot hold; a refusal is spent
// from the shared refusal budget.
type RoundsGatheringPlanner struct {
	reviewer *Rounder
}
type RoundsGatheringResult struct {
	Verdict
	Plan domain.PlanID
}

func NewRoundsGatheringPlanner(reviewer *Rounder) (*RoundsGatheringPlanner, error) {
	if reviewer == nil || !reviewer.methodEnabled(policy.HoldGatherings) {
		return nil, fmt.Errorf("%w: NewRoundsGatheringPlanner: reviewer == nil || !reviewer.methodEnabled(policy.HoldGatherings)", ErrControl)
	}
	return &RoundsGatheringPlanner{reviewer}, nil
}

const gatheringMethodPrefix = "gathering-"

func (r *RoundsGatheringPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoundsGatheringResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoundsGatheringResult{Verdict: BuildingReasonDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoundsGatheringResult{}, fmt.Errorf("%w: step: !state.ObservationKnown || state.Snapshot.Validate() != nil", ErrControl)
	}
	review, err := p.journal.LoadRounds(call)
	if err != nil {
		return RoundsGatheringResult{}, err
	}
	if !review.Enabled || !review.Snapshot.Matches(state.Snapshot) {
		return RoundsGatheringResult{Verdict: BuildingReasonNoReview}, nil
	}
	goal, workable, err := p.journal.Workable(call, review, policy.HoldGatherings)
	if err != nil {
		return RoundsGatheringResult{}, err
	}
	if !workable {
		return RoundsGatheringResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	// A party already commissioned this Episode: its plan still open, or begun
	// within the longest a party runs. The game would refuse a second one.
	var lastStart domain.Tick = -1
	for _, method := range goal.History {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoundsGatheringResult{}, err
		}
		if store.PlanOpen(plan) {
			return RoundsGatheringResult{Verdict: BuildingReasonExistingWork}, nil
		}
		for _, progress := range plan.Progress {
			if view := progress.View(); view.Stage == domain.Completed && view.Tick > lastStart {
				lastStart = view.Tick
			}
		}
	}
	if lastStart >= 0 && policy.GatheringRunning(lastStart, review.Tick) {
		return RoundsGatheringResult{Verdict: waitFor(policy.CauseMethodUsed, "gathering_running")}, nil
	}
	expected, err := stepScope(call, r.reviewer.native)
	if err != nil {
		return RoundsGatheringResult{}, err
	}
	if !roundsBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoundsGatheringResult{}, fmt.Errorf("%w: step: !roundsBuildingBoundary(expected, state.Snapshot, review.Tick)", ErrControl)
	}
	started := r.reviewer.clock.Now()
	read, err := r.reviewer.observeOwned(call, r.reviewer.native, expected, domain.Unknown[[]policy.ConstructionClaim]())
	if err != nil {
		return RoundsGatheringResult{}, err
	}
	party, known := read.Projection.Facts.GatheringPlan.Value()
	if !known || party.Organizer == "" {
		return RoundsGatheringResult{Verdict: waitFor(policy.CauseMethodUsed, "gathering_plan")}, nil
	}
	method, verdict, ok, err := admitStandardMethod(call, p.journal, goal, gatheringMethodPrefix, state.Snapshot)
	if err != nil {
		return RoundsGatheringResult{}, err
	}
	if !ok {
		return RoundsGatheringResult{Verdict: verdict}, nil
	}
	if !arbiter.tryClaim([]domain.PawnID{domain.PawnID(party.Organizer)}) {
		return RoundsGatheringResult{Verdict: claimHeld("organizer")}, nil
	}
	gathering, err := domain.NewGathering(party.Def, domain.PawnID(party.Organizer))
	if err != nil {
		return RoundsGatheringResult{}, err
	}
	id := domain.MintPlanID()
	action, err := domain.NewGatheringAction(domain.ActionID(fmt.Sprintf("%s-0", id)), gathering)
	if err != nil {
		return RoundsGatheringResult{}, err
	}
	spec, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoundsGatheringResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoundsGatheringResult{}, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoundsGatheringResult{}, fmt.Errorf("%w: step: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge", ErrControl)
	}
	reason := fmt.Sprintf("gathering: %s organizes a %s on a colony-wide mood dip", party.Organizer, party.Def)
	if _, err = p.journal.CommitMethodReason(call, goal.Standard.ID, goal.Revision, method, reason, spec); err != nil {
		return RoundsGatheringResult{}, err
	}
	return RoundsGatheringResult{Verdict: BuildingReasonAdmitted, Plan: id}, nil
}
