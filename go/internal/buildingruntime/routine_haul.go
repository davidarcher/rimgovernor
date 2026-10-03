package buildingruntime

import (
	"context"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	snap "github.com/davidarcher/RimGovernor/go/internal/snapshot"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// RoutineHaulSource reuses the generic colony read for the loose-item census
// (cell/definition included) and the existing tend pawn read (already
// requests combat+work+care details) for hauler eligibility: dead/downed/
// drafted/mental state, health, existing job and the Hauling work setting.
// No new native call is introduced for this slice. This is exactly
// RoutineSecureSuppliesSource minus PreviewZone: MaintainStorage only
// delivers into storage SecureSupplies/MaintainFoodStorage already made legal
// (native haul picks the destination cell); it never
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
	Verdict
	Plan domain.PlanID
	// NativeWorkTicks asks for a clock window while an ordered haul is
	// still on its way (haulWait).
	NativeWorkTicks uint32
}

// haulTransitTicks is the window lent while a pawn carries out an ordered
// haul, after which the census is read again.
const haulTransitTicks = 250

// haulWait is the result when no item and hauler pair is left to propose.
// Haul is an intent-mode kind: an applied haul completed its method when it
// was ordered, and while a pawn is still on its way the item stays listed.
// Only game time moves it, so the planner lends the clock a short window.
func haulWait(items []policy.UpkeepItem, pawns []policy.SecureSuppliesHaulerFacts) PlanResult {
	if policy.HaulInTransit(items, pawns) {
		return PlanResult{Kind: PlanWaiting, Dependency: "haul in transit", NativeWorkTicks: haulTransitTicks, Verdict: BuildingReasonExistingWork}
	}
	return PlanResult{Kind: PlanWaiting, Dependency: "eligible hauler", Verdict: BuildingReasonUsed}
}

func NewRoutineHaulPlanner(reviewer *RoutineReviewer, native RoutineHaulSource) (*RoutineHaulPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, fmt.Errorf("%w: NewRoutineHaulPlanner: reviewer == nil || native == nil", ErrControl)
	}
	return &RoutineHaulPlanner{reviewer, native}, nil
}

// propose plans one MaintainStorage haul without committing it: every read
// and selection runs here, and the returned proposal's commit runs the
// admission path the step used to run inline (#622).
func (r *RoutineHaulPlanner) propose(call, epoch context.Context) (PlanResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return PlanResult{Kind: PlanUnsupported, Verdict: BuildingReasonDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil || state.Snapshot.Native == 0 {
		return PlanResult{}, fmt.Errorf("%w: propose: !state.ObservationKnown || state.Snapshot.Validate() != nil || state.Snapshot.Native == 0", ErrControl)
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return PlanResult{}, err
	}
	if !review.Enabled || !review.Snapshot.Matches(state.Snapshot) {
		return PlanResult{Kind: PlanWaiting, Dependency: "routine review", Verdict: BuildingReasonNoReview}, nil
	}
	call, recorded := recordPlannerStep(call, policy.MaintainStorage, state.Snapshot, review.Tick)
	defer recorded()
	goal, workable, err := p.journal.Workable(call, review, policy.MaintainStorage)
	if err != nil {
		return PlanResult{}, err
	}
	if !workable {
		return PlanResult{Kind: PlanDemandSatisfied, Verdict: BuildingReasonNoDeficit}, nil
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
		return PlanResult{Kind: PlanWaiting, Dependency: "development slot", Verdict: BuildingReasonRefused}, nil
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
		return PlanResult{}, fmt.Errorf("%w: propose: !routineBuildingBoundary(expected, state.Snapshot, review.Tick)", ErrControl)
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
		return PlanResult{Kind: PlanDemandSatisfied, Verdict: BuildingReasonUsed}, nil
	}
	if open, err := cancelStaleHaulMethods(call, p.journal, goal, targetIDs); err != nil {
		return PlanResult{}, err
	} else if open {
		return PlanResult{Kind: PlanDemandSatisfied, Verdict: BuildingReasonExistingWork}, nil
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
		return PlanResult{}, fmt.Errorf("%w: propose: err != nil || emergency.Context.GetTick() < int64(review.Tick)", ErrControl)
	}
	complete, known := emergency.Facts.ColonistsComplete.Value()
	if !known || !complete || len(emergency.Facts.Colonists) == 0 {
		return PlanResult{Kind: PlanWaiting, Dependency: "colonist census", Verdict: BuildingReasonUsed}, nil
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
		return PlanResult{}, fmt.Errorf("%w: propose: observed == nil", ErrControl)
	}
	if _, err = boundary.Context(observed.Context, state.Snapshot); err != nil {
		return PlanResult{}, fmt.Errorf("%w: propose: err != nil", ErrControl)
	}
	if len(observed.Pawns) != len(ids) {
		return PlanResult{}, fmt.Errorf("%w: propose: len(observed.Pawns) != len(ids)", ErrControl)
	}
	var pawns []policy.SecureSuppliesHaulerFacts
	seen := map[string]bool{}
	for _, row := range observed.Pawns {
		if row == nil || row.Pawn == nil || seen[row.Pawn.GetId()] {
			return PlanResult{}, fmt.Errorf("%w: propose: row == nil || row.Pawn == nil || seen[row.Pawn.GetId()]", ErrControl)
		}
		seen[row.Pawn.GetId()] = true
		pawn := domain.PawnID(row.Pawn.GetId())
		facts := secureSuppliesHaulerFacts(pawn, row)
		pawns = append(pawns, facts)
	}
	snap.NoteSecureSupplies(call, snap.SecureSuppliesCall{Items: items, Pawns: pawns})
	item, pawn, ok := policy.SelectSecureSupplies(items, pawns)
	if !ok {
		return haulWait(items, pawns), nil
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
		return PlanResult{Kind: PlanWaiting, Dependency: "retry budget", Verdict: BuildingReasonExhausted}, nil
	}
	method := domain.MethodID(fmt.Sprintf("%s%d", prefix, attempt))
	id := domain.MintPlanID()
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
	proposal.commit = func(ctx context.Context) (domain.PlanID, Verdict, error) {
		if err := p.current(ctx, epoch); err != nil {
			return "", Verdict{}, err
		}
		elapsed := r.reviewer.clock.Now().Sub(started)
		if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
			return "", Verdict{}, fmt.Errorf("%w: propose: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge", ErrControl)
		}
		if _, err := p.journal.CommitGoalMethod(ctx, goal.Goal.ID, goal.Revision, method, plan); err != nil {
			return "", Verdict{}, err
		}
		return id, BuildingReasonAdmitted, nil
	}
	return PlanResult{Kind: PlanProposed, Proposal: proposal, Verdict: BuildingReasonAdmitted}, nil
}
