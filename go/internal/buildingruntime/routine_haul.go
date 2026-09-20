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
	result, err := r.propose(call, epoch)
	if err != nil || result.Kind != PlanProposed {
		return RoutineHaulResult{Reason: result.Reason}, err
	}
	return commitClaimed(call, arbiter, result.Proposal)
}

// commitClaimed is the first-arrival path a migrated planner keeps for its
// own Step(): claim the proposal's pawns and entities on arbiter and commit
// at once. The clock step never takes it; there the coordinator ranks the
// wave's proposals first (#622).
func commitClaimed(call context.Context, arbiter *stepArbiter, proposal *Proposal) (RoutineHaulResult, error) {
	if !arbiter.tryClaim(proposal.Claims.Pawns, proposal.Claims.Entities...) {
		return RoutineHaulResult{Reason: BuildingMethodUsed}, nil
	}
	plan, reason, err := proposal.commit(call)
	if err != nil {
		return RoutineHaulResult{}, err
	}
	return RoutineHaulResult{Reason: reason, Plan: plan}, nil
}

// propose plans one MaintainStorage haul without committing it: every read
// and selection runs here, and the returned proposal's commit runs the
// admission path the step used to run inline (#622).
func (r *RoutineHaulPlanner) propose(call, epoch context.Context) (PlanResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return PlanResult{Kind: PlanUnsupported, Reason: BuildingMethodDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil || state.Snapshot.Native == 0 {
		return PlanResult{}, ErrControl
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return PlanResult{}, err
	}
	if !review.Enabled || !review.Snapshot.Matches(state.Snapshot) {
		return PlanResult{Kind: PlanWaiting, Dependency: "routine review", Reason: BuildingMethodNoReview}, nil
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
		return PlanResult{}, err
	}
	if !found || goal.Goal.Status != domain.GoalActive || goal.Goal.Need != domain.NeedDeficit {
		return PlanResult{Kind: PlanDemandSatisfied, Reason: BuildingMethodNoDeficit}, nil
	}
	// MaintainStorage competes for the same bounded concurrent-project
	// capacity as comfort/expansion/other priority>=3 autopilot goals; only
	// act while this review's arbitration actually selected it. Checked after
	// existing-work above: RankDevelopment marks an in-flight goal Committed
	// but never re-Selected (development.go), so checking Selected first
	// would refuse a goal that already has a haul in flight on every review
	// cycle after admission, instead of recognizing it as existing work --
	// stalling completion and never letting the clock settle (issue #42).
	if err = cancelStalledHaulMethods(call, p.journal, goal, review.Tick, r.reviewer.policy.HaulStallTicks); err != nil {
		return PlanResult{}, err
	}
	selected := false
	for _, row := range review.Development.Rows {
		selected = selected || row.Goal == policy.MaintainStorage && row.Selected
	}
	if !selected {
		return PlanResult{Kind: PlanWaiting, Dependency: "development slot", Reason: BuildingMethodRefused}, nil
	}
	identity, _, err := r.native.Identity(call)
	if err != nil {
		return PlanResult{}, err
	}
	expected, err := observation.DecodeIdentity(identity)
	if err != nil {
		return PlanResult{}, err
	}
	if !routineBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return PlanResult{}, ErrControl
	}
	started := r.reviewer.clock.Now()
	reading, err := r.reviewer.observeColony(call, r.native, expected, nil)
	if err != nil {
		return PlanResult{}, err
	}
	upkeepReview, err := policy.ReviewUpkeep(reading.Projection.Facts.Upkeep, policy.UpkeepHistory{}, nil)
	if err != nil {
		return PlanResult{}, err
	}
	var targetIDs []string
	for _, need := range upkeepReview.Needs {
		if need.Goal == policy.MaintainStorage {
			targetIDs, _ = need.Targets.Value()
		}
	}
	if len(targetIDs) == 0 {
		return PlanResult{Kind: PlanDemandSatisfied, Reason: BuildingMethodUsed}, nil
	}
	if open, err := cancelStaleHaulMethods(call, p.journal, goal, targetIDs, review.Tick, r.reviewer.policy.HaulStallTicks); err != nil {
		return PlanResult{}, err
	} else if open {
		return PlanResult{Kind: PlanDemandSatisfied, Reason: BuildingMethodExistingWork}, nil
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
		return PlanResult{}, err
	}
	if _, err = boundary.Context(emergency.Context, state.Snapshot); err != nil || emergency.Context.GetTick() < int64(review.Tick) {
		return PlanResult{}, ErrControl
	}
	complete, known := emergency.Facts.ColonistsComplete.Value()
	if !known || !complete || len(emergency.Facts.Colonists) == 0 {
		return PlanResult{Kind: PlanWaiting, Dependency: "colonist census", Reason: BuildingMethodUsed}, nil
	}
	ids := make([]string, 0, len(emergency.Facts.Colonists))
	for _, pawn := range emergency.Facts.Colonists {
		ids = append(ids, string(pawn.ID))
	}
	reply, _, err := r.native.ReadTendPawns(call, identityRef, ids)
	if err != nil {
		return PlanResult{}, err
	}
	observed := reply.GetObserved()
	if observed == nil {
		return PlanResult{}, ErrControl
	}
	if _, err = boundary.Context(observed.Context, state.Snapshot); err != nil {
		return PlanResult{}, ErrControl
	}
	counts := observed.Completeness
	if counts == nil || counts.Page == nil || !counts.Page.GetComplete() || counts.Page.GetNextCursor() != "" || counts.Matched == nil || counts.Returned == nil || counts.Unreadable == nil || counts.GetUnreadable() != 0 || counts.GetMatched() != uint64(len(ids)) || counts.GetReturned() != uint64(len(ids)) || len(observed.Pawns) != len(ids) {
		return PlanResult{}, ErrControl
	}
	preferences, loadErr := p.journal.LoadWorkPreferences(call, state.Snapshot.Plan)
	if loadErr != nil && !errors.Is(loadErr, store.ErrNotFound) {
		return PlanResult{}, loadErr
	}
	if preferences.Revision != review.WorkPreferenceRevision {
		return PlanResult{}, ErrControl
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
			return PlanResult{}, ErrControl
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
		return PlanResult{Kind: PlanWaiting, Dependency: "eligible hauler", Reason: BuildingMethodUsed}, nil
	}
	haul, err := domain.NewHaul(pawn, item.ID, item.Definition, item.Cell)
	if err != nil {
		return PlanResult{}, err
	}
	// Keyed by item and attempt count, not pawn: a fresh attempt after an
	// interrupted or refused try picks whichever hauler is currently best.
	prefix := fmt.Sprintf("haul-%s-", item.ID)
	attempt, err := haulAttemptCount(call, p.journal, goal, prefix)
	if err != nil {
		return PlanResult{}, err
	}
	if attempt >= maxMedicalAttemptsPerPatient {
		if err = yieldDevelopment(call, p.journal, review, policy.MaintainStorage); err != nil {
			return PlanResult{}, err
		}
		return PlanResult{Kind: PlanWaiting, Dependency: "retry budget", Reason: BuildingMethodExhausted}, nil
	}
	method := domain.MethodID(fmt.Sprintf("%s%d", prefix, attempt))
	digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%d/%s", goal.Goal.ID, goal.Goal.Epoch, method)))
	id := domain.PlanID(fmt.Sprintf("routine-haul-%x", digest[:16]))
	action, err := domain.NewHaulAction(domain.ActionID(fmt.Sprintf("%s-0", id)), haul)
	if err != nil {
		return PlanResult{}, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return PlanResult{}, err
	}
	proposal := &Proposal{ID: "haul/" + string(id), Planner: "haul", Goal: goal.Goal.ID, Priority: plannerMaintenance, Urgency: goal.Goal.Priority, Snapshot: state.Snapshot, Facts: factsColony,
		Claims: ResourceClaims{Pawns: []domain.PawnID{pawn}, Entities: []string{"haul-item:" + item.ID}}, ValidTick: review.Tick, Actions: []domain.Action{action}}
	proposal.commit = func(ctx context.Context) (domain.PlanID, RoutineBuildingReason, error) {
		if err := p.current(ctx, epoch); err != nil {
			return "", "", err
		}
		elapsed := r.reviewer.clock.Now().Sub(started)
		if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
			return "", "", ErrControl
		}
		if _, err := p.journal.CommitGoalMethod(ctx, goal.Goal.ID, goal.Revision, method, plan); err != nil {
			return "", "", err
		}
		return id, BuildingMethodAdmitted, nil
	}
	return PlanResult{Kind: PlanProposed, Proposal: proposal, Reason: BuildingMethodAdmitted}, nil
}
