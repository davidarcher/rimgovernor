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
	PreviewBuilding(context.Context, domain.Action, domain.GenerationSnapshot) (bridge.BuildingPreview, bridge.Result, error)
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
	return r.step(call, epoch, newStepArbiter())
}
func (r *RoutineSecureSuppliesPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoutineSecureSuppliesResult, error) {
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
	if ok && !arbiter.tryClaim([]domain.PawnID{pawn}, "haul-item:"+item.ID) {
		ok = false
	}
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
		fallback, err := r.coveredStorageFallback(call, epoch, state, goal, reading.Projection, item, started, arbiter)
		if err != nil {
			return RoutineSecureSuppliesResult{}, err
		}
		if fallback.Reason != "" {
			return fallback, nil
		}
		fallback, err = r.supplyRoomFallback(call, epoch, state, goal, reading.Projection, started, arbiter)
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
// caller should try supplyRoomFallback next.
func (r *RoutineSecureSuppliesPlanner) coveredStorageFallback(call, epoch context.Context, state ControlState, goal store.GoalState, projection observation.ColonyProjection, item policy.UpkeepItem, started time.Time, arbiter *stepArbiter) (RoutineSecureSuppliesResult, error) {
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

// supplyRoomShellMethod names SecureSupplies' whole-room fallback method: a
// small Wall/Door enclosure built only after coveredStorageFallback finds no
// reusable roofed patch. It never places a stockpile zone itself — once the
// shell is complete and its interior has been reported roofed by the
// ordinary cell census, coveredStorageFallback's own site search naturally
// selects a patch inside it on a later step, exactly as upkeep_storage.py's
// covered_storage reuses a supply_storeroom's finished room.
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

// supplyRoomFallback ports upkeep_storage.py's supply_storeroom step: once
// covered_storage can no longer reuse existing roofing, site and build one
// small enclosed room (Wall perimeter, Door on the south wall's center) via
// upkeep_sites.enclosure_site's free-cell search. It admits at most one such
// room per goal episode, matching Python's `prior` dedup check: a completed
// or pending room-shell method already present blocks a second one rather
// than raising a duplicate-room refusal, since routine steps report "nothing
// to do" here rather than an interactive skill-blocked error. A zero-value,
// empty-Reason result means the fallback did not apply this step, and the
// caller should report its own exhaustion reason instead.
func (r *RoutineSecureSuppliesPlanner) supplyRoomFallback(call, epoch context.Context, state ControlState, goal store.GoalState, projection observation.ColonyProjection, started time.Time, arbiter *stepArbiter) (RoutineSecureSuppliesResult, error) {
	p := r.reviewer.player
	zoneAttempts := medicalAttemptCount(goal.Methods, goal.Goal.Epoch, secureSuppliesZonePrefix)
	if zoneAttempts >= maxSecureSuppliesZoneMethods {
		return RoutineSecureSuppliesResult{}, nil
	}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoutineSecureSuppliesResult{}, err
		}
		if secureSuppliesRoomShellPlan(plan.Spec) {
			return RoutineSecureSuppliesResult{}, nil
		}
	}
	held, err := p.journal.BuildingReservations(call, state.Snapshot)
	if err != nil {
		return RoutineSecureSuppliesResult{}, err
	}
	var protected []domain.Cell
	for _, h := range held {
		protected = append(protected, h.Footprint...)
	}
	wallDef, wok := animalContainmentDefinition(projection.Definitions, "Wall")
	doorDef, dok := animalContainmentDefinition(projection.Definitions, "Door")
	if !wok || !dok {
		return RoutineSecureSuppliesResult{Reason: BuildingMethodUnknown}, nil
	}
	wavail, wak := wallDef.Available.Value()
	davail, dak := doorDef.Available.Value()
	if !wak || !dak || !wavail || !davail {
		return RoutineSecureSuppliesResult{Reason: BuildingMethodUnknown}, nil
	}
	stuff, known := animalContainmentStuff(wallDef, doorDef)
	if !known {
		return RoutineSecureSuppliesResult{Reason: BuildingMethodUnknown}, nil
	}
	sites, err := policy.SupplyRoomEnclosureSites(policy.SupplyRoomEnclosureRequest{Bounds: projection.Bounds, Anchor: projection.Center, Cells: projection.Cells, Protected: protected})
	if err != nil {
		return RoutineSecureSuppliesResult{}, err
	}
	digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%d/%s", goal.Goal.ID, goal.Goal.Epoch, supplyRoomShellMethod)))
	planID := domain.PlanID(fmt.Sprintf("routine-supply-room-shell-%x", digest[:16]))
	snapshot := state.Snapshot
	snapshot.Plan = planID
	snapshot.Revision = 1
	for _, room := range sites {
		actions, previews, stock, reason, err := r.previewSupplyRoomShell(call, snapshot, room, stuff, projection)
		if err != nil {
			return RoutineSecureSuppliesResult{}, err
		}
		if reason == BuildingMethodUnknown {
			return RoutineSecureSuppliesResult{Reason: reason}, nil
		}
		if reason != "" {
			continue
		}
		plan, err := domain.NewPlan(planID, 1, actions)
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
		decision, err := p.journal.AdmitBuildingMethod(call, store.BuildingMethodRequest{Goal: goal.Goal.ID, Revision: goal.Revision, Method: supplyRoomShellMethod, Plan: plan, Current: snapshot, Tick: projection.Identity.Tick, Bounds: domain.Known(projection.Bounds), Stock: stock, Rules: r.reviewer.rules, Previews: previews, Purpose: policy.Routine})
		if err != nil {
			return RoutineSecureSuppliesResult{}, err
		}
		outcome := BuildingMethodRefused
		if decision.Admitted {
			outcome = BuildingMethodAdmitted
		}
		return RoutineSecureSuppliesResult{Reason: outcome, Plan: planID}, nil
	}
	return RoutineSecureSuppliesResult{Reason: BuildingMethodNoSpace}, nil
}

// previewSupplyRoomShell previews one candidate room's full 6x6 perimeter
// (one Door anchoring the south wall's center, Wall elsewhere), mirroring
// previewPenShell. It never commits: a rejected or infeasible cell aborts
// only this candidate.
func (r *RoutineSecureSuppliesPlanner) previewSupplyRoomShell(ctx context.Context, snapshot domain.GenerationSnapshot, room policy.Rectangle, stuff string, facts observation.ColonyProjection) ([]domain.Action, []policy.Preview, policy.StockObservation, RoutineBuildingReason, error) {
	door := domain.Cell{X: room.X + room.Width/2, Z: room.Z}
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
		if v.Action != action || !v.Snapshot.Matches(snapshot) || v.Tick != facts.Identity.Tick || !preview.Stock.Snapshot.Matches(snapshot) || preview.Stock.Tick != facts.Identity.Tick {
			return nil, nil, policy.StockObservation{}, "", ErrControl
		}
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
