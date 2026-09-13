package buildingruntime

import (
	"context"
	"crypto/sha256"
	"errors"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// RoutineSecureSuppliesSource reuses the generic colony read for the vulnerable
// item census (cell/definition included) and the existing tend pawn read
// (already requests combat+work+care details) for hauler eligibility: dead/
// downed/drafted/mental state, health, existing job and the Hauling work
// setting. No new native call is introduced for this slice.
type RoutineSecureSuppliesSource interface {
	observation.ColonySource
	ReadEmergency(context.Context, *c.Identity) (bridge.EmergencyObservation, bridge.Result, error)
	ReadTendPawns(context.Context, *c.Identity, []string) (*n.ListPawnsReply, bridge.Result, error)
}
type RoutineSecureSuppliesPlanner struct {
	reviewer *RoutineReviewer
	native   RoutineSecureSuppliesSource
}
type RoutineSecureSuppliesResult struct {
	Reason RoutineBuildingReason
	Plan   domain.PlanID
}

func NewRoutineSecureSuppliesPlanner(reviewer *RoutineReviewer, native RoutineSecureSuppliesSource) (*RoutineSecureSuppliesPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, ErrControl
	}
	return &RoutineSecureSuppliesPlanner{reviewer, native}, nil
}
func (r *RoutineSecureSuppliesPlanner) Step(ctx context.Context) (RoutineSecureSuppliesResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, false)
	if err != nil {
		return RoutineSecureSuppliesResult{}, err
	}
	defer done()
	return r.step(call, epoch)
}
func (r *RoutineSecureSuppliesPlanner) step(call, epoch context.Context) (RoutineSecureSuppliesResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutineSecureSuppliesResult{Reason: BuildingMethodDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil || state.Snapshot.Native == 0 {
		return RoutineSecureSuppliesResult{}, ErrControl
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutineSecureSuppliesResult{}, err
	}
	if !review.Enabled || !review.Snapshot.Matches(state.Snapshot) {
		return RoutineSecureSuppliesResult{Reason: BuildingMethodNoReview}, nil
	}
	var goal store.GoalState
	found := false
	for _, binding := range review.Goals {
		if binding.Need == policy.SecureSupplies {
			goal, err = p.journal.LoadGoal(call, binding.Goal)
			found = true
			break
		}
	}
	if err != nil {
		return RoutineSecureSuppliesResult{}, err
	}
	if !found || goal.Goal.Status != domain.GoalActive || goal.Goal.Need != domain.NeedDeficit {
		return RoutineSecureSuppliesResult{Reason: BuildingMethodNoDeficit}, nil
	}
	// SecureSupplies competes for the same bounded concurrent-project capacity
	// as comfort/expansion/other priority>=3 autopilot goals; only act while
	// this review's arbitration actually selected it.
	selected := false
	for _, row := range review.Development.Rows {
		selected = selected || row.Goal == policy.SecureSupplies && row.Selected
	}
	if !selected {
		return RoutineSecureSuppliesResult{Reason: BuildingMethodRefused}, nil
	}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoutineSecureSuppliesResult{}, err
		}
		if domain.GoalWorkOpen(plan.Progress) {
			return RoutineSecureSuppliesResult{Reason: BuildingMethodExistingWork}, nil
		}
	}
	identity, _, err := r.native.Identity(call)
	if err != nil {
		return RoutineSecureSuppliesResult{}, err
	}
	expected, err := observation.DecodeIdentity(identity)
	if err != nil {
		return RoutineSecureSuppliesResult{}, err
	}
	if !routineBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoutineSecureSuppliesResult{}, ErrControl
	}
	started := r.reviewer.clock.Now()
	reading, err := observation.ObserveColony(call, r.native, r.reviewer.clock, expected, r.reviewer.maxAge, false, nil)
	if err != nil {
		return RoutineSecureSuppliesResult{}, err
	}
	upkeepReview, err := policy.ReviewUpkeep(reading.Projection.Facts.Upkeep, policy.UpkeepHistory{}, nil)
	if err != nil {
		return RoutineSecureSuppliesResult{}, err
	}
	var targetIDs []string
	for _, need := range upkeepReview.Needs {
		if need.Goal == policy.SecureSupplies {
			targetIDs, _ = need.Targets.Value()
		}
	}
	if len(targetIDs) == 0 {
		return RoutineSecureSuppliesResult{Reason: BuildingMethodUsed}, nil
	}
	byID := map[string]policy.UpkeepItem{}
	if rows, known := reading.Projection.Facts.Upkeep.Items.Value(); known {
		for _, row := range rows {
			byID[row.ID] = row
		}
	}
	var items []policy.UpkeepItem
	for _, id := range targetIDs {
		if item, ok := byID[id]; ok {
			items = append(items, item)
		}
	}
	if len(items) > 8 {
		items = items[:8]
	}
	identityRef := boundaryIdentity(state.Snapshot)
	emergency, _, err := r.native.ReadEmergency(call, identityRef)
	if err != nil {
		return RoutineSecureSuppliesResult{}, err
	}
	if _, err = boundaryContext(emergency.Context, state.Snapshot); err != nil || emergency.Context.GetTick() < int64(review.Tick) {
		return RoutineSecureSuppliesResult{}, ErrControl
	}
	complete, known := emergency.Facts.ColonistsComplete.Value()
	if !known || !complete || len(emergency.Facts.Colonists) == 0 {
		return RoutineSecureSuppliesResult{Reason: BuildingMethodUsed}, nil
	}
	ids := make([]string, 0, len(emergency.Facts.Colonists))
	for _, pawn := range emergency.Facts.Colonists {
		ids = append(ids, string(pawn.ID))
	}
	reply, _, err := r.native.ReadTendPawns(call, identityRef, ids)
	if err != nil {
		return RoutineSecureSuppliesResult{}, err
	}
	observed := reply.GetObserved()
	if observed == nil {
		return RoutineSecureSuppliesResult{}, ErrControl
	}
	if _, err = boundaryContext(observed.Context, state.Snapshot); err != nil {
		return RoutineSecureSuppliesResult{}, ErrControl
	}
	counts := observed.Completeness
	if counts == nil || counts.Page == nil || !counts.Page.GetComplete() || counts.Page.GetNextCursor() != "" || counts.Matched == nil || counts.Returned == nil || counts.Unreadable == nil || counts.GetUnreadable() != 0 || counts.GetMatched() != uint64(len(ids)) || counts.GetReturned() != uint64(len(ids)) || len(observed.Pawns) != len(ids) {
		return RoutineSecureSuppliesResult{}, ErrControl
	}
	preferences, loadErr := p.journal.LoadWorkPreferences(call, state.Snapshot.Plan)
	if loadErr != nil && !errors.Is(loadErr, store.ErrNotFound) {
		return RoutineSecureSuppliesResult{}, loadErr
	}
	if preferences.Revision != review.WorkPreferenceRevision {
		return RoutineSecureSuppliesResult{}, ErrControl
	}
	overridden := map[domain.PawnID]bool{}
	for _, o := range preferences.Overrides {
		if o.Work == policy.WorkType("Hauling") && o.Priority == 0 {
			overridden[domain.PawnID(o.Pawn)] = true
		}
	}
	var pawns []policy.SecureSuppliesHaulerFacts
	seen := map[string]bool{}
	for _, row := range observed.Pawns {
		if row == nil || row.Pawn == nil || seen[row.Pawn.GetId()] {
			return RoutineSecureSuppliesResult{}, ErrControl
		}
		seen[row.Pawn.GetId()] = true
		pawn := domain.PawnID(row.Pawn.GetId())
		facts := secureSuppliesHaulerFacts(pawn, row)
		if overridden[pawn] {
			facts.HaulingEnabled = domain.Known(false)
		}
		pawns = append(pawns, facts)
	}
	item, pawn, ok := policy.SelectSecureSupplies(items, pawns)
	if !ok {
		return RoutineSecureSuppliesResult{Reason: BuildingMethodUsed}, nil
	}
	haul, err := domain.NewHaul(pawn, item.ID, item.Definition, item.Cell)
	if err != nil {
		return RoutineSecureSuppliesResult{}, err
	}
	// Keyed by item and attempt count, not pawn: a fresh attempt after an
	// interrupted or refused try picks whichever hauler is currently best.
	prefix := fmt.Sprintf("secure-supplies-%s-", item.ID)
	attempt := medicalAttemptCount(goal.Methods, goal.Goal.Epoch, prefix)
	if attempt >= maxMedicalAttemptsPerPatient {
		return RoutineSecureSuppliesResult{Reason: BuildingMethodExhausted}, nil
	}
	method := domain.MethodID(fmt.Sprintf("%s%d", prefix, attempt))
	digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%d/%s", goal.Goal.ID, goal.Goal.Epoch, method)))
	id := domain.PlanID(fmt.Sprintf("routine-secure-supplies-%x", digest[:16]))
	action, err := domain.NewHaulAction(domain.ActionID(fmt.Sprintf("%s-0", id)), haul)
	if err != nil {
		return RoutineSecureSuppliesResult{}, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoutineSecureSuppliesResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutineSecureSuppliesResult{}, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoutineSecureSuppliesResult{}, ErrControl
	}
	if _, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, method, plan); err != nil {
		return RoutineSecureSuppliesResult{}, err
	}
	return RoutineSecureSuppliesResult{Reason: BuildingMethodAdmitted, Plan: id}, nil
}

func secureSuppliesHaulerFacts(pawn domain.PawnID, row *n.PawnState) policy.SecureSuppliesHaulerFacts {
	facts := policy.SecureSuppliesHaulerFacts{Pawn: pawn, Dead: draftBool(row.Dead), Downed: draftBool(row.Downed), Drafted: draftBool(row.Drafted), MentalState: draftPresence(row.MentalState, row.Issues, "mental_state")}
	if row.Job != nil && !tendIssue(row.Job.Issues, "player_forced") {
		facts.PlayerForced = draftBool(row.Job.PlayerForced)
	}
	if health := row.Health; health != nil && !tendIssue(health.Issues, "health") {
		facts.NeedsTend, facts.Bleeding = draftBool(health.NeedsTend), draftBool(health.Bleeding)
	}
	if settings := row.Settings; settings != nil && !tendIssue(settings.Issues, "work") {
		facts.HaulingEnabled = haulingWorkEnabled(settings.Work)
	}
	return facts
}

func haulingWorkEnabled(work []*n.WorkSetting) domain.Fact[bool] {
	for _, w := range work {
		if w == nil || w.GetDefName() != "Hauling" {
			continue
		}
		if w.Disabled == nil || w.Priority == nil {
			return domain.Unknown[bool]()
		}
		return domain.Known(!w.GetDisabled() && w.GetPriority() > 0)
	}
	return domain.Unknown[bool]()
}
