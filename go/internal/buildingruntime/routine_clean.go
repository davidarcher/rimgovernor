package buildingruntime

import (
	"context"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// RoutineCleanSource reuses the generic colony read for the filth census
// and the existing tend pawn read
// (already requests combat+work+care details) for cleaner eligibility: dead/
// downed/drafted/mental state, health, existing job and whether Cleaning is
// disabled outright. No new native call is introduced for this slice.
type RoutineCleanSource interface {
	observation.ColonySource
	ReadEmergency(context.Context, *c.Identity) (bridge.EmergencyObservation, bridge.Result, error)
	ReadTendPawns(context.Context, *c.Identity, []string) (*n.ListPawnsReply, bridge.Result, error)
}

type RoutineCleanPlanner struct {
	reviewer *RoutineReviewer
	native   RoutineCleanSource
}
type RoutineCleanResult struct {
	Verdict
	Plan domain.PlanID
}

func NewRoutineCleanPlanner(reviewer *RoutineReviewer, native RoutineCleanSource) (*RoutineCleanPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, fmt.Errorf("%w: NewRoutineCleanPlanner: reviewer == nil || native == nil", ErrControl)
	}
	return &RoutineCleanPlanner{reviewer, native}, nil
}
func (r *RoutineCleanPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoutineCleanResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutineCleanResult{Verdict: BuildingReasonDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil || state.Snapshot.Native == 0 {
		return RoutineCleanResult{}, fmt.Errorf("%w: step: !state.ObservationKnown || state.Snapshot.Validate() != nil || state.Snapshot.Native == 0", ErrControl)
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutineCleanResult{}, err
	}
	if !review.Enabled || !review.Snapshot.Matches(state.Snapshot) {
		return RoutineCleanResult{Verdict: BuildingReasonNoReview}, nil
	}
	goal, workable, err := p.journal.Workable(call, review, policy.MaintainCleanFacilities)
	if err != nil {
		return RoutineCleanResult{}, err
	}
	if !workable {
		return RoutineCleanResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	// MaintainCleanFacilities competes for the same bounded concurrent-project
	// capacity as comfort/expansion/other priority>=3 autopilot goals; only act
	// while this review's arbitration actually selected it.
	selected := false
	for _, row := range review.Development.Rows {
		selected = selected || row.Goal == policy.MaintainCleanFacilities && row.Selected
	}
	if !selected {
		return RoutineCleanResult{Verdict: awaitingSlot(string(policy.MaintainCleanFacilities))}, nil
	}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoutineCleanResult{}, err
		}
		if store.PlanOpen(plan) {
			return RoutineCleanResult{Verdict: BuildingReasonExistingWork}, nil
		}
	}
	identity, _, err := r.native.Identity(call)
	if err != nil {
		return RoutineCleanResult{}, err
	}
	expected, err := observation.DecodeIdentity(identity)
	if err != nil {
		return RoutineCleanResult{}, err
	}
	if !routineBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoutineCleanResult{}, fmt.Errorf("%w: step: !routineBuildingBoundary(expected, state.Snapshot, review.Tick)", ErrControl)
	}
	started := r.reviewer.clock.Now()
	reading, err := r.reviewer.observeRooms(call, r.native.(observation.RoutineSource), expected, domain.Unknown[[]policy.ConstructionClaim]())
	if err != nil {
		return RoutineCleanResult{}, err
	}
	// The bounded response reads the same room census, coverage and latch
	// the review did: filth outside a latched dirty workspace is ordinary
	// colonist work, never a direct order.
	reading.Projection.Facts.Upkeep.Rooms = reading.Projection.Rooms
	if pawns, known := reading.Projection.WorkPawns.Value(); known {
		reading.Projection.Facts.Labor = policy.RoutineLabor(pawns)
	}
	reading.Projection.Facts.CleaningContext(expected.Tick)
	upkeepReview, err := policy.ReviewUpkeepWith(reading.Projection.Facts.Upkeep, review.Latches.Upkeep, nil, r.reviewer.policy.Cleanliness)
	if err != nil {
		return RoutineCleanResult{}, err
	}
	var targetIDs []string
	for _, need := range upkeepReview.Needs {
		if need.Goal == policy.MaintainCleanFacilities {
			targetIDs, _ = need.Targets.Value()
		}
	}
	if len(targetIDs) == 0 {
		return RoutineCleanResult{Verdict: BuildingReasonUsed}, nil
	}
	byID := map[string]policy.UpkeepFilth{}
	if rows, known := reading.Projection.Facts.Upkeep.Filth.Value(); known {
		for _, row := range rows {
			byID[row.ID] = row
		}
	}
	var filth []policy.UpkeepFilth
	for _, id := range targetIDs {
		if row, ok := byID[id]; ok {
			filth = append(filth, row)
		}
	}
	if len(filth) > 8 {
		filth = filth[:8]
	}
	identityRef := boundary.Identity(state.Snapshot)
	emergency, _, err := r.native.ReadEmergency(call, identityRef)
	if err != nil {
		return RoutineCleanResult{}, err
	}
	if _, err = boundary.Context(emergency.Context, state.Snapshot); err != nil || emergency.Context.GetTick() < int64(review.Tick) {
		return RoutineCleanResult{}, fmt.Errorf("%w: step: err != nil || emergency.Context.GetTick() < int64(review.Tick)", ErrControl)
	}
	complete, known := emergency.Facts.ColonistsComplete.Value()
	if !known || !complete || len(emergency.Facts.Colonists) == 0 {
		return RoutineCleanResult{Verdict: BuildingReasonUsed}, nil
	}
	ids := make([]string, 0, len(emergency.Facts.Colonists))
	for _, pawn := range emergency.Facts.Colonists {
		ids = append(ids, string(pawn.ID))
	}
	reply, _, err := r.native.ReadTendPawns(call, identityRef, ids)
	if err != nil {
		return RoutineCleanResult{}, err
	}
	observed := reply.GetObserved()
	if observed == nil {
		return RoutineCleanResult{}, fmt.Errorf("%w: step: observed == nil", ErrControl)
	}
	if _, err = boundary.Context(observed.Context, state.Snapshot); err != nil {
		return RoutineCleanResult{}, fmt.Errorf("%w: step: err != nil", ErrControl)
	}
	if len(observed.Pawns) != len(ids) {
		return RoutineCleanResult{}, fmt.Errorf("%w: step: len(observed.Pawns) != len(ids)", ErrControl)
	}
	var pawns []policy.CleanCandidateFacts
	seen := map[string]bool{}
	for _, row := range observed.Pawns {
		if row == nil || row.Pawn == nil || seen[row.Pawn.GetId()] {
			return RoutineCleanResult{}, fmt.Errorf("%w: step: row == nil || row.Pawn == nil || seen[row.Pawn.GetId()]", ErrControl)
		}
		seen[row.Pawn.GetId()] = true
		pawn := domain.PawnID(row.Pawn.GetId())
		facts := cleanCandidateFacts(pawn, row)
		pawns = append(pawns, facts)
	}
	target, pawn, ok := policy.SelectClean(filth, pawns)
	if ok && !arbiter.tryClaim([]domain.PawnID{pawn}, "clean-target:"+target.ID) {
		ok = false
	}
	if !ok {
		return RoutineCleanResult{Verdict: BuildingReasonUsed}, nil
	}
	clean, err := domain.NewClean(pawn, target.ID, target.Cell)
	if err != nil {
		return RoutineCleanResult{}, err
	}
	// Keyed by filth and attempt count, not pawn: a fresh attempt after an
	// interrupted or refused try picks whichever cleaner is currently best.
	prefix := fmt.Sprintf("clean-%s-", target.ID)
	attempt := medicalAttemptCount(goal.History, goal.Goal.Epoch, prefix)
	if attempt >= maxMedicalAttemptsPerPatient {
		return RoutineCleanResult{Verdict: BuildingReasonExhausted}, nil
	}
	method := domain.MethodID(fmt.Sprintf("%s%d", prefix, attempt))
	id := domain.MintPlanID()
	action, err := domain.NewCleanAction(domain.ActionID(fmt.Sprintf("%s-0", id)), clean)
	if err != nil {
		return RoutineCleanResult{}, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoutineCleanResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutineCleanResult{}, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoutineCleanResult{}, fmt.Errorf("%w: step: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge", ErrControl)
	}
	if _, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, method, plan); err != nil {
		return RoutineCleanResult{}, err
	}
	return RoutineCleanResult{Verdict: BuildingReasonAdmitted, Plan: id}, nil
}

func cleanCandidateFacts(pawn domain.PawnID, row *n.PawnState) policy.CleanCandidateFacts {
	facts := policy.CleanCandidateFacts{Pawn: pawn, Dead: boundary.FactBool(row.Dead), Downed: boundary.FactBool(row.Downed), Drafted: boundary.FactBool(row.Drafted), MentalState: boundary.FactPresence(row.MentalState, row.Issues, "mental_state")}
	if row.Job != nil && !boundary.IssueField(row.Job.Issues, "player_forced") {
		facts.PlayerForced = boundary.FactBool(row.Job.PlayerForced)
	}
	if health := row.Health; health != nil && !boundary.IssueField(health.Issues, "health") {
		facts.NeedsTend, facts.Bleeding = boundary.FactBool(health.NeedsTend), boundary.FactBool(health.Bleeding)
	}
	if settings := row.Settings; settings != nil && !boundary.IssueField(settings.Issues, "work") {
		facts.CleaningEnabled = cleaningWorkEnabled(settings.Work)
	}
	return facts
}

func cleaningWorkEnabled(work []*n.WorkSetting) domain.Fact[bool] {
	for _, w := range work {
		if w == nil || w.GetDefName() != "Cleaning" {
			continue
		}
		if w.Disabled == nil || w.Priority == nil {
			return domain.Unknown[bool]()
		}
		// A direct clean order is player-forced: native honours it whatever
		// the Work-tab priority says (OrderTool.FinishWorkGiverPlan), and the
		// bounded response exists precisely for colonies whose ordinary
		// coverage (Cleaning priority > 0) failed. Only real incapability
		// excludes a pawn.
		return domain.Known(!w.GetDisabled())
	}
	return domain.Unknown[bool]()
}
