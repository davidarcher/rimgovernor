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

// RoutineHaulSource reuses the generic colony read for the loose-item census
// (cell/definition included) and the existing tend pawn read (already
// requests combat+work+care details) for hauler eligibility: dead/downed/
// drafted/mental state, health, existing job and the Hauling work setting.
// No new native call is introduced for this slice. This is exactly
// RoutineSecureSuppliesSource minus PreviewZone: MaintainStorage only
// delivers into storage SecureSupplies/EnsureFoodStorage already made legal
// (native's own RequireSafeStorage picks the destination cell); it never
// proposes a new zone.
type RoutineHaulSource interface {
	observation.ColonySource
	ReadEmergency(context.Context, *c.Identity) (bridge.EmergencyObservation, bridge.Result, error)
	ReadTendPawns(context.Context, *c.Identity, []string) (*n.ListPawnsReply, bridge.Result, error)
}

type RoutineHaulPlanner struct {
	reviewer *RoutineReviewer
	native   RoutineHaulSource
}
type RoutineHaulResult struct {
	Reason RoutineBuildingReason
	Plan   domain.PlanID
}

func NewRoutineHaulPlanner(reviewer *RoutineReviewer, native RoutineHaulSource) (*RoutineHaulPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, ErrControl
	}
	return &RoutineHaulPlanner{reviewer, native}, nil
}
func (r *RoutineHaulPlanner) Step(ctx context.Context) (RoutineHaulResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, false)
	if err != nil {
		return RoutineHaulResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

// step delivers ordinary (non-decaying) MaintainStorage items to whatever
// legal storage native picks, reusing the exact same item+hauler selection
// SecureSupplies uses for its own (decaying/vulnerable) item list --
// policy.SelectSecureSupplies is generic over []UpkeepItem/hauler facts, not
// coupled to the SecureSupplies goal itself. policy.ReviewUpkeep keeps the
// two goals' own UpkeepItem selections disjoint (Deterioration == 0 here,
// > 0 for SecureSupplies), so the two planners never race over the same
// real-world item.
func (r *RoutineHaulPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoutineHaulResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutineHaulResult{Reason: BuildingMethodDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil || state.Snapshot.Native == 0 {
		return RoutineHaulResult{}, ErrControl
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutineHaulResult{}, err
	}
	if !review.Enabled || !review.Snapshot.Matches(state.Snapshot) {
		return RoutineHaulResult{Reason: BuildingMethodNoReview}, nil
	}
	var goal store.GoalState
	found := false
	for _, binding := range review.Goals {
		if binding.Need == policy.MaintainStorage {
			goal, err = p.journal.LoadGoal(call, binding.Goal)
			found = true
			break
		}
	}
	if err != nil {
		return RoutineHaulResult{}, err
	}
	if !found || goal.Goal.Status != domain.GoalActive || goal.Goal.Need != domain.NeedDeficit {
		return RoutineHaulResult{Reason: BuildingMethodNoDeficit}, nil
	}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoutineHaulResult{}, err
		}
		if domain.GoalWorkOpen(plan.Progress) {
			return RoutineHaulResult{Reason: BuildingMethodExistingWork}, nil
		}
	}
	// MaintainStorage competes for the same bounded concurrent-project
	// capacity as comfort/expansion/other priority>=3 autopilot goals; only
	// act while this review's arbitration actually selected it. Checked after
	// existing-work above: RankDevelopment marks an in-flight goal Committed
	// but never re-Selected (development.go), so checking Selected first
	// would refuse a goal that already has a haul in flight on every review
	// cycle after admission, instead of recognizing it as existing work --
	// stalling completion and never letting the clock settle (issue #42).
	selected := false
	for _, row := range review.Development.Rows {
		selected = selected || row.Goal == policy.MaintainStorage && row.Selected
	}
	if !selected {
		return RoutineHaulResult{Reason: BuildingMethodRefused}, nil
	}
	identity, _, err := r.native.Identity(call)
	if err != nil {
		return RoutineHaulResult{}, err
	}
	expected, err := observation.DecodeIdentity(identity)
	if err != nil {
		return RoutineHaulResult{}, err
	}
	if !routineBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoutineHaulResult{}, ErrControl
	}
	started := r.reviewer.clock.Now()
	reading, err := observation.ObserveColony(call, r.native, r.reviewer.clock, expected, r.reviewer.maxAge, true, nil)
	if err != nil {
		return RoutineHaulResult{}, err
	}
	upkeepReview, err := policy.ReviewUpkeep(reading.Projection.Facts.Upkeep, policy.UpkeepHistory{}, nil)
	if err != nil {
		return RoutineHaulResult{}, err
	}
	var targetIDs []string
	for _, need := range upkeepReview.Needs {
		if need.Goal == policy.MaintainStorage {
			targetIDs, _ = need.Targets.Value()
		}
	}
	if len(targetIDs) == 0 {
		return RoutineHaulResult{Reason: BuildingMethodUsed}, nil
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
	identityRef := boundary.Identity(state.Snapshot)
	emergency, _, err := r.native.ReadEmergency(call, identityRef)
	if err != nil {
		return RoutineHaulResult{}, err
	}
	if _, err = boundary.Context(emergency.Context, state.Snapshot); err != nil || emergency.Context.GetTick() < int64(review.Tick) {
		return RoutineHaulResult{}, ErrControl
	}
	complete, known := emergency.Facts.ColonistsComplete.Value()
	if !known || !complete || len(emergency.Facts.Colonists) == 0 {
		return RoutineHaulResult{Reason: BuildingMethodUsed}, nil
	}
	ids := make([]string, 0, len(emergency.Facts.Colonists))
	for _, pawn := range emergency.Facts.Colonists {
		ids = append(ids, string(pawn.ID))
	}
	reply, _, err := r.native.ReadTendPawns(call, identityRef, ids)
	if err != nil {
		return RoutineHaulResult{}, err
	}
	observed := reply.GetObserved()
	if observed == nil {
		return RoutineHaulResult{}, ErrControl
	}
	if _, err = boundary.Context(observed.Context, state.Snapshot); err != nil {
		return RoutineHaulResult{}, ErrControl
	}
	counts := observed.Completeness
	if counts == nil || counts.Page == nil || !counts.Page.GetComplete() || counts.Page.GetNextCursor() != "" || counts.Matched == nil || counts.Returned == nil || counts.Unreadable == nil || counts.GetUnreadable() != 0 || counts.GetMatched() != uint64(len(ids)) || counts.GetReturned() != uint64(len(ids)) || len(observed.Pawns) != len(ids) {
		return RoutineHaulResult{}, ErrControl
	}
	preferences, loadErr := p.journal.LoadWorkPreferences(call, state.Snapshot.Plan)
	if loadErr != nil && !errors.Is(loadErr, store.ErrNotFound) {
		return RoutineHaulResult{}, loadErr
	}
	if preferences.Revision != review.WorkPreferenceRevision {
		return RoutineHaulResult{}, ErrControl
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
			return RoutineHaulResult{}, ErrControl
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
	if ok && !arbiter.tryClaim([]domain.PawnID{pawn}, "haul-item:"+item.ID) {
		ok = false
	}
	if !ok {
		return RoutineHaulResult{Reason: BuildingMethodUsed}, nil
	}
	haul, err := domain.NewHaul(pawn, item.ID, item.Definition, item.Cell)
	if err != nil {
		return RoutineHaulResult{}, err
	}
	// Keyed by item and attempt count, not pawn: a fresh attempt after an
	// interrupted or refused try picks whichever hauler is currently best.
	prefix := fmt.Sprintf("haul-%s-", item.ID)
	attempt := medicalAttemptCount(goal.Methods, goal.Goal.Epoch, prefix)
	if attempt >= maxMedicalAttemptsPerPatient {
		return RoutineHaulResult{Reason: BuildingMethodExhausted}, nil
	}
	method := domain.MethodID(fmt.Sprintf("%s%d", prefix, attempt))
	digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%d/%s", goal.Goal.ID, goal.Goal.Epoch, method)))
	id := domain.PlanID(fmt.Sprintf("routine-haul-%x", digest[:16]))
	action, err := domain.NewHaulAction(domain.ActionID(fmt.Sprintf("%s-0", id)), haul)
	if err != nil {
		return RoutineHaulResult{}, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoutineHaulResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutineHaulResult{}, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoutineHaulResult{}, ErrControl
	}
	if _, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, method, plan); err != nil {
		return RoutineHaulResult{}, err
	}
	return RoutineHaulResult{Reason: BuildingMethodAdmitted, Plan: id}, nil
}
