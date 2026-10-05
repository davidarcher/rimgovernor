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

// RoundsPrisonerInteractionPlanner proposes one interaction write for
// MaintainPopulation's deficit: policy.PrisonerRecruitDeficit and
// SelectPrisonerInteractionMethod read the dedicated per-cycle population
// census (RoundsFacts.Prisoners, sourced from the
// rimgovernor/observations_read_population read) since, unlike husbandry,
// no other per-cycle read already carries recruitable/interaction facts.
// It dispatches whichever use policy.MaintainPopulation chooses per
// prisoner: Recruit, Convert, Enslave or Release; never execution.
type RoundsPrisonerInteractionPlanner struct {
	reviewer *Rounder
	// building shells the planned jail while a prisoner is held (#835);
	// nil for a source that cannot preview buildings.
	building *RoundsBuildingPlanner
}
type RoundsPrisonerInteractionResult struct {
	Verdict
	Plan domain.PlanID
}

func NewRoundsPrisonerInteractionPlanner(reviewer *Rounder) (*RoundsPrisonerInteractionPlanner, error) {
	if reviewer == nil {
		return nil, fmt.Errorf("%w: NewRoundsPrisonerInteractionPlanner: reviewer == nil", ErrControl)
	}
	r := &RoundsPrisonerInteractionPlanner{reviewer: reviewer}
	if source, ok := reviewer.native.(RoundsBuildingSource); ok {
		r.building = &RoundsBuildingPlanner{reviewer: reviewer, native: source, concern: policy.MaintainPopulation, definition: "Wall"}
	}
	return r, nil
}

func (r *RoundsPrisonerInteractionPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoundsPrisonerInteractionResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoundsPrisonerInteractionResult{Verdict: BuildingReasonDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoundsPrisonerInteractionResult{}, fmt.Errorf("%w: step: !state.ObservationKnown || state.Snapshot.Validate() != nil", ErrControl)
	}
	review, err := p.journal.LoadRounds(call)
	if err != nil {
		return RoundsPrisonerInteractionResult{}, err
	}
	if !review.Enabled || !review.Snapshot.Matches(state.Snapshot) {
		return RoundsPrisonerInteractionResult{Verdict: BuildingReasonNoReview}, nil
	}
	goal, workable, err := p.journal.Workable(call, review, policy.MaintainPopulation)
	if err != nil {
		return RoundsPrisonerInteractionResult{}, err
	}
	if !workable {
		return RoundsPrisonerInteractionResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoundsPrisonerInteractionResult{}, err
		}
		if store.PlanOpen(plan) {
			return RoundsPrisonerInteractionResult{Verdict: BuildingReasonExistingWork}, nil
		}
	}
	expected, err := stepScope(call, r.reviewer.native)
	if err != nil {
		return RoundsPrisonerInteractionResult{}, err
	}
	if !roundsBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoundsPrisonerInteractionResult{}, fmt.Errorf("%w: step: !roundsBuildingBoundary(expected, state.Snapshot, review.Tick)", ErrControl)
	}
	started := r.reviewer.clock.Now()
	read, err := r.reviewer.observeOwned(call, r.reviewer.native, expected, domain.Unknown[[]policy.ConstructionClaim]())
	if err != nil {
		return RoundsPrisonerInteractionResult{}, err
	}
	choice := policy.SelectPrisonerInteractionMethod(read.Projection.Facts.Prisoners, read.Projection.Facts.PrisonerColony, read.Projection.Facts.FoodDays, r.reviewer.policy.Prisoners())
	switch choice.Reason {
	case policy.PrisonerNoDeficit:
		return RoundsPrisonerInteractionResult{Verdict: waitFor(WaitMethodUsed, "prisoner_interaction")}, nil
	case policy.PrisonerUnknown:
		return RoundsPrisonerInteractionResult{Verdict: fieldUnavailable("prisoners")}, nil
	}
	if result, handled, err := r.stageJail(call, epoch, state, review, goal, expected); err != nil || handled {
		return result, err
	}
	// Keyed by mode, pawn and attempt count, mirroring
	// RoundsHusbandryPlanner's method key: a fresh attempt after an
	// interrupted or failed try re-selects whichever prisoner and write is
	// currently best.
	prefix := fmt.Sprintf("%s-%s-", choice.Interaction, choice.Pawn)
	attempt := medicalAttemptCount(goal.History, goal.Standard.Episode, prefix)
	if attempt >= maxMedicalAttemptsPerPatient {
		return RoundsPrisonerInteractionResult{Verdict: refuse(RefusalRetriesSpent, "maxMedicalAttemptsPerPatient", "")}, nil
	}
	method := domain.MethodID(fmt.Sprintf("%s%d", prefix, attempt))
	interaction, err := domain.NewPrisonerInteraction(choice.Pawn, choice.Interaction)
	if err != nil {
		return RoundsPrisonerInteractionResult{}, err
	}
	id := domain.MintPlanID()
	action, err := domain.NewPrisonerInteractionAction(domain.ActionID(fmt.Sprintf("%s-0", id)), interaction)
	if err != nil {
		return RoundsPrisonerInteractionResult{}, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoundsPrisonerInteractionResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoundsPrisonerInteractionResult{}, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoundsPrisonerInteractionResult{}, fmt.Errorf("%w: step: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge", ErrControl)
	}
	if _, err = p.journal.CommitMethod(call, goal.Standard.ID, goal.Revision, method, plan); err != nil {
		return RoundsPrisonerInteractionResult{}, err
	}
	return RoundsPrisonerInteractionResult{Verdict: BuildingReasonAdmitted, Plan: id}, nil
}

// stageJail takes the next jail step (#835, #880) while a prisoner is
// held: shell a planned jail, set a bed standing in one for prisoners, or
// place the next template bed. handled is false when nothing is due, the
// step was already tried this epoch, or native refuses it, so the
// interaction goes on.
func (r *RoundsPrisonerInteractionPlanner) stageJail(call, epoch context.Context, state ControlState, review store.Rounds, goal store.StandardState, expected observation.Identity) (RoundsPrisonerInteractionResult, bool, error) {
	if r.building == nil {
		return RoundsPrisonerInteractionResult{}, false, nil
	}
	reading, err := r.reviewer.observeRooms(call, r.reviewer.native, expected, domain.Unknown[[]policy.ConstructionClaim](), "Wall", "Door")
	if err != nil {
		return RoundsPrisonerInteractionResult{}, false, err
	}
	step := jailStep(reading.Projection)
	var result RoundsBuildingResult
	switch step.Kind {
	case policy.JailNone:
		return RoundsPrisonerInteractionResult{}, false, nil
	case policy.JailShell:
		result, err = r.building.shellRoom(call, epoch, state, review, goal, reading.ColonyReading, step.Room, plannedRoomMethod(step.Room), "")
	case policy.JailPlace:
		method := domain.MethodID(fmt.Sprintf("jail-place-%d-%d-%s", step.Room.Interior.X, step.Room.Interior.Z, step.Piece.Slot))
		result, err = r.building.placePiece(call, epoch, state, review, goal, reading, step.Piece, method)
	case policy.JailMark:
		result, err = r.markJailBed(call, epoch, state, goal, reading.Projection, step.Bed)
	}
	if err != nil || result.Verdict.Is(WaitMethodUsed) || result.Verdict.Is(RefusalNoSpace) || result.Verdict.Is(RefusalFieldUnavailable) || result.Verdict.Is(RefusalSharedAdmission) {
		return RoundsPrisonerInteractionResult{}, false, err
	}
	return RoundsPrisonerInteractionResult{Verdict: result.Verdict}, true, nil
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
// per bed per Episode.
func (r *RoundsPrisonerInteractionPlanner) markJailBed(call, epoch context.Context, state ControlState, goal store.StandardState, facts observation.ColonyProjection, bed string) (RoundsBuildingResult, error) {
	p := r.reviewer.player
	native, ok := r.reviewer.native.(RoundsHospitalSource)
	if !ok {
		return RoundsBuildingResult{Verdict: fieldUnavailable("hospital_source")}, nil
	}
	method := domain.MethodID("jail-mark-" + bed)
	if _, err := p.journal.LoadMethod(call, goal.Standard.ID, goal.Standard.Episode, method); err == nil {
		return RoundsBuildingResult{Verdict: waitFor(WaitMethodUsed, "jail_bed_method")}, nil
	} else if !errors.Is(err, store.ErrNotFound) {
		return RoundsBuildingResult{}, err
	}
	target, _, err := native.ReadBedUseTarget(call, boundary.Identity(state.Snapshot), bed)
	if err != nil {
		return RoundsBuildingResult{}, err
	}
	if _, err = boundary.Context(target.Context, state.Snapshot); err != nil || target.Context.GetTick() < int64(facts.Identity.Tick) {
		return RoundsBuildingResult{}, fmt.Errorf("%w: markJailBed: err != nil || target.Context.GetTick() < int64(facts.Identity.Tick)", ErrControl)
	}
	if target.Prisoners {
		return RoundsBuildingResult{Verdict: waitFor(WaitMethodUsed, "jail_bed_prisoners")}, nil
	}
	patch, err := domain.NewBedPrisoners(bed)
	if err != nil {
		return RoundsBuildingResult{}, err
	}
	if err = p.commitBedPatch(call, epoch, state, goal, method, patch, nil); err != nil {
		return RoundsBuildingResult{}, err
	}
	return RoundsBuildingResult{Verdict: BuildingReasonAdmitted}, nil
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
