package buildingruntime

import (
	"context"
	"fmt"
	"slices"
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
)

// The warehouse keeps the indoor-only filter at Low priority so the
// workstation stockpiles draw items first; the opening outdoor store keeps
// the non-perishables at Normal until the warehouse replaces it.
func init() {
	RegisterStockpileRole(domain.GeneralRole, fixedStockpileRole(domain.GeneralFilter(), domain.LowPriority))
	RegisterStockpileRole(domain.OpeningGeneralRole, fixedStockpileRole(domain.OpeningStoreFilter(), domain.NormalPriority))
	RegisterStockpileRole(domain.FoodRole, fixedStockpileRole(domain.FoodFilter(), domain.PreferredPriority))
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
	PreviewBuilding(context.Context, domain.Action, domain.GenerationSnapshot) (bridge.BuildingPreview, bridge.Result, error)
}

type RoutineSecureSuppliesPlanner struct {
	reviewer *RoutineReviewer
	native   RoutineSecureSuppliesSource
}
type RoutineSecureSuppliesResult struct {
	Verdict
	Plan domain.PlanID
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
// episode before SecureSupplies requests the storage room. Two is
// enough: a haul native refuses (no storage accepts the item) fails its
// method, and a second identical refusal means the map, not the hauler, is
// the problem.
const maxSecureSuppliesHaulAttempts = 2

// propose plans one SecureSupplies method without committing it: a direct
// haul while the item's haul budget lasts, then the storage room request.
// Every read runs here; the returned proposal's commit runs the admission
// path the step used to run inline (#622).
func (r *RoutineSecureSuppliesPlanner) propose(call, epoch context.Context) (PlanResult, error) {
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
	call, recorded := recordPlannerStep(call, policy.SecureSupplies, state.Snapshot, review.Tick)
	defer recorded()
	goal, workable, err := p.journal.Workable(call, review, policy.SecureSupplies)
	if err != nil {
		return PlanResult{}, err
	}
	if !workable {
		return PlanResult{Kind: PlanDemandSatisfied, Verdict: BuildingReasonNoDeficit}, nil
	}
	// SecureSupplies competes for the same bounded concurrent-project capacity
	// as comfort/expansion/other priority>=3 autopilot goals; only act while
	// this review's arbitration actually selected it.
	selected := false
	for _, row := range review.Development.Rows {
		selected = selected || row.Goal == policy.SecureSupplies && row.Selected
	}
	if !selected {
		return PlanResult{Kind: PlanWaiting, Dependency: "development slot", Verdict: awaitingSlot(string(policy.SecureSupplies))}, nil
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
	prefix := fmt.Sprintf("secure-supplies-%s-", item.ID)
	attempt, err := haulAttemptCount(call, p.journal, goal, prefix)
	if err != nil {
		return PlanResult{}, err
	}
	if attempt >= maxSecureSuppliesHaulAttempts {
		fallback, err := r.supplyRoomFallback(call, epoch, state, goal, review, reading, started)
		if err != nil {
			return PlanResult{}, err
		}
		if fallback.Kind != "" {
			return fallback, nil
		}
		// Every route for this item is spent: the direct-haul budget and the
		// storage room request. The zones are the storage planner's.
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
	proposal := r.proposal(call, id, goal, state, review.Tick, []domain.Action{action}, ResourceClaims{Pawns: []domain.PawnID{pawn}, Entities: []string{"haul-item:" + item.ID}})
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
func (r *RoutineSecureSuppliesPlanner) admitBuilding(epoch context.Context, state ControlState, started time.Time, request store.BuildingMethodRequest) func(context.Context) (domain.PlanID, Verdict, error) {
	p := r.reviewer.player
	return func(ctx context.Context) (domain.PlanID, Verdict, error) {
		if err := p.current(ctx, epoch); err != nil {
			return "", Verdict{}, err
		}
		if p.session.State() != state {
			return "", Verdict{}, fmt.Errorf("%w: admitBuilding: p.session.State() != state", ErrControl)
		}
		elapsed := r.reviewer.clock.Now().Sub(started)
		if elapsed < 0 || elapsed > r.reviewer.maxAge {
			return "", Verdict{}, fmt.Errorf("%w: admitBuilding: elapsed < 0 || elapsed > r.reviewer.maxAge", ErrControl)
		}
		decision, err := admitMethod(ctx, p.journal, request)
		if err != nil {
			return "", Verdict{}, err
		}
		if !decision.Admitted {
			return request.Plan.ID(), BuildingReasonRefused, nil
		}
		return request.Plan.ID(), BuildingReasonAdmitted, nil
	}
}

// supplyRoomShellMethod names SecureSupplies' room request: the layout
// plan's storage room, raised when no shell stands on the slot. It never
// places a stockpile zone: once the room stands the storage planner sites
// the warehouse in it (policy.PlanStorage).
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

// supplyRoomFallback requests the room an unstored item needs, once its
// direct hauls are spent. SecureSupplies never raises an ad-hoc shed
// (#1186); it stores in the layout plan's storage room. When a shell
// already stands on that slot (#1177) the warehouse is the storage
// planner's to site and grow, and the step does not apply. Otherwise it
// raises the storage room itself, its exact ring and its door onto the
// spine, at most once per goal episode. A zero-value, empty-Reason result
// means the step did not apply, and the caller reports its own exhaustion
// reason instead.
func (r *RoutineSecureSuppliesPlanner) supplyRoomFallback(call, epoch context.Context, state ControlState, goal store.GoalState, review store.RoutineReview, reading observation.ColonyReading, started time.Time) (PlanResult, error) {
	p := r.reviewer.player
	projection := reading.Projection
	storage, planned := plannedStorageRoom(projection)
	if !planned {
		return PlanResult{Kind: PlanWaiting, Dependency: "layout plan storage room", Verdict: fieldUnavailable("storage_room_plan")}, nil
	}
	perimeter := storageRoomRing(storage, projection.Cells)
	if perimeter == nil {
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
	reserved := map[domain.Cell]bool{}
	for _, h := range held {
		for _, c := range h.Footprint {
			reserved[c] = true
		}
	}
	for _, c := range storage.Cells() {
		if reserved[c] {
			return PlanResult{Kind: PlanWaiting, Dependency: "storage room site", Verdict: BuildingReasonNoSpace}, nil
		}
	}
	wallDef, wok := animalContainmentDefinition(projection.Definitions, "Wall")
	doorDef, dok := animalContainmentDefinition(projection.Definitions, "Door")
	if !wok || !dok {
		return PlanResult{Kind: PlanWaiting, Dependency: "wall and door definitions", Verdict: fieldUnavailable("wall_door_definitions")}, nil
	}
	wavail, wak := wallDef.Available.Value()
	davail, dak := doorDef.Available.Value()
	if !wak || !dak || !wavail || !davail {
		return PlanResult{Kind: PlanWaiting, Dependency: "wall and door definitions", Verdict: fieldUnavailable("wall_door_definitions")}, nil
	}
	stuff, known := animalContainmentStuff(wallDef, doorDef)
	if !known {
		return PlanResult{Kind: PlanWaiting, Dependency: "wall and door definitions", Verdict: fieldUnavailable("wall_door_stuff")}, nil
	}
	// Rock under the room is mined first through the shared rock step
	// (#1754): the interior and the door are dug, then the ring is built;
	// a ring cell on natural rock is left as the wall it already is. The
	// dig is admitted here with the ring waiting on it.
	rock := policy.RockStep(supplyRoomRoleCells(storage), projection.Cells)
	if len(rock.Left) > 0 {
		left := map[domain.Cell]bool{}
		for _, cell := range rock.Left {
			left[cell] = true
		}
		perimeter = slices.DeleteFunc(slices.Clone(perimeter), func(cell domain.Cell) bool { return left[cell] })
	}
	if len(rock.Dig) > 0 {
		source, ok := r.native.(RoutineBuildingSource)
		if !ok {
			return PlanResult{Kind: PlanWaiting, Dependency: "storage room dig", Verdict: fieldUnavailable("excavation_source")}, nil
		}
		ring := make([]domain.Building, 0, len(perimeter))
		for i, cell := range perimeter {
			definition := "Wall"
			if i == 0 {
				definition = "Door"
			}
			building, err := domain.NewBuilding(definition, cell, domain.North, stuff)
			if err != nil {
				return PlanResult{}, err
			}
			ring = append(ring, building)
		}
		dig := &RoutineBuildingPlanner{reviewer: r.reviewer, native: source, goal: policy.SecureSupplies}
		if excavation, ok := r.native.(RoutineExcavationSource); ok {
			dig.excavation = excavation
		}
		check := func() error {
			if err := p.current(call, epoch); err != nil {
				return err
			}
			if p.session.State() != state {
				return fmt.Errorf("%w: supplyRoomFallback: session state changed", ErrControl)
			}
			return nil
		}
		step := excavationStep{state: state, review: review, goal: goal, facts: projection, read: reading}
		result, handled, err := dig.admitRockStep(call, epoch, step, supplyRoomRoleCells(storage), storage.Door(), "plan-dig-supply-room", ring, check)
		if err != nil {
			return PlanResult{}, err
		}
		if handled {
			return PlanResult{Kind: PlanWaiting, Dependency: "storage room dig", Verdict: result.Verdict}, nil
		}
		return PlanResult{Kind: PlanWaiting, Dependency: "storage room dig", Verdict: BuildingReasonNoSpace}, nil
	}
	planID := domain.MintPlanID()
	snapshot := state.Snapshot
	snapshot.Plan = planID
	snapshot.Revision = 1
	actions, previews, stock, reason, err := r.previewSupplyRoomShell(call, snapshot, perimeter, stuff, projection)
	if err != nil {
		return PlanResult{}, err
	}
	if reason.Is(RefusalFieldUnavailable) {
		return PlanResult{Kind: PlanWaiting, Dependency: "shell preview", Verdict: reason}, nil
	}
	if !reason.IsZero() {
		return PlanResult{Kind: PlanWaiting, Dependency: "storage room site", Verdict: BuildingReasonNoSpace}, nil
	}
	plan, err := domain.NewPlan(planID, 1, actions)
	if err != nil {
		return PlanResult{}, err
	}
	proposal := r.proposal(call, planID, goal, state, projection.Identity.Tick, actions, previewClaims(previews))
	proposal.commit = r.admitBuilding(epoch, state, started, store.BuildingMethodRequest{Goal: goal.Goal.ID, Revision: goal.Revision, Method: supplyRoomShellMethod, Plan: plan, Current: snapshot, Tick: projection.Identity.Tick, Bounds: domain.Known(projection.Bounds), Stock: stock, Previews: previews, Purpose: policy.Routine})
	return PlanResult{Kind: PlanProposed, Proposal: proposal, Verdict: BuildingReasonAdmitted}, nil
}

// storageRoomRing is the planned storage room's own ring to raise, door
// first, or nil when a player wall or door stands on every cell of it.
func storageRoomRing(room domain.RoomFootprint, cells []policy.SiteCell) []domain.Cell {
	at := make(map[domain.Cell]policy.SiteCell, len(cells))
	for _, c := range cells {
		at[c.Cell] = c
	}
	door := room.Door()
	stands := true
	for _, w := range room.Walls() {
		c := at[w]
		edifice, ek := c.PlayerEdifice.Value()
		doorway, dk := c.Doorway.Value()
		stands = stands && (ek && edifice != "" || dk && doorway)
	}
	if stands {
		return nil
	}
	ring := []domain.Cell{door}
	for _, w := range room.Walls() {
		if w != door {
			ring = append(ring, w)
		}
	}
	return ring
}

// supplyRoomRoleCells is the storage room's cells by what rock on them
// means: the interior and the door need floor, the other ring cells are
// walls, which natural rock already is.
func supplyRoomRoleCells(room domain.RoomFootprint) []policy.RoleCell {
	door := room.Door()
	cells := []policy.RoleCell{{Cell: door, Role: policy.RockNeedsFloor}}
	for _, cell := range room.Interior() {
		cells = append(cells, policy.RoleCell{Cell: cell, Role: policy.RockNeedsFloor})
	}
	for _, cell := range room.Walls() {
		if cell != door {
			cells = append(cells, policy.RoleCell{Cell: cell, Role: policy.RockBlocks})
		}
	}
	return cells
}

// plannedStorageRoom is the layout plan's storage room, the slot the
// starter shell stands on at every tier (#1177).
func plannedStorageRoom(projection observation.ColonyProjection) (domain.RoomFootprint, bool) {
	plan, known := projection.LayoutPlan.Value()
	if !known {
		return domain.RoomFootprint{}, false
	}
	shells := plan.PlannedShells(policy.RoomRoleStoreroom)
	if len(shells) == 0 {
		return domain.RoomFootprint{}, false
	}
	// A further storage room (#1772) is raised before the first one is
	// zoned again.
	for _, shell := range shells {
		if storageRoomRing(shell, projection.Cells) != nil {
			return shell, true
		}
	}
	return shells[0], true
}

// previewSupplyRoomShell previews the storage room's ring, perimeter door
// first (a Door, Wall elsewhere), mirroring previewPenShell. It never
// commits: a rejected or infeasible cell aborts the room.
func (r *RoutineSecureSuppliesPlanner) previewSupplyRoomShell(ctx context.Context, snapshot domain.GenerationSnapshot, perimeter []domain.Cell, stuff string, facts observation.ColonyProjection) ([]domain.Action, []policy.Preview, policy.StockObservation, Verdict, error) {
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
			return nil, nil, policy.StockObservation{}, Verdict{}, err
		}
		action, err := domain.NewBuildingAction(domain.ActionID(fmt.Sprintf("%s-%d", snapshot.Plan, i)), building)
		if err != nil {
			return nil, nil, policy.StockObservation{}, Verdict{}, err
		}
		preview, _, err := r.native.PreviewBuilding(ctx, action, snapshot)
		if err != nil {
			return nil, nil, policy.StockObservation{}, Verdict{}, err
		}
		v := preview.Preview
		made, madeKnown := v.MadeFromStuff.Value()
		if !madeKnown || made != (stuff != "") {
			return nil, nil, policy.StockObservation{}, fieldUnavailable("supply_shell_preview"), nil
		}
		footprint, fk := v.Footprint.Value()
		can, ck := v.CanPlace.Value()
		safe, sk := v.SafeToPlace.Value()
		if !fk || len(footprint) != 1 || footprint[0] != cell || !ck || !can || !sk || !safe {
			return nil, nil, policy.StockObservation{}, BuildingReasonNoSpace, nil
		}
		if err := mergeRoutineStock(&stock, preview.Stock, i == 0); err != nil {
			return nil, nil, policy.StockObservation{}, Verdict{}, err
		}
		actions = append(actions, action)
		previews = append(previews, v)
	}
	return actions, previews, stock, Verdict{}, nil
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
