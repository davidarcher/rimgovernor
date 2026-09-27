package buildingruntime

import (
	"context"
	"errors"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// RoutinePrisonerInteractionPlanner proposes one interaction write for
// MaintainPopulation's deficit: policy.PrisonerRecruitDeficit and
// SelectPrisonerInteractionMethod read the dedicated per-cycle population
// census (RoutineFacts.Prisoners, sourced from the
// rimgovernor/observations_read_population read) since, unlike husbandry,
// no other per-cycle read already carries recruitable/interaction facts.
// Disclosed narrowing: only the Recruit interaction, and Release once the
// operator opts in with PrisonerReleaseAfterDays, are ever dispatched --
// never execution or any other player-only order; see
// policy.MaintainPopulation's doc comment for why.
type RoutinePrisonerInteractionPlanner struct {
	reviewer *RoutineReviewer
	// building shells the planned jail while a prisoner is held (#835);
	// nil for a source that cannot preview buildings.
	building *RoutineBuildingPlanner
}
type RoutinePrisonerInteractionResult struct {
	Reason RoutineBuildingReason
	Plan   domain.PlanID
}

func NewRoutinePrisonerInteractionPlanner(reviewer *RoutineReviewer) (*RoutinePrisonerInteractionPlanner, error) {
	if reviewer == nil {
		return nil, fmt.Errorf("%w: NewRoutinePrisonerInteractionPlanner: reviewer == nil", ErrControl)
	}
	r := &RoutinePrisonerInteractionPlanner{reviewer: reviewer}
	if source, ok := reviewer.native.(RoutineBuildingSource); ok {
		r.building = &RoutineBuildingPlanner{reviewer: reviewer, native: source, goal: policy.MaintainPopulation, definition: "Wall"}
	}
	return r, nil
}

func (r *RoutinePrisonerInteractionPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoutinePrisonerInteractionResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutinePrisonerInteractionResult{Reason: BuildingMethodDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoutinePrisonerInteractionResult{}, fmt.Errorf("%w: step: !state.ObservationKnown || state.Snapshot.Validate() != nil", ErrControl)
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutinePrisonerInteractionResult{}, err
	}
	if !review.Enabled || !review.Snapshot.Matches(state.Snapshot) {
		return RoutinePrisonerInteractionResult{Reason: BuildingMethodNoReview}, nil
	}
	var goal store.GoalState
	found := false
	for _, binding := range review.Goals {
		if binding.Need == policy.MaintainPopulation {
			goal, err = p.journal.LoadGoal(call, binding.Goal)
			found = true
			break
		}
	}
	if err != nil {
		return RoutinePrisonerInteractionResult{}, err
	}
	if !found || goal.Goal.Status != domain.GoalActive || goal.Goal.Need != domain.NeedDeficit {
		return RoutinePrisonerInteractionResult{Reason: BuildingMethodNoDeficit}, nil
	}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoutinePrisonerInteractionResult{}, err
		}
		if store.PlanOpen(plan) {
			return RoutinePrisonerInteractionResult{Reason: BuildingMethodExistingWork}, nil
		}
	}
	expected, err := routineScope(call, r.reviewer.native)
	if err != nil {
		return RoutinePrisonerInteractionResult{}, err
	}
	if !routineBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoutinePrisonerInteractionResult{}, fmt.Errorf("%w: step: !routineBuildingBoundary(expected, state.Snapshot, review.Tick)", ErrControl)
	}
	started := r.reviewer.clock.Now()
	read, err := r.reviewer.observeOwned(call, r.reviewer.native, expected, domain.Unknown[[]policy.ConstructionClaim]())
	if err != nil {
		return RoutinePrisonerInteractionResult{}, err
	}
	choice := policy.SelectPrisonerInteractionMethod(read.Projection.Facts.Prisoners, read.Projection.Facts.FoodDays, r.reviewer.policy.Prisoners())
	switch choice.Reason {
	case policy.PrisonerNoDeficit:
		return RoutinePrisonerInteractionResult{Reason: BuildingMethodUsed}, nil
	case policy.PrisonerUnknown:
		return RoutinePrisonerInteractionResult{Reason: BuildingMethodUnknown}, nil
	}
	if result, handled, err := r.stageJail(call, epoch, state, review, goal, expected); err != nil || handled {
		return result, err
	}
	// Keyed by mode, pawn and attempt count, mirroring
	// RoutineHusbandryPlanner's method key: a fresh attempt after an
	// interrupted or failed try re-selects whichever prisoner and write is
	// currently best.
	prefix := fmt.Sprintf("%s-%s-", choice.Interaction, choice.Pawn)
	attempt := medicalAttemptCount(goal.History, goal.Goal.Epoch, prefix)
	if attempt >= maxMedicalAttemptsPerPatient {
		return RoutinePrisonerInteractionResult{Reason: BuildingMethodExhausted}, nil
	}
	method := domain.MethodID(fmt.Sprintf("%s%d", prefix, attempt))
	interaction, err := domain.NewPrisonerInteraction(choice.Pawn, choice.Interaction)
	if err != nil {
		return RoutinePrisonerInteractionResult{}, err
	}
	id := domain.MintPlanID("routine-prisoner-interaction")
	action, err := domain.NewPrisonerInteractionAction(domain.ActionID(fmt.Sprintf("%s-0", id)), interaction)
	if err != nil {
		return RoutinePrisonerInteractionResult{}, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoutinePrisonerInteractionResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutinePrisonerInteractionResult{}, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoutinePrisonerInteractionResult{}, fmt.Errorf("%w: step: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge", ErrControl)
	}
	if _, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, method, plan); err != nil {
		return RoutinePrisonerInteractionResult{}, err
	}
	return RoutinePrisonerInteractionResult{Reason: BuildingMethodAdmitted, Plan: id}, nil
}

// stageJail takes the next jail step (#835, #880) while a prisoner is
// held: shell a planned jail, set a bed standing in one for prisoners, or
// place the next template bed. handled is false when nothing is due, the
// step was already tried this epoch, or native refuses it, so the
// interaction goes on.
func (r *RoutinePrisonerInteractionPlanner) stageJail(call, epoch context.Context, state ControlState, review store.RoutineReview, goal store.GoalState, expected observation.Identity) (RoutinePrisonerInteractionResult, bool, error) {
	if r.building == nil {
		return RoutinePrisonerInteractionResult{}, false, nil
	}
	reading, err := r.reviewer.observeRooms(call, r.reviewer.native, expected, domain.Unknown[[]policy.ConstructionClaim](), "Wall", "Door", policy.JailBedDefinition)
	if err != nil {
		return RoutinePrisonerInteractionResult{}, false, err
	}
	step := jailStep(reading.Projection)
	var result RoutineBuildingResult
	switch step.Kind {
	case policy.JailNone:
		return RoutinePrisonerInteractionResult{}, false, nil
	case policy.JailShell:
		result, err = r.building.shellRoom(call, epoch, state, review, goal, reading.ColonyReading, step.Room, plannedRoomMethod(step.Room), "routine-planned-jail", "")
	case policy.JailPlace:
		method := domain.MethodID(fmt.Sprintf("jail-place-%d-%d-%s", step.Room.Interior.X, step.Room.Interior.Z, step.Piece.Slot))
		result, err = r.building.placePiece(call, epoch, state, review, goal, reading, step.Piece, method, "routine-jail-bed")
	case policy.JailMark:
		result, err = r.markJailBed(call, epoch, state, goal, reading.Projection, step.Bed)
	}
	clockSchedulerLog("%s: jail %s (held %d, beds %d) reason=%v", goal.Goal.ID, step.Kind, step.Held, step.Beds, result.Reason)
	if err != nil || result.Reason == BuildingMethodUsed || result.Reason == BuildingMethodNoSpace || result.Reason == BuildingMethodUnknown || result.Reason == BuildingMethodRefused {
		return RoutinePrisonerInteractionResult{}, false, err
	}
	return RoutinePrisonerInteractionResult{Reason: result.Reason}, true, nil
}

// jailStep is the projection's next jail step; none below Masonry or
// while a fact is unknown.
func jailStep(facts observation.ColonyProjection) policy.JailStep {
	plan, rooms, known := plannedLayout(facts)
	prisoners, pk := facts.Facts.Prisoners.Value()
	sleeping, sk := facts.Facts.Sleeping.Value()
	built, bk := facts.Facts.CurrentConstruction.Value()
	if !known || !pk || !sk || !bk || !built.Colony {
		return policy.JailStep{}
	}
	return policy.NextJailStep(plan, rooms, heldPrisoners(prisoners), sleeping.Beds, built.Buildings)
}

// markJailBed commits one CAS-gated patch setting bed for prisoners, once
// per bed per goal epoch.
func (r *RoutinePrisonerInteractionPlanner) markJailBed(call, epoch context.Context, state ControlState, goal store.GoalState, facts observation.ColonyProjection, bed string) (RoutineBuildingResult, error) {
	p := r.reviewer.player
	native, ok := r.reviewer.native.(RoutineHospitalSource)
	if !ok {
		return RoutineBuildingResult{Reason: BuildingMethodUnknown}, nil
	}
	method := domain.MethodID("jail-mark-" + bed)
	if _, err := p.journal.LoadGoalMethod(call, goal.Goal.ID, goal.Goal.Epoch, method); err == nil {
		return RoutineBuildingResult{Reason: BuildingMethodUsed}, nil
	} else if !errors.Is(err, store.ErrNotFound) {
		return RoutineBuildingResult{}, err
	}
	target, _, err := native.ReadBedUseTarget(call, boundary.Identity(state.Snapshot), bed)
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	if _, err = boundary.Context(target.Context, state.Snapshot); err != nil || target.Context.GetTick() < int64(facts.Identity.Tick) {
		return RoutineBuildingResult{}, fmt.Errorf("%w: markJailBed: err != nil || target.Context.GetTick() < int64(facts.Identity.Tick)", ErrControl)
	}
	if target.Prisoners {
		return RoutineBuildingResult{Reason: BuildingMethodUsed}, nil
	}
	patch, err := domain.NewBedPrisoners(bed)
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	preview, _, err := native.PreviewBedUse(call, boundary.Identity(state.Snapshot), patch)
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	if evaluated := preview.GetEvaluated(); evaluated == nil || !evaluated.GetAccepted() {
		return RoutineBuildingResult{Reason: BuildingMethodRefused}, nil
	}
	id := domain.MintPlanID("routine-jail-mark")
	action, err := domain.NewBedUseAction(domain.ActionID(fmt.Sprintf("%s-0", id)), patch)
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutineBuildingResult{}, err
	}
	if p.session.State() != state {
		return RoutineBuildingResult{}, fmt.Errorf("%w: markJailBed: p.session.State() != state", ErrControl)
	}
	if _, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, method, plan); err != nil {
		return RoutineBuildingResult{}, err
	}
	return RoutineBuildingResult{Reason: BuildingMethodAdmitted}, nil
}

// heldPrisoners counts the living prisoners in the census.
func heldPrisoners(prisoners []policy.PrisonerFacts) int {
	n := 0
	for _, p := range prisoners {
		held, hk := p.Prisoner.Value()
		dead, dk := p.Dead.Value()
		if hk && held && dk && !dead {
			n++
		}
	}
	return n
}
