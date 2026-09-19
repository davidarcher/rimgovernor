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
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// RoutineCleanSource reuses the generic colony read for the filth census
// (cell included, unlike the exact-ID CAS lookup this census lacks — see
// bridge.ReadFilthTarget's doc comment) and the existing tend pawn read
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
	Reason RoutineBuildingReason
	Plan   domain.PlanID
}

func NewRoutineCleanPlanner(reviewer *RoutineReviewer, native RoutineCleanSource) (*RoutineCleanPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, ErrControl
	}
	return &RoutineCleanPlanner{reviewer, native}, nil
}
func (r *RoutineCleanPlanner) Step(ctx context.Context) (RoutineCleanResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, false)
	if err != nil {
		return RoutineCleanResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}
func (r *RoutineCleanPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoutineCleanResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutineCleanResult{Reason: BuildingMethodDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil || state.Snapshot.Native == 0 {
		return RoutineCleanResult{}, ErrControl
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutineCleanResult{}, err
	}
	if !review.Enabled || !review.Snapshot.Matches(state.Snapshot) {
		return RoutineCleanResult{Reason: BuildingMethodNoReview}, nil
	}
	var goal store.GoalState
	found := false
	for _, binding := range review.Goals {
		if binding.Need == policy.MaintainCleanFacilities {
			goal, err = p.journal.LoadGoal(call, binding.Goal)
			found = true
			break
		}
	}
	if err != nil {
		return RoutineCleanResult{}, err
	}
	if !found || goal.Goal.Status != domain.GoalActive || goal.Goal.Need != domain.NeedDeficit {
		return RoutineCleanResult{Reason: BuildingMethodNoDeficit}, nil
	}
	// MaintainCleanFacilities competes for the same bounded concurrent-project
	// capacity as comfort/expansion/other priority>=3 autopilot goals; only act
	// while this review's arbitration actually selected it.
	selected := false
	for _, row := range review.Development.Rows {
		selected = selected || row.Goal == policy.MaintainCleanFacilities && row.Selected
	}
	if !selected {
		return RoutineCleanResult{Reason: BuildingMethodRefused}, nil
	}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoutineCleanResult{}, err
		}
		if domain.GoalWorkOpen(plan.Progress) {
			return RoutineCleanResult{Reason: BuildingMethodExistingWork}, nil
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
		return RoutineCleanResult{}, ErrControl
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
		return RoutineCleanResult{Reason: BuildingMethodUsed}, nil
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
		return RoutineCleanResult{}, ErrControl
	}
	complete, known := emergency.Facts.ColonistsComplete.Value()
	if !known || !complete || len(emergency.Facts.Colonists) == 0 {
		return RoutineCleanResult{Reason: BuildingMethodUsed}, nil
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
		return RoutineCleanResult{}, ErrControl
	}
	if _, err = boundary.Context(observed.Context, state.Snapshot); err != nil {
		return RoutineCleanResult{}, ErrControl
	}
	counts := observed.Completeness
	if counts == nil || counts.Page == nil || !counts.Page.GetComplete() || counts.Page.GetNextCursor() != "" || counts.Matched == nil || counts.Returned == nil || counts.Unreadable == nil || counts.GetUnreadable() != 0 || counts.GetMatched() != uint64(len(ids)) || counts.GetReturned() != uint64(len(ids)) || len(observed.Pawns) != len(ids) {
		return RoutineCleanResult{}, ErrControl
	}
	preferences, loadErr := p.journal.LoadWorkPreferences(call, state.Snapshot.Plan)
	if loadErr != nil && !errors.Is(loadErr, store.ErrNotFound) {
		return RoutineCleanResult{}, loadErr
	}
	if preferences.Revision != review.WorkPreferenceRevision {
		return RoutineCleanResult{}, ErrControl
	}
	overridden := map[domain.PawnID]bool{}
	for _, o := range preferences.Overrides {
		if o.Work == policy.WorkType("Cleaning") && o.Priority == 0 {
			overridden[domain.PawnID(o.Pawn)] = true
		}
	}
	var pawns []policy.CleanCandidateFacts
	seen := map[string]bool{}
	for _, row := range observed.Pawns {
		if row == nil || row.Pawn == nil || seen[row.Pawn.GetId()] {
			return RoutineCleanResult{}, ErrControl
		}
		seen[row.Pawn.GetId()] = true
		pawn := domain.PawnID(row.Pawn.GetId())
		facts := cleanCandidateFacts(pawn, row)
		if overridden[pawn] {
			facts.CleaningEnabled = domain.Known(false)
		}
		pawns = append(pawns, facts)
	}
	target, pawn, ok := policy.SelectClean(filth, pawns)
	if ok && !arbiter.tryClaim([]domain.PawnID{pawn}, "clean-target:"+target.ID) {
		ok = false
	}
	if !ok {
		return RoutineCleanResult{Reason: BuildingMethodUsed}, nil
	}
	clean, err := domain.NewClean(pawn, target.ID, target.Cell)
	if err != nil {
		return RoutineCleanResult{}, err
	}
	// Keyed by filth and attempt count, not pawn: a fresh attempt after an
	// interrupted or refused try picks whichever cleaner is currently best.
	prefix := fmt.Sprintf("clean-%s-", target.ID)
	attempt := medicalAttemptCount(goal.Methods, goal.Goal.Epoch, prefix)
	if attempt >= maxMedicalAttemptsPerPatient {
		if err = yieldDevelopment(call, p.journal, review, policy.MaintainCleanFacilities); err != nil {
			return RoutineCleanResult{}, err
		}
		return RoutineCleanResult{Reason: BuildingMethodExhausted}, nil
	}
	method := domain.MethodID(fmt.Sprintf("%s%d", prefix, attempt))
	digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%d/%s", goal.Goal.ID, goal.Goal.Epoch, method)))
	id := domain.PlanID(fmt.Sprintf("routine-clean-%x", digest[:16]))
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
		return RoutineCleanResult{}, ErrControl
	}
	if _, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, method, plan); err != nil {
		return RoutineCleanResult{}, err
	}
	return RoutineCleanResult{Reason: BuildingMethodAdmitted, Plan: id}, nil
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
		// excludes a pawn; controller-issued priority-0 overrides are
		// honoured by the caller.
		return domain.Known(!w.GetDisabled())
	}
	return domain.Unknown[bool]()
}
