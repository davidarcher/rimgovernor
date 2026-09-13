package buildingruntime

import (
	"context"
	"crypto/sha256"
	"errors"
	"fmt"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
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
	PreviewZone(context.Context, *c.Identity, bridge.ZoneTarget) (*op.PreviewReply, bridge.Result, error)
}

// maxSecureSuppliesZoneMethods bounds SecureSupplies' covered-storage fallback
// to a handful of new zones per goal episode, mirroring upkeep_storage.py's
// three-item SecureSupplies cap across covered_storage and supply_storeroom.
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
	reading, err := observation.ObserveColony(call, r.native, r.reviewer.clock, expected, r.reviewer.maxAge, true, nil)
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
	identityRef := boundary.Identity(state.Snapshot)
	emergency, _, err := r.native.ReadEmergency(call, identityRef)
	if err != nil {
		return RoutineSecureSuppliesResult{}, err
	}
	if _, err = boundary.Context(emergency.Context, state.Snapshot); err != nil || emergency.Context.GetTick() < int64(review.Tick) {
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
	if _, err = boundary.Context(observed.Context, state.Snapshot); err != nil {
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
		fallback, err := r.coveredStorageFallback(call, epoch, state, goal, reading.Projection, item, started)
		if err != nil {
			return RoutineSecureSuppliesResult{}, err
		}
		if fallback.Reason != "" {
			return fallback, nil
		}
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

// coveredStorageFallback ports upkeep_storage.py's covered_storage step: once
// ordinary hauling for the selected vulnerable item has been retried to its
// bound, propose a small allow-listed stockpile zone (native preset='nothing'
// with an explicit definition allow-list) on the nearest legal roofed 2x2
// patch instead, exactly as the Python reference's dry-run zone-create
// fallback does. It is bounded to maxSecureSuppliesZoneMethods zones per goal
// episode. A zero-value, empty-Reason result means the fallback did not apply
// this step (no zone budget left, no legal site, or a stale read) and the
// caller should report its own exhaustion reason instead.
//
// upkeep_storage.py's second-tier supply_storeroom fallback (building an
// entirely new enclosed room when no covered patch exists) is not ported by
// this slice; it remains open, tracked in docs/BACKLOG.md alongside
// MaintainStoneShell.
func (r *RoutineSecureSuppliesPlanner) coveredStorageFallback(call, epoch context.Context, state ControlState, goal store.GoalState, projection observation.ColonyProjection, item policy.UpkeepItem, started time.Time) (RoutineSecureSuppliesResult, error) {
	p := r.reviewer.player
	zoneAttempts := medicalAttemptCount(goal.Methods, goal.Goal.Epoch, secureSuppliesZonePrefix)
	if zoneAttempts >= maxSecureSuppliesZoneMethods {
		return RoutineSecureSuppliesResult{}, nil
	}
	token, known := projection.ZoneMapToken.Value()
	if !known {
		return RoutineSecureSuppliesResult{}, nil
	}
	held, err := p.journal.BuildingReservations(call, state.Snapshot)
	if err != nil {
		return RoutineSecureSuppliesResult{}, err
	}
	var protected []domain.Cell
	for _, h := range held {
		protected = append(protected, h.Footprint...)
	}
	sites, err := policy.CoveredStorageSites(policy.CoveredStorageRequest{Bounds: projection.Bounds, Anchor: projection.Center, Cells: projection.Cells, Protected: protected})
	if err != nil {
		return RoutineSecureSuppliesResult{}, err
	}
	if len(sites) == 0 {
		return RoutineSecureSuppliesResult{}, nil
	}
	site := sites[0]
	cells := make([]domain.Cell, 0, int(site.Width*site.Height))
	for x := site.X; x < site.X+site.Width; x++ {
		for z := site.Z; z < site.Z+site.Height; z++ {
			cells = append(cells, domain.Cell{X: x, Z: z})
		}
	}
	value, err := domain.NewAllowListStockpileZone(domain.ImportantPriority, []string{item.Definition}, cells)
	if err != nil {
		return RoutineSecureSuppliesResult{}, err
	}
	method := domain.MethodID(fmt.Sprintf("%s%d", secureSuppliesZonePrefix, zoneAttempts))
	digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%d/%s", goal.Goal.ID, goal.Goal.Epoch, method)))
	id := domain.PlanID(fmt.Sprintf("routine-secure-supplies-zone-%x", digest[:16]))
	snapshot := state.Snapshot
	snapshot.Plan = id
	snapshot.Revision = 1
	action, err := domain.NewZoneCreateAction(domain.ActionID(fmt.Sprintf("%s-0", id)), value)
	if err != nil {
		return RoutineSecureSuppliesResult{}, err
	}
	reply, _, err := r.native.PreviewZone(call, boundary.Identity(snapshot), bridge.ZoneTarget{Zone: value, Token: token})
	if err != nil {
		return RoutineSecureSuppliesResult{}, err
	}
	v := reply.GetEvaluated()
	if v == nil || !v.GetAccepted() {
		return RoutineSecureSuppliesResult{Reason: BuildingMethodRefused}, nil
	}
	if _, err = boundary.Context(v.Context, snapshot); err != nil || domain.Tick(v.Context.GetTick()) != projection.Identity.Tick {
		return RoutineSecureSuppliesResult{}, ErrControl
	}
	preview := policy.Preview{Action: action, Snapshot: snapshot, Tick: projection.Identity.Tick, CanPlace: domain.Known(true), SafeToPlace: domain.Known(true), MadeFromStuff: domain.Known(false), WatchCellsAccessible: domain.Known(true), Footprint: domain.Known(cells), Costs: domain.Known([]policy.Amount{})}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoutineSecureSuppliesResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutineSecureSuppliesResult{}, err
	}
	if p.session.State() != state {
		return RoutineSecureSuppliesResult{}, ErrControl
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoutineSecureSuppliesResult{}, ErrControl
	}
	decision, err := p.journal.AdmitBuildingMethod(call, store.BuildingMethodRequest{Goal: goal.Goal.ID, Revision: goal.Revision, Method: method, Plan: plan, Current: snapshot, Tick: projection.Identity.Tick, Bounds: domain.Known(projection.Bounds), Stock: policy.StockObservation{Snapshot: snapshot, Tick: projection.Identity.Tick}, Rules: r.reviewer.rules, Previews: []policy.Preview{preview}, Purpose: policy.Routine})
	if err != nil {
		return RoutineSecureSuppliesResult{}, err
	}
	if !decision.Admitted {
		return RoutineSecureSuppliesResult{Reason: BuildingMethodRefused}, nil
	}
	return RoutineSecureSuppliesResult{Reason: BuildingMethodAdmitted, Plan: id}, nil
}

func secureSuppliesHaulerFacts(pawn domain.PawnID, row *n.PawnState) policy.SecureSuppliesHaulerFacts {
	facts := policy.SecureSuppliesHaulerFacts{Pawn: pawn, Dead: boundary.FactBool(row.Dead), Downed: boundary.FactBool(row.Downed), Drafted: boundary.FactBool(row.Drafted), MentalState: boundary.FactPresence(row.MentalState, row.Issues, "mental_state")}
	if row.Job != nil && !boundary.IssueField(row.Job.Issues, "player_forced") {
		facts.PlayerForced = boundary.FactBool(row.Job.PlayerForced)
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
