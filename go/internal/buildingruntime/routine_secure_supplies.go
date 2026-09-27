package buildingruntime

import (
	"context"
	"errors"
	"fmt"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	snap "github.com/davidarcher/RimGovernor/go/internal/snapshot"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
)

// The general store keeps the general filter at Normal priority; a
// covered:<def> fallback zone keeps its one definition at Important.
func init() {
	RegisterStockpileRole(domain.GeneralRole, fixedStockpileRole(domain.GeneralFilter(), domain.NormalPriority))
	RegisterStockpileRole(domain.FoodRole, fixedStockpileRole(domain.FoodFilter(), domain.PreferredPriority))
	RegisterStockpileRole(strings.TrimSuffix(domain.CoveredRolePrefix, ":"), func(_ StockpileRoleInput, role string) (policy.StockpileRoleState, bool) {
		_, definition, _ := strings.Cut(role, ":")
		filter, err := domain.AllowOnlyFilter([]string{definition})
		if definition == "" || err != nil {
			return policy.StockpileRoleState{}, false
		}
		return policy.StockpileRoleState{Filter: filter, Priority: domain.ImportantPriority}, true
	})
}

// RoutineSecureSuppliesSource reuses the generic colony read for the vulnerable
// item census (cell/definition included) and the existing tend pawn read
// (already requests combat+work+care details) for hauler eligibility: dead/
// downed/drafted/mental state, health, existing job and the Hauling work
// setting. No new native call is introduced for this slice.
type RoutineSecureSuppliesSource interface {
	observation.ColonySource
	ReadEmergency(context.Context, *c.Identity) (bridge.EmergencyObservation, bridge.Result, error)
	ReadTendPawns(context.Context, *c.Identity, []string) (*n.ListPawnsReply, bridge.Result, error)
	PreviewZone(context.Context, *c.Identity, bridge.ZoneTarget) (*op.PreviewReply, bridge.Result, error)
	PreviewBuilding(context.Context, domain.Action, domain.GenerationSnapshot) (bridge.BuildingPreview, bridge.Result, error)
}

// maxSecureSuppliesZoneMethods bounds SecureSupplies' covered-storage fallback
// to a handful of new zones per goal episode: a three-item cap across
// covered_storage and supply_storeroom.
// Once reached, ordinary haul attempts remain the only route until a new
// episode begins.
const maxSecureSuppliesZoneMethods = 3

const secureSuppliesZonePrefix = "secure-supplies-zone-"

type RoutineSecureSuppliesPlanner struct {
	reviewer *RoutineReviewer
	native   RoutineSecureSuppliesSource
}
type RoutineSecureSuppliesResult struct {
	Reason RoutineBuildingReason
	Plan   domain.PlanID
	// NativeWorkTicks asks for a clock window while an ordered haul is
	// still on its way (haulWait).
	NativeWorkTicks uint32
}

func NewRoutineSecureSuppliesPlanner(reviewer *RoutineReviewer, native RoutineSecureSuppliesSource) (*RoutineSecureSuppliesPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, fmt.Errorf("%w: NewRoutineSecureSuppliesPlanner: reviewer == nil || native == nil", ErrControl)
	}
	return &RoutineSecureSuppliesPlanner{reviewer, native}, nil
}

// maxSecureSuppliesHaulAttempts bounds direct hauls of one item per goal
// episode before SecureSupplies tries its covered-storage fallbacks. Two is
// enough: a haul native refuses (no storage accepts the item) fails its
// method, and a second identical refusal means the map, not the hauler, is
// the problem.
const maxSecureSuppliesHaulAttempts = 2

// propose plans one SecureSupplies method without committing it: a direct
// haul while the item's haul budget lasts, then the covered-storage and
// supply-room fallbacks. Every read runs here; the returned proposal's
// commit runs the admission path the step used to run inline (#622).
func (r *RoutineSecureSuppliesPlanner) propose(call, epoch context.Context) (PlanResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return PlanResult{Kind: PlanUnsupported, Reason: BuildingMethodDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil || state.Snapshot.Native == 0 {
		return PlanResult{}, fmt.Errorf("%w: propose: !state.ObservationKnown || state.Snapshot.Validate() != nil || state.Snapshot.Native == 0", ErrControl)
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return PlanResult{}, err
	}
	if !review.Enabled || !review.Snapshot.Matches(state.Snapshot) {
		return PlanResult{Kind: PlanWaiting, Dependency: "routine review", Reason: BuildingMethodNoReview}, nil
	}
	call, recorded := recordPlannerStep(call, policy.SecureSupplies, state.Snapshot, review.Tick)
	defer recorded()
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
		return PlanResult{}, err
	}
	if !found || goal.Goal.Status != domain.GoalActive || goal.Goal.Need != domain.NeedDeficit {
		return PlanResult{Kind: PlanDemandSatisfied, Reason: BuildingMethodNoDeficit}, nil
	}
	// SecureSupplies competes for the same bounded concurrent-project capacity
	// as comfort/expansion/other priority>=3 autopilot goals; only act while
	// this review's arbitration actually selected it.
	selected := false
	for _, row := range review.Development.Rows {
		selected = selected || row.Goal == policy.SecureSupplies && row.Selected
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
		if need.Goal == policy.SecureSupplies {
			targetIDs, _ = need.Targets.Value()
		}
	}
	if len(targetIDs) == 0 {
		return PlanResult{Kind: PlanDemandSatisfied, Reason: BuildingMethodUsed}, nil
	}
	if open, err := cancelStaleHaulMethods(call, p.journal, goal, targetIDs); err != nil {
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
		return PlanResult{}, fmt.Errorf("%w: propose: err != nil || emergency.Context.GetTick() < int64(review.Tick)", ErrControl)
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
		return PlanResult{}, fmt.Errorf("%w: propose: observed == nil", ErrControl)
	}
	if _, err = boundary.Context(observed.Context, state.Snapshot); err != nil {
		return PlanResult{}, fmt.Errorf("%w: propose: err != nil", ErrControl)
	}
	if len(observed.Pawns) != len(ids) {
		return PlanResult{}, fmt.Errorf("%w: propose: len(observed.Pawns) != len(ids)", ErrControl)
	}
	preferences, loadErr := p.journal.LoadWorkPreferences(call, state.Snapshot.Plan)
	if loadErr != nil && !errors.Is(loadErr, store.ErrNotFound) {
		return PlanResult{}, loadErr
	}
	if preferences.Revision != review.WorkPreferenceRevision {
		return PlanResult{}, fmt.Errorf("%w: propose: preferences.Revision != review.WorkPreferenceRevision", ErrControl)
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
			return PlanResult{}, fmt.Errorf("%w: propose: row == nil || row.Pawn == nil || seen[row.Pawn.GetId()]", ErrControl)
		}
		seen[row.Pawn.GetId()] = true
		pawn := domain.PawnID(row.Pawn.GetId())
		facts := secureSuppliesHaulerFacts(pawn, row)
		if overridden[pawn] {
			facts.HaulingEnabled = domain.Known(false)
		}
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
	prefix := fmt.Sprintf("secure-supplies-%s-", item.ID)
	attempt, err := haulAttemptCount(call, p.journal, goal, prefix)
	if err != nil {
		return PlanResult{}, err
	}
	zonesCompleted, err := completedSecureSuppliesZones(call, p.journal, goal)
	if err != nil {
		return PlanResult{}, err
	}
	if attempt >= secureSuppliesHaulBudget(zonesCompleted) {
		fallback, err := r.coveredStorageFallback(call, epoch, state, goal, reading.Projection, item, started)
		if err != nil {
			return PlanResult{}, err
		}
		if fallback.Kind != "" {
			return fallback, nil
		}
		fallback, err = r.supplyRoomFallback(call, epoch, state, goal, reading.Projection, started)
		if err != nil {
			return PlanResult{}, err
		}
		if fallback.Kind != "" {
			return fallback, nil
		}
		// Every route for this item is spent: the direct-haul budget, the
		// covered-storage zones and the supply room.
		return PlanResult{Kind: PlanWaiting, Dependency: "retry budget", Reason: BuildingMethodExhausted}, nil
	}
	method := domain.MethodID(fmt.Sprintf("%s%d", prefix, attempt))
	id := domain.MintPlanID("routine-secure-supplies")
	action, err := domain.NewHaulAction(domain.ActionID(fmt.Sprintf("%s-0", id)), haul)
	if err != nil {
		return PlanResult{}, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return PlanResult{}, err
	}
	proposal := r.proposal(call, id, goal, state, review.Tick, []domain.Action{action}, ResourceClaims{Pawns: []domain.PawnID{pawn}, Entities: []string{"haul-item:" + item.ID}})
	proposal.commit = func(ctx context.Context) (domain.PlanID, RoutineBuildingReason, error) {
		if err := p.current(ctx, epoch); err != nil {
			return "", "", err
		}
		elapsed := r.reviewer.clock.Now().Sub(started)
		if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
			return "", "", fmt.Errorf("%w: propose: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge", ErrControl)
		}
		if _, err := p.journal.CommitGoalMethod(ctx, goal.Goal.ID, goal.Revision, method, plan); err != nil {
			return "", "", err
		}
		return id, BuildingMethodAdmitted, nil
	}
	return PlanResult{Kind: PlanProposed, Proposal: proposal, Reason: BuildingMethodAdmitted}, nil
}

// proposal is the planner's Proposal for plan id under goal: the wave's
// foothold priority, the goal's own urgency, and the claims given.
func (r *RoutineSecureSuppliesPlanner) proposal(ctx context.Context, id domain.PlanID, goal store.GoalState, state ControlState, tick domain.Tick, actions []domain.Action, claims ResourceClaims) *Proposal {
	return &Proposal{ID: "secureSupplies/" + string(id), Planner: "secureSupplies", Goal: goal.Goal.ID, Priority: plannerFoothold, Urgency: goal.Goal.Priority, Snapshot: state.Snapshot, Facts: factsBuilding, Claims: claims, ValidTick: tick, Actions: actions}
}

// previewClaims sums the previews' known costs into quantity claims.
func previewClaims(previews []policy.Preview) ResourceClaims {
	totals := map[policy.Resource]int64{}
	var order []policy.Resource
	for _, preview := range previews {
		costs, known := preview.Costs.Value()
		if !known {
			continue
		}
		for _, cost := range costs {
			if _, seen := totals[cost.Resource]; !seen {
				order = append(order, cost.Resource)
			}
			totals[cost.Resource] += cost.Count
		}
	}
	claims := ResourceClaims{}
	for _, resource := range order {
		claims.Quantities = append(claims.Quantities, policy.Amount{Resource: resource, Count: totals[resource]})
	}
	return claims
}

// admitBuilding is the fallbacks' commit: the shared building admission
// path, refused when it declines the method.
func (r *RoutineSecureSuppliesPlanner) admitBuilding(epoch context.Context, state ControlState, started time.Time, request store.BuildingMethodRequest) func(context.Context) (domain.PlanID, RoutineBuildingReason, error) {
	p := r.reviewer.player
	return func(ctx context.Context) (domain.PlanID, RoutineBuildingReason, error) {
		if err := p.current(ctx, epoch); err != nil {
			return "", "", err
		}
		if p.session.State() != state {
			return "", "", fmt.Errorf("%w: admitBuilding: p.session.State() != state", ErrControl)
		}
		elapsed := r.reviewer.clock.Now().Sub(started)
		if elapsed < 0 || elapsed > r.reviewer.maxAge {
			return "", "", fmt.Errorf("%w: admitBuilding: elapsed < 0 || elapsed > r.reviewer.maxAge", ErrControl)
		}
		decision, err := p.journal.AdmitBuildingMethod(ctx, request)
		if err != nil {
			return "", "", err
		}
		if !decision.Admitted {
			return request.Plan.ID(), BuildingMethodRefused, nil
		}
		return request.Plan.ID(), BuildingMethodAdmitted, nil
	}
}

// secureSuppliesHaulBudget is how many direct hauls of one item the goal
// episode may issue: the base bound, plus another round for every
// covered-storage zone the fallback has completed. Before the zone exists
// native refuses the haul ("no empty, accessible spot"); the zone is what
// makes a retry worth spending, so each completed zone earns one.
func secureSuppliesHaulBudget(zonesCompleted int) int {
	if zonesCompleted < 0 {
		zonesCompleted = 0
	}
	return maxSecureSuppliesHaulAttempts * (1 + zonesCompleted)
}

// completedSecureSuppliesZones counts the goal episode's covered-storage zone
// methods whose every action completed. Pending, cancelled or unsuccessful
// zones earn no haul retries.
func completedSecureSuppliesZones(ctx context.Context, journal *store.Store, goal store.GoalState) (int, error) {
	history, err := journal.LoadGoalMethods(ctx, goal.Goal.ID, goal.Goal.Epoch)
	if err != nil {
		return 0, err
	}
	count := 0
	for _, m := range history {
		if m.Epoch != goal.Goal.Epoch || !strings.HasPrefix(string(m.Method), secureSuppliesZonePrefix) {
			continue
		}
		plan, err := journal.LoadPlan(ctx, m.Plan)
		if err != nil {
			return 0, err
		}
		done := len(plan.Progress) > 0
		for _, progress := range plan.Progress {
			if progress.View().Stage != domain.Completed {
				done = false
			}
		}
		if done {
			count++
		}
	}
	return count, nil
}

// maxSecureSuppliesZoneSites bounds how many census-legal 2x2 patches one
// covered_storage step previews before giving the site search up for this
// step: native refuses a patch the census misjudged (a thing the census does
// not count, a roof that fell since the read), and the next nearest patch is
// the answer, not the same one again next step (#216, #223).
const maxSecureSuppliesZoneSites = 4

// coveredStorageFallback is the covered_storage step: once
// ordinary hauling for the selected vulnerable item has been retried to its
// bound, propose a small allow-listed stockpile zone (native preset='nothing'
// with an explicit definition allow-list) on the nearest legal roofed 2x2
// patch instead. It is bounded to maxSecureSuppliesZoneMethods zones per goal
// episode. A zero-value, empty-Reason result means the fallback did not apply
// this step (no zone budget left, no legal site, every previewed site refused
// natively, or a stale read) and the caller should try supplyRoomFallback
// next.
func (r *RoutineSecureSuppliesPlanner) coveredStorageFallback(call, epoch context.Context, state ControlState, goal store.GoalState, projection observation.ColonyProjection, item policy.UpkeepItem, started time.Time) (PlanResult, error) {
	p := r.reviewer.player
	zoneAttempts, err := haulAttemptCount(call, p.journal, goal, secureSuppliesZonePrefix)
	if err != nil {
		return PlanResult{}, err
	}
	if zoneAttempts >= maxSecureSuppliesZoneMethods {
		return PlanResult{}, nil
	}
	token, known := projection.ZoneMapToken.Value()
	if !known {
		return PlanResult{}, nil
	}
	if general, err := r.generalStore(call, epoch, state, goal, projection, token, started); err != nil || general.Kind != "" {
		return general, err
	}
	held, err := p.journal.BuildingReservations(call, state.Snapshot)
	if err != nil {
		return PlanResult{}, err
	}
	var protected []domain.Cell
	for _, h := range held {
		protected = append(protected, h.Footprint...)
	}
	storage := policy.CoveredStorageRequest{Bounds: projection.Bounds, Anchor: layoutAnchor(projection, policy.DistrictStorage), Cells: projection.Cells, Protected: layoutProtected(projection, protected)}
	snap.NoteCoveredStorage(call, storage)
	sites, err := policy.CoveredStorageSites(storage)
	if err != nil {
		return PlanResult{}, err
	}
	if len(sites) == 0 {
		return PlanResult{}, nil
	}
	method := domain.MethodID(fmt.Sprintf("%s%d", secureSuppliesZonePrefix, zoneAttempts))
	id := domain.MintPlanID("routine-secure-supplies-zone")
	snapshot := state.Snapshot
	snapshot.Plan = id
	snapshot.Revision = 1
	value, cells, v, err := previewCoveredStorageSites(call, r.native, boundary.Identity(snapshot), token, item.Definition, sites, goal.Goal.ID)
	if err != nil {
		return PlanResult{}, err
	}
	if v == nil {
		return PlanResult{}, nil
	}
	if _, err = boundary.Context(v.Context, snapshot); err != nil || domain.Tick(v.Context.GetTick()) < projection.Identity.Tick {
		return PlanResult{}, fmt.Errorf("%w: coveredStorageFallback: err != nil || domain.Tick(v.Context.GetTick()) < projection.Identity.Tick", ErrControl)
	}
	action, err := domain.NewZoneCreateAction(domain.ActionID(fmt.Sprintf("%s-0", id)), value)
	if err != nil {
		return PlanResult{}, err
	}
	preview := policy.Preview{Action: action, Snapshot: snapshot, Tick: projection.Identity.Tick, CanPlace: domain.Known(true), SafeToPlace: domain.Known(true), MadeFromStuff: domain.Known(false), WatchCellsAccessible: domain.Known(true), Footprint: domain.Known(cells), Costs: domain.Known([]policy.Amount{})}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return PlanResult{}, err
	}
	proposal := r.proposal(call, id, goal, state, projection.Identity.Tick, []domain.Action{action}, previewClaims([]policy.Preview{preview}))
	proposal.commit = r.admitBuilding(epoch, state, started, store.BuildingMethodRequest{Goal: goal.Goal.ID, Revision: goal.Revision, Method: method, Plan: plan, Current: snapshot, Tick: projection.Identity.Tick, Bounds: domain.Known(projection.Bounds), Stock: policy.StockObservation{Snapshot: snapshot, Tick: projection.Identity.Tick}, Previews: []policy.Preview{preview}, Purpose: policy.Routine})
	return PlanResult{Kind: PlanProposed, Proposal: proposal, Reason: BuildingMethodAdmitted}, nil
}

// generalStoreMethod zones a completed storeroom shell's interior as the
// colony's Normal-priority general store (#720). It shares the zone prefix,
// so it spends one of the episode's zone methods.
const generalStoreMethod = domain.MethodID(secureSuppliesZonePrefix + "general")

// generalStore proposes the general store once this episode's storeroom
// shell has completed: vanilla then hauls the vulnerable item (and every
// other non-perishable) indoors, and the Important working stockpiles at the
// benches and kitchen pull from it. A zero result means it does not apply
// (no completed shell, already proposed, or native refused the interior)
// and the 2x2 covered-storage search runs instead.
func (r *RoutineSecureSuppliesPlanner) generalStore(call, epoch context.Context, state ControlState, goal store.GoalState, projection observation.ColonyProjection, token string, started time.Time) (PlanResult, error) {
	p := r.reviewer.player
	var cells []domain.Cell
	for _, method := range goal.Methods {
		if method.Method == generalStoreMethod {
			return PlanResult{}, nil
		}
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return PlanResult{}, err
		}
		if !secureSuppliesRoomShellPlan(plan.Spec) || len(plan.Progress) == 0 {
			continue
		}
		done := true
		for _, progress := range plan.Progress {
			done = done && progress.View().Stage == domain.Completed
		}
		if done {
			cells = shellInterior(plan.Spec)
		}
	}
	if len(cells) == 0 {
		return PlanResult{}, nil
	}
	value, err := domain.NewFilteredStockpileZone(domain.GeneralFilter(), domain.NormalPriority, cells)
	if err == nil {
		value, err = value.WithRole(domain.GeneralRole)
	}
	if err != nil {
		return PlanResult{}, nil
	}
	if value, err = value.WithRole("general"); err != nil {
		return PlanResult{}, err
	}
	id := domain.MintPlanID("routine-general-store")
	snapshot := state.Snapshot
	snapshot.Plan = id
	snapshot.Revision = 1
	reply, _, err := r.native.PreviewZone(call, boundary.Identity(snapshot), bridge.ZoneTarget{Zone: value, Token: token})
	var refused *bridge.NativeFailure
	if errors.As(err, &refused) {
		clockSchedulerLog("%s: general store refused code=%v detail=%q", goal.Goal.ID, refused.Value.GetCode(), refused.Value.GetDetail())
		return PlanResult{}, nil
	}
	if err != nil {
		return PlanResult{}, err
	}
	v := reply.GetEvaluated()
	if v == nil || !v.GetAccepted() {
		return PlanResult{}, nil
	}
	if _, err = boundary.Context(v.Context, snapshot); err != nil || domain.Tick(v.Context.GetTick()) < projection.Identity.Tick {
		return PlanResult{}, fmt.Errorf("%w: generalStore: err != nil || domain.Tick(v.Context.GetTick()) < projection.Identity.Tick", ErrControl)
	}
	action, err := domain.NewZoneCreateAction(domain.ActionID(fmt.Sprintf("%s-0", id)), value)
	if err != nil {
		return PlanResult{}, err
	}
	preview := policy.Preview{Action: action, Snapshot: snapshot, Tick: projection.Identity.Tick, CanPlace: domain.Known(true), SafeToPlace: domain.Known(true), MadeFromStuff: domain.Known(false), WatchCellsAccessible: domain.Known(true), Footprint: domain.Known(cells), Costs: domain.Known([]policy.Amount{})}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return PlanResult{}, err
	}
	proposal := r.proposal(call, id, goal, state, projection.Identity.Tick, []domain.Action{action}, previewClaims([]policy.Preview{preview}))
	proposal.commit = r.admitBuilding(epoch, state, started, store.BuildingMethodRequest{Goal: goal.Goal.ID, Revision: goal.Revision, Method: generalStoreMethod, Plan: plan, Current: snapshot, Tick: projection.Identity.Tick, Bounds: domain.Known(projection.Bounds), Stock: policy.StockObservation{Snapshot: snapshot, Tick: projection.Identity.Tick}, Previews: []policy.Preview{preview}, Purpose: policy.Routine})
	return PlanResult{Kind: PlanProposed, Proposal: proposal, Reason: BuildingMethodAdmitted}, nil
}

// shellInterior is the cells strictly inside a room shell's Wall/Door
// perimeter: the bounding box of its building cells less its border.
func shellInterior(spec domain.PlanSpec) []domain.Cell {
	first := true
	var lo, hi domain.Cell
	for _, action := range spec.Actions() {
		b, ok := action.Building()
		if !ok || b.Definition() != "Wall" && b.Definition() != "Door" {
			continue
		}
		c := b.Cell()
		if first {
			lo, hi, first = c, c, false
		}
		lo.X, lo.Z = min(lo.X, c.X), min(lo.Z, c.Z)
		hi.X, hi.Z = max(hi.X, c.X), max(hi.Z, c.Z)
	}
	var cells []domain.Cell
	for x := lo.X + 1; x < hi.X; x++ {
		for z := lo.Z + 1; z < hi.Z; z++ {
			cells = append(cells, domain.Cell{X: x, Z: z})
		}
	}
	return cells
}

// zonePreviewer is the one native read previewCoveredStorageSites needs.
type zonePreviewer interface {
	PreviewZone(context.Context, *c.Identity, bridge.ZoneTarget) (*op.PreviewReply, bridge.Result, error)
}

// previewCoveredStorageSites previews the census-legal patches nearest the
// colony in order, at most maxSecureSuppliesZoneSites of them, and returns
// the first allow-list stockpile zone native accepts with its cells and
// evaluation. A native refusal of one patch (bridge.NativeFailure) is that
// patch's verdict at this tick, not a failed read: it is logged and the next
// patch is tried. A nil evaluation with a nil error means every previewed
// patch was refused; any other error is the read's own failure.
func previewCoveredStorageSites(ctx context.Context, native zonePreviewer, identity *c.Identity, token, definition string, sites []policy.Rectangle, goal domain.GoalID) (domain.ZoneCreate, []domain.Cell, *op.PreviewEvaluation, error) {
	for i, site := range sites {
		if i >= maxSecureSuppliesZoneSites {
			break
		}
		cells := make([]domain.Cell, 0, int(site.Width*site.Height))
		for x := site.X; x < site.X+site.Width; x++ {
			for z := site.Z; z < site.Z+site.Height; z++ {
				cells = append(cells, domain.Cell{X: x, Z: z})
			}
		}
		value, err := allowListZone(domain.ImportantPriority, []string{definition}, cells)
		if err == nil {
			value, err = value.WithRole(domain.CoveredRolePrefix + definition)
		}
		if err != nil {
			return domain.ZoneCreate{}, nil, nil, err
		}
		reply, _, err := native.PreviewZone(ctx, identity, bridge.ZoneTarget{Zone: value, Token: token})
		var refused *bridge.NativeFailure
		if errors.As(err, &refused) {
			clockSchedulerLog("%s: covered storage site (%d,%d) refused code=%v detail=%q", goal, site.X, site.Z, refused.Value.GetCode(), refused.Value.GetDetail())
			continue
		}
		if err != nil {
			return domain.ZoneCreate{}, nil, nil, err
		}
		v := reply.GetEvaluated()
		if v != nil && v.GetAccepted() {
			return value, cells, v, nil
		}
		// Native evaluates refused ground as Accepted false (#223).
		clockSchedulerLog("%s: covered storage site (%d,%d) refused by the native evaluation", goal, site.X, site.Z)
	}
	return domain.ZoneCreate{}, nil, nil, nil
}

// supplyRoomShellMethod names SecureSupplies' whole-room fallback method: a
// small Wall/Door enclosure built only after coveredStorageFallback finds no
// reusable roofed patch. It never places a stockpile zone itself — once the
// shell is complete and its interior has been reported roofed by the
// ordinary cell census, coveredStorageFallback's own site search naturally
// selects a patch inside it on a later step, reusing the finished room.
const supplyRoomShellMethod domain.MethodID = "supply-room-shell"

// secureSuppliesRoomShellPlan reports whether a plan spec already places the
// SecureSupplies room shell's Wall/Door perimeter, mirroring
// animalContainmentPlanKindOf's recovery of a prior pen shell from its own
// actions rather than a naming convention.
func secureSuppliesRoomShellPlan(spec domain.PlanSpec) bool {
	for _, action := range spec.Actions() {
		b, ok := action.Building()
		if !ok {
			continue
		}
		if b.Definition() == "Wall" || b.Definition() == "Door" {
			return true
		}
	}
	return false
}

// supplyRoomFallback is the supply_storeroom step: once
// covered_storage can no longer reuse existing roofing, site and build one
// small enclosed room (Wall perimeter, Door on the south wall's center) via
// upkeep_sites.enclosure_site's free-cell search. It admits at most one such
// room per goal episode (a `prior` dedup check): a completed
// or pending room-shell method already present blocks a second one rather
// than raising a duplicate-room refusal, since routine steps report "nothing
// to do" here rather than an interactive skill-blocked error. A zero-value,
// empty-Reason result means the fallback did not apply this step, and the
// caller should report its own exhaustion reason instead.
func (r *RoutineSecureSuppliesPlanner) supplyRoomFallback(call, epoch context.Context, state ControlState, goal store.GoalState, projection observation.ColonyProjection, started time.Time) (PlanResult, error) {
	p := r.reviewer.player
	zoneAttempts, err := haulAttemptCount(call, p.journal, goal, secureSuppliesZonePrefix)
	if err != nil {
		return PlanResult{}, err
	}
	if zoneAttempts >= maxSecureSuppliesZoneMethods {
		return PlanResult{}, nil
	}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return PlanResult{}, err
		}
		if secureSuppliesRoomShellPlan(plan.Spec) {
			return PlanResult{}, nil
		}
	}
	held, err := p.journal.BuildingReservations(call, state.Snapshot)
	if err != nil {
		return PlanResult{}, err
	}
	var protected []domain.Cell
	for _, h := range held {
		protected = append(protected, h.Footprint...)
	}
	wallDef, wok := animalContainmentDefinition(projection.Definitions, "Wall")
	doorDef, dok := animalContainmentDefinition(projection.Definitions, "Door")
	if !wok || !dok {
		return PlanResult{Kind: PlanWaiting, Dependency: "wall and door definitions", Reason: BuildingMethodUnknown}, nil
	}
	wavail, wak := wallDef.Available.Value()
	davail, dak := doorDef.Available.Value()
	if !wak || !dak || !wavail || !davail {
		return PlanResult{Kind: PlanWaiting, Dependency: "wall and door definitions", Reason: BuildingMethodUnknown}, nil
	}
	stuff, known := animalContainmentStuff(wallDef, doorDef)
	if !known {
		return PlanResult{Kind: PlanWaiting, Dependency: "wall and door definitions", Reason: BuildingMethodUnknown}, nil
	}
	sites, err := policy.SupplyRoomEnclosureSites(policy.SupplyRoomEnclosureRequest{Bounds: projection.Bounds, Anchor: layoutAnchor(projection, policy.DistrictStorage), Cells: projection.Cells, Protected: layoutProtected(projection, protected)})
	if err != nil {
		return PlanResult{}, err
	}
	if sites, err = benchCentralSites(call, r.native, boundary.Identity(state.Snapshot), projection.Cells, sites); err != nil {
		return PlanResult{}, err
	}
	planID := domain.MintPlanID("routine-supply-room-shell")
	snapshot := state.Snapshot
	snapshot.Plan = planID
	snapshot.Revision = 1
	// The layout plan's storerooms come first, built to their exact
	// rectangle and door (#787); the 6x6 search sites follow with the door
	// on the south wall's centre.
	type supplyRoomSite struct {
		room policy.Rectangle
		door domain.Cell
	}
	var candidates []supplyRoomSite
	reserved := make(map[domain.Cell]bool, len(protected))
	for _, c := range protected {
		reserved[c] = true
	}
planned:
	for _, shell := range plannedShells(projection, policy.RoomRoleStoreroom) {
		for _, c := range shell.Cells() {
			if reserved[c] {
				continue planned
			}
		}
		b := shell.Bounds()
		candidates = append(candidates, supplyRoomSite{policy.Rectangle{X: b.X, Z: b.Z, Width: b.Width, Height: b.Height}, shell.Door()})
	}
	for _, room := range sites {
		candidates = append(candidates, supplyRoomSite{room, domain.Cell{X: room.X + room.Width/2, Z: room.Z}})
	}
	for _, site := range candidates {
		actions, previews, stock, reason, err := r.previewSupplyRoomShell(call, snapshot, site.room, site.door, stuff, projection)
		if err != nil {
			return PlanResult{}, err
		}
		if reason == BuildingMethodUnknown {
			return PlanResult{Kind: PlanWaiting, Dependency: "shell preview", Reason: reason}, nil
		}
		if reason != "" {
			continue
		}
		plan, err := domain.NewPlan(planID, 1, actions)
		if err != nil {
			return PlanResult{}, err
		}
		proposal := r.proposal(call, planID, goal, state, projection.Identity.Tick, actions, previewClaims(previews))
		proposal.commit = r.admitBuilding(epoch, state, started, store.BuildingMethodRequest{Goal: goal.Goal.ID, Revision: goal.Revision, Method: supplyRoomShellMethod, Plan: plan, Current: snapshot, Tick: projection.Identity.Tick, Bounds: domain.Known(projection.Bounds), Stock: stock, Previews: previews, Purpose: policy.Routine})
		return PlanResult{Kind: PlanProposed, Proposal: proposal, Reason: BuildingMethodAdmitted}, nil
	}
	return PlanResult{Kind: PlanWaiting, Dependency: "supply room site", Reason: BuildingMethodNoSpace}, nil
}

// previewSupplyRoomShell previews one candidate room's full perimeter (a
// Door on door, Wall elsewhere), mirroring
// previewPenShell. It never commits: a rejected or infeasible cell aborts
// only this candidate.
func (r *RoutineSecureSuppliesPlanner) previewSupplyRoomShell(ctx context.Context, snapshot domain.GenerationSnapshot, room policy.Rectangle, door domain.Cell, stuff string, facts observation.ColonyProjection) ([]domain.Action, []policy.Preview, policy.StockObservation, RoutineBuildingReason, error) {
	perimeter := []domain.Cell{door}
	for x := room.X; x < room.X+room.Width; x++ {
		for z := room.Z; z < room.Z+room.Height; z++ {
			cell := domain.Cell{X: x, Z: z}
			if cell != door && (x == room.X || x == room.X+room.Width-1 || z == room.Z || z == room.Z+room.Height-1) {
				perimeter = append(perimeter, cell)
			}
		}
	}
	stock := policy.StockObservation{Snapshot: snapshot, Tick: facts.Identity.Tick}
	var actions []domain.Action
	var previews []policy.Preview
	for i, cell := range perimeter {
		definition := "Wall"
		if i == 0 {
			definition = "Door"
		}
		building, err := domain.NewBuilding(definition, cell, domain.North, stuff)
		if err != nil {
			return nil, nil, policy.StockObservation{}, "", err
		}
		action, err := domain.NewBuildingAction(domain.ActionID(fmt.Sprintf("%s-%d", snapshot.Plan, i)), building)
		if err != nil {
			return nil, nil, policy.StockObservation{}, "", err
		}
		preview, _, err := r.native.PreviewBuilding(ctx, action, snapshot)
		if err != nil {
			return nil, nil, policy.StockObservation{}, "", err
		}
		v := preview.Preview
		made, madeKnown := v.MadeFromStuff.Value()
		if !madeKnown || made != (stuff != "") {
			return nil, nil, policy.StockObservation{}, BuildingMethodUnknown, nil
		}
		footprint, fk := v.Footprint.Value()
		can, ck := v.CanPlace.Value()
		safe, sk := v.SafeToPlace.Value()
		if !fk || len(footprint) != 1 || footprint[0] != cell || !ck || !can || !sk || !safe {
			return nil, nil, policy.StockObservation{}, BuildingMethodNoSpace, nil
		}
		if err := mergeRoutineStock(&stock, preview.Stock, i == 0); err != nil {
			return nil, nil, policy.StockObservation{}, "", err
		}
		actions = append(actions, action)
		previews = append(previews, v)
	}
	return actions, previews, stock, "", nil
}

func secureSuppliesHaulerFacts(pawn domain.PawnID, row *n.PawnState) policy.SecureSuppliesHaulerFacts {
	facts := policy.SecureSuppliesHaulerFacts{Pawn: pawn, Dead: boundary.FactBool(row.Dead), Downed: boundary.FactBool(row.Downed), Drafted: boundary.FactBool(row.Drafted), MentalState: boundary.FactPresence(row.MentalState, row.Issues, "mental_state")}
	if row.Job != nil && !boundary.IssueField(row.Job.Issues, "player_forced") {
		facts.PlayerForced = boundary.FactBool(row.Job.PlayerForced)
	}
	if row.Job != nil && row.Job.GetDefName() == "HaulToCell" {
		facts.Hauling = row.Job.GetTargetA().GetEntity().GetId()
	}
	if health := row.Health; health != nil && !boundary.IssueField(health.Issues, "health") {
		facts.NeedsTend, facts.Bleeding = boundary.FactBool(health.NeedsTend), boundary.FactBool(health.Bleeding)
	}
	if settings := row.Settings; settings != nil && !boundary.IssueField(settings.Issues, "work") {
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
