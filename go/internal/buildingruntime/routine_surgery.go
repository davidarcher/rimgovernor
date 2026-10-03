package buildingruntime

import (
	"context"
	"crypto/sha256"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// RoutineSurgeryPlanner composes MaintainSurgery's method (#1164): one plan
// of medical ProductionBillIntents, at most one per patient, from a fresh pawn care read.
// A patient with a queued bill or an open surgery action is in flight. The
// goal settles on the health change (the operation leaving the census),
// never on the bill. When nothing can be queued the first want (part short
// or no capable doctor) is the step's reason, filed on the goal record.
type RoutineSurgeryPlanner struct {
	reviewer *RoutineReviewer
}

type RoutineSurgeryResult struct {
	Verdict
	Plan  domain.PlanID
	Wants []policy.SurgeryWant
	// Harvest is set when the plan carries an organ harvest (#1169)
	// or an artificial part recovery (#1232), or a peg-leg step (#1236).
	Harvest bool
	// NativeWorkTicks lends a window while a surgery bill is queued
	// (#1238): native doctors carry it, and no plan action stays open.
	NativeWorkTicks uint32
}

// surgeryNativeWorkTicks is the window a queued bill lends per stop; the
// next stop reads the bill again.
const surgeryNativeWorkTicks = medicalWaitTicks

func NewRoutineSurgeryPlanner(reviewer *RoutineReviewer) (*RoutineSurgeryPlanner, error) {
	if reviewer == nil {
		return nil, fmt.Errorf("%w: NewRoutineSurgeryPlanner: reviewer == nil", ErrControl)
	}
	return &RoutineSurgeryPlanner{reviewer}, nil
}

func (r *RoutineSurgeryPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoutineSurgeryResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutineSurgeryResult{Verdict: BuildingReasonDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoutineSurgeryResult{}, fmt.Errorf("%w: step: !state.ObservationKnown || state.Snapshot.Validate() != nil", ErrControl)
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutineSurgeryResult{}, err
	}
	if !review.Enabled || !review.Snapshot.Matches(state.Snapshot) {
		return RoutineSurgeryResult{Verdict: BuildingReasonNoReview}, nil
	}
	expected, err := routineScope(call, r.reviewer.native)
	if err != nil || !routineBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoutineSurgeryResult{}, fmt.Errorf("%w: step: err != nil || !routineBuildingBoundary(expected, state.Snapshot, review.Tick)", ErrControl)
	}
	claims, err := p.journal.ConstructionClaims(call, state.Snapshot, expected.Tick)
	if err != nil {
		return RoutineSurgeryResult{}, err
	}
	read, err := r.reviewer.observeOwned(call, r.reviewer.native, expected, claims)
	if err != nil {
		return RoutineSurgeryResult{}, err
	}
	// A queued bill is clock work whether or not the goal still stands
	// (#1238): an elective's bill settles its goal before the operation.
	var ticks uint32
	if policy.SurgeryBillsQueued(read.Projection.Facts.MedicalPawns, read.Projection.Facts.Prisoners) {
		ticks = surgeryNativeWorkTicks
	}
	goal, workable, err := p.journal.Workable(call, review, policy.MaintainSurgery)
	if err != nil {
		return RoutineSurgeryResult{}, err
	}
	if !workable {
		return RoutineSurgeryResult{Verdict: BuildingReasonNoDeficit, NativeWorkTicks: ticks}, nil
	}
	inFlight := map[policy.PawnID]bool{}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoutineSurgeryResult{}, err
		}
		for _, progress := range plan.Progress {
			if s, ok := progress.Action().Surgery(); ok && domain.GoalWorkOpen([]domain.Progress{progress}) {
				inFlight[policy.PawnID(s.Pawn())] = true
			}
		}
	}
	if _, known := read.Projection.Facts.MedicalPawns.Value(); !known {
		return RoutineSurgeryResult{Verdict: fieldUnavailable("medical_pawns"), NativeWorkTicks: ticks}, nil
	}
	surgery := policy.SurgeryContext{HospitalBed: positiveFact(policy.HospitalBedReady(read.Projection.Facts.Sleeping))}
	if pawns, known := read.Projection.WorkPawns.Value(); known {
		surgery.Profiles = policy.Profiles(pawns)
	}
	selection := policy.SelectSurgery(read.Projection.Facts.MedicalPawns, inFlight, surgery)
	result := RoutineSurgeryResult{Wants: selection.Wants, NativeWorkTicks: ticks}
	var queue []policy.SurgeryChoice
	for _, choice := range selection.Queue {
		if arbiter.tryClaim([]domain.PawnID{domain.PawnID(choice.Pawn)}) {
			queue = append(queue, choice)
		}
	}
	// Organ harvest (#1169): at most one, from a prisoner the colony would
	// not recruit, for a colonist's part-short organ or a silver runway
	// deficit, when its gain outweighs the mood and goodwill cost.
	stock := map[policy.Resource]int64{}
	resources, _ := read.Projection.Facts.Resources.Value()
	for _, row := range resources {
		stock[row.Resource] += row.Count
	}
	short, _ := policy.RoutineSilverShort(read.Projection.Facts, r.reviewer.policy, review.Latches.MedicalReserve).Value()
	needs := policy.OrganNeeds(read.Projection.Facts.MedicalPawns, selection.Wants, short, stock)
	harvest, harvesting := policy.SelectOrganHarvest(read.Projection.Facts.Prisoners, read.Projection.Facts.PrisonerColony, needs, inFlight)
	if !harvesting {
		// Artificial part recovery (#1232): the same one-at-a-time slot.
		parts := policy.PartRecoveryNeeds(read.Projection.Facts.MedicalPawns, selection.Wants)
		harvest, harvesting = policy.SelectPartRecovery(read.Projection.Facts.Prisoners, read.Projection.Facts.PrisonerColony, parts, inFlight)
	}
	if !harvesting {
		// Prisoners stay on herbal (#1239): a cut only better medicine
		// could serve is refused; the review asks for herbal instead.
		if blocked, ok := policy.CareLimitedHarvest(read.Projection.Facts.Prisoners, read.Projection.Facts.PrisonerColony, needs, policy.PartRecoveryNeeds(read.Projection.Facts.MedicalPawns, selection.Wants), inFlight); ok {
			clockSchedulerLog("%s: surgery %s on prisoner %s refused: medicine care limit %s allows no stocked medicine; herbal wanted", goal.Goal.ID, blocked.Recipe, blocked.Prisoner, policy.PrisonerMedicalCare)
		}
	}
	if !harvesting {
		// Peg-leg cycling (#1236): training, control or a reinstall.
		facts := read.Projection.Facts
		harvest, harvesting = policy.SelectPegCycle(facts.Prisoners, facts.PrisonerColony, facts.FoodDays, r.reviewer.policy.Prisoners(), selection.Wants, inFlight)
	}
	harvesting = harvesting && arbiter.tryClaim([]domain.PawnID{harvest.Prisoner})
	result.Harvest = harvesting
	if len(queue) == 0 && !harvesting {
		switch {
		case len(inFlight) > 0:
			result.Verdict = BuildingReasonExistingWork
		case len(selection.Wants) > 0:
			result.Verdict = awaitingMethod(selection.Wants[0].Reason)
		default:
			result.Verdict = BuildingReasonUsed
		}
		return result, nil
	}
	// The tick keys the method: a failed surgery leaves the part missing,
	// and the same recipe is queued again on a later review.
	hash := sha256.New()
	fmt.Fprintf(hash, "%s/%d/%d\n", goal.Goal.ID, goal.Goal.Epoch, expected.Tick)
	for _, choice := range queue {
		fmt.Fprintf(hash, "%s/%s/%d\n", choice.Pawn, choice.Recipe, choice.Part)
	}
	if harvesting {
		fmt.Fprintf(hash, "harvest/%s/%s/%d/%s\n", harvest.Prisoner, harvest.Recipe, harvest.Part, harvest.Surgeon)
	}
	method := domain.MethodID(fmt.Sprintf("restore-%x", hash.Sum(nil)[:16]))
	for _, previous := range goal.Methods {
		if previous.Method == method {
			result.Verdict = BuildingReasonUsed
			return result, nil
		}
	}
	id := domain.MintPlanID()
	actions := make([]domain.Action, 0, len(queue))
	for i, choice := range queue {
		surgery, err := domain.NewSurgery(domain.PawnID(choice.Pawn), choice.Recipe, choice.Part, false)
		if err != nil {
			return RoutineSurgeryResult{}, err
		}
		action, err := domain.NewSurgeryAction(domain.ActionID(fmt.Sprintf("%s-%d", id, i)), surgery)
		if err != nil {
			return RoutineSurgeryResult{}, err
		}
		actions = append(actions, action)
	}
	if harvesting {
		cut, err := domain.NewSurgery(harvest.Prisoner, harvest.Recipe, harvest.Part, harvest.Violation)
		if err == nil && harvest.Surgeon != "" {
			cut, err = cut.WithSurgeon(harvest.Surgeon) // peg-leg training's doctor (#1253)
		}
		if err != nil {
			return RoutineSurgeryResult{}, err
		}
		action, err := domain.NewSurgeryAction(domain.ActionID(fmt.Sprintf("%s-%d", id, len(actions))), cut)
		if err != nil {
			return RoutineSurgeryResult{}, err
		}
		actions = append(actions, action)
	}
	plan, err := domain.NewPlan(id, 1, actions)
	if err != nil {
		return RoutineSurgeryResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutineSurgeryResult{}, err
	}
	if p.session.State() != state {
		return RoutineSurgeryResult{}, fmt.Errorf("%w: step: p.session.State() != state", ErrControl)
	}
	if _, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, method, plan); err != nil {
		return RoutineSurgeryResult{}, err
	}
	result.Verdict, result.Plan = BuildingReasonAdmitted, id
	return result, nil
}
