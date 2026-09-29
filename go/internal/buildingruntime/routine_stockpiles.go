package buildingruntime

import (
	"context"
	"crypto/sha256"
	"errors"
	"fmt"
	"strings"
	"sync"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
)

// stockpileMemory remembers, per world, since when each owned stockpile has
// sat mostly empty: the reviewer records it every review, the planner reads
// it. A restart forgets it, which only delays a shrink by a day.
type stockpileMemory struct {
	mu    sync.Mutex
	world string
	low   map[string]domain.Tick
}

func stockpileWorld(s domain.GenerationSnapshot) string {
	return fmt.Sprintf("%s/%s/%d", s.Colony, s.Load, s.Map)
}

// observe records the zones' low state at tick and fills LowSince.
func (m *stockpileMemory) observe(world string, tick domain.Tick, zones []policy.StockpileZone) {
	m.mu.Lock()
	defer m.mu.Unlock()
	if m.world != world || m.low == nil {
		m.world, m.low = world, map[string]domain.Tick{}
	}
	seen := map[string]bool{}
	for i := range zones {
		z := &zones[i]
		seen[z.ID] = true
		if !z.Low() {
			delete(m.low, z.ID)
			continue
		}
		since, ok := m.low[z.ID]
		if !ok || since > tick {
			since = tick
			m.low[z.ID] = since
		}
		z.LowSince = since
	}
	for id := range m.low {
		if !seen[id] {
			delete(m.low, id)
		}
	}
}

// fill copies the recorded LowSince onto zones still low, without recording.
func (m *stockpileMemory) fill(world string, zones []policy.StockpileZone) {
	m.mu.Lock()
	defer m.mu.Unlock()
	if m.world != world {
		return
	}
	for i := range zones {
		if since, ok := m.low[zones[i].ID]; ok && zones[i].Low() {
			zones[i].LowSince = since
		}
	}
}

// stockpileRequest builds the MaintainStockpiles input from the projection's
// planning cells (a zone's cells are those naming it; a cell whose
// storage-empty flag is false holds things) and the colony's stockpile
// claims, their settings superseded by the latest patch of each; the
// registered roles judge on the projection and benches.
func stockpileRequest(projection *observation.ColonyProjection, owned []store.OwnedZone, patches map[string]store.AppliedStockpile, benches domain.Fact[map[string]bool]) policy.StockpileRequest {
	type cells struct{ all, stored []domain.Cell }
	byZone := map[string]*cells{}
	for _, cell := range projection.Cells {
		id, known := cell.ZoneID.Value()
		if !known || id == "" {
			continue
		}
		entry := byZone[id]
		if entry == nil {
			entry = &cells{}
			byZone[id] = entry
		}
		entry.all = append(entry.all, cell.Cell)
		if empty, ek := cell.StorageEmpty.Value(); ek && !empty {
			entry.stored = append(entry.stored, cell.Cell)
		}
	}
	request := policy.StockpileRequest{Tick: projection.Identity.Tick, Roles: stockpileRoles(StockpileRoleInput{Projection: projection, Benches: benches}), Cells: projection.Cells, Bounds: projection.Bounds, Protected: nil, Colonists: projection.Facts.Colonists, Anchor: projection.Center, Rooms: domain.Unknown[[]policy.Room]()}
	if rooms, ok := projection.Rooms.Value(); ok {
		request.Rooms = domain.Known(rooms.Rooms)
	}
	request.Sited = stockpileSites(projection, request.Protected)
	if plan, known := projection.LayoutPlan.Value(); known {
		request.Prisons = policy.PrisonCells(plan)
	}
	request.Opening = true
	if census, ok := projection.Facts.CurrentConstruction.Value(); ok {
		for _, b := range census.Buildings {
			if foodStorageCookingDefinitions[b.Building.Definition()] {
				cell := b.Building.Cell()
				request.Kitchen = &cell
				break
			}
		}
	}
	for _, z := range owned {
		entry := byZone[z.ID]
		if z.Kind != domain.StockpileZone || entry == nil {
			continue
		}
		zone := policy.StockpileZone{ID: z.ID, Role: z.Role, Cells: entry.all, Stored: entry.stored, Filter: z.Filter, Priority: z.Priority}
		if patch, ok := patches[z.ID]; ok && patch.Kind == domain.StorageZoneTarget {
			zone.Filter, zone.Priority = patch.Filter, patch.Priority
			if patch.Role != "" {
				zone.Role = patch.Role
			}
		}
		request.Zones = append(request.Zones, zone)
	}
	return request
}

// reviewStockpiles serves the MaintainStockpiles review (#725) on the
// projection when the method is served; otherwise the fact stays unknown
// and the goal is never assessed active.
func (r *RoutineReviewer) reviewStockpiles(ctx context.Context, snapshot domain.GenerationSnapshot, projection *observation.ColonyProjection) error {
	projection.Facts.Stockpiles = domain.Unknown[policy.StockpileReview]()
	if !r.methodEnabled(policy.MaintainStockpiles) {
		return nil
	}
	request, known, err := r.stockpileRequest(ctx, snapshot, projection)
	if err != nil || !known {
		return err
	}
	r.stockpiles.observe(stockpileWorld(snapshot), request.Tick, request.Zones)
	review := policy.PlanStockpileMaintenance(request)
	projection.Facts.Stockpiles = domain.Known(review)
	for _, e := range review.Edits {
		clockEvent(ctx, "layout", "stockpiles", "stockpile edit: "+e.Explanation, "zone", e.Zone, "kind", string(e.Kind), "hauls", e.Hauls)
	}
	return nil
}

func (r *RoutineReviewer) stockpileRequest(ctx context.Context, snapshot domain.GenerationSnapshot, projection *observation.ColonyProjection) (policy.StockpileRequest, bool, error) {
	tick := projection.Identity.Tick
	claims, err := r.player.journal.ZoneClaims(ctx, snapshot, tick)
	if err != nil {
		return policy.StockpileRequest{}, false, err
	}
	owned, ok := claims.Value()
	if !ok || len(projection.Cells) == 0 {
		return policy.StockpileRequest{}, false, nil
	}
	patches, err := r.player.journal.StockpilePatches(ctx, snapshot, tick)
	if err != nil {
		return policy.StockpileRequest{}, false, err
	}
	benches := domain.Unknown[map[string]bool]()
	roles := map[string]bool{}
	for _, z := range owned {
		roles[z.Role] = true
		if _, read := benches.Value(); strings.HasPrefix(z.Role, domain.IngredientsPrefix) && !read {
			if benches, err = r.standingBenches(ctx, snapshot, projection.Identity); err != nil {
				return policy.StockpileRequest{}, false, err
			}
		}
	}
	zoneGoal := map[string]domain.GoalID{}
	for _, z := range owned {
		zoneGoal[z.ID] = z.Goal
	}
	request := stockpileRequest(projection, owned, patches, benches)
	for _, z := range request.Zones {
		shelves, _, err := zoneShelves(ctx, r.player.journal, zoneGoal[z.ID], z.ID, projection.Facts.CurrentConstruction)
		if err != nil {
			return policy.StockpileRequest{}, false, err
		}
		for _, s := range shelves {
			if s.Building == "" || s.Open {
				continue
			}
			shelf := policy.StockpileShelf{Building: s.Building, Zone: z.ID, Cells: len(s.Cells)}
			if applied, ok := patches[s.Building]; ok && applied.Kind == domain.StorageBuildingTarget {
				shelf.Patched, shelf.Filter, shelf.Priority = true, applied.Filter, applied.Priority
			}
			request.Shelves = append(request.Shelves, shelf)
		}
	}
	weapons := 0
	if !roles[domain.WeaponsRole] {
		if weapons, err = r.looseWeapons(ctx, snapshot, projection.Bounds); err != nil {
			return policy.StockpileRequest{}, false, err
		}
	}
	request.Needs = stockpileNeeds(projection.Facts, weapons)
	return request, true, nil
}

// stockpileNeeds counts the things waiting for each fixed role (#724):
// serviceable stored apparel (hit points and quality over the gear floors)
// for apparel, the loose weapons for weapons, poor stored apparel and the
// worn-out garments pawns will shed for the worn dump, spoiled items and
// rotting animal corpses for the rotten dump, humanlike corpses for the
// corpse dump. An unknown census counts nothing.
func stockpileNeeds(facts policy.RoutineFacts, weapons int) map[string]int {
	needs := map[string]int{domain.WeaponsRole: weapons}
	if gear, ok := facts.Gear.Value(); ok {
		if stored, ok := gear.Stored.Value(); ok {
			for _, row := range stored {
				if float64(row.HPBand) >= domain.GearHitPointFloor*10 && row.Quality >= 2 {
					needs[domain.ApparelRole] += row.Count
				} else {
					needs[domain.WornDumpRole] += row.Count
				}
			}
		}
		for _, pawn := range gear.Pawns {
			worn, _ := pawn.Apparel.Value()
			for _, a := range worn {
				if a.Condition < domain.GearHitPointFloor {
					needs[domain.WornDumpRole]++
				}
			}
		}
	}
	if waste, ok := facts.Waste.Value(); ok {
		for _, item := range waste {
			if item.State == policy.WasteBuried {
				continue
			}
			switch {
			case item.Kind == "spoiled", item.Kind == "corpse" && item.CorpseOf == domain.CorpseAnimal:
				needs[domain.RottenDumpRole]++
			case item.Kind == "corpse" && (item.CorpseOf == domain.CorpseColonist || item.CorpseOf == domain.CorpseStranger):
				needs[domain.CorpseDumpRole]++
			}
		}
	}
	return needs
}

// weaponCensus is the loose-weapon read the weapons role counts from.
type weaponCensus interface {
	ReadEquipWeapons(context.Context, *c.Identity, domain.Cell, domain.Cell) (bridge.EquipRead, bridge.Result, error)
}

// looseWeapons counts the unbiocoded weapons by trade lying on the map; a
// source without the read counts none.
func (r *RoutineReviewer) looseWeapons(ctx context.Context, snapshot domain.GenerationSnapshot, bounds policy.Bounds) (int, error) {
	source, ok := r.native.(weaponCensus)
	if !ok || bounds.Width <= 0 || bounds.Height <= 0 {
		return 0, nil
	}
	read, _, err := source.ReadEquipWeapons(ctx, boundary.Identity(snapshot), domain.Cell{}, domain.Cell{X: bounds.Width - 1, Z: bounds.Height - 1})
	if err != nil {
		return 0, err
	}
	if _, err = boundary.Context(read.Context, snapshot); err != nil {
		return 0, fmt.Errorf("%w: looseWeapons: err != nil", ErrControl)
	}
	count := 0
	for _, w := range read.Targets {
		if !w.Biocoded && policy.ClassifyWeapon(w.ByTrade, w.Ranged, w.Melee) != policy.WeaponMakeshift {
			count++
		}
	}
	return count, nil
}

// standingBenches is the bench census as a set of ids; unknown without
// the read.
func (r *RoutineReviewer) standingBenches(ctx context.Context, snapshot domain.GenerationSnapshot, expected observation.Identity) (domain.Fact[map[string]bool], error) {
	native, ok := r.native.(RoutineWorkBenchSource)
	if !ok {
		return domain.Unknown[map[string]bool](), nil
	}
	rows, _, err := r.benchSource(native, expected, false).ReadGearBenches(ctx, boundary.Identity(snapshot))
	if err != nil {
		return domain.Unknown[map[string]bool](), err
	}
	ids := map[string]bool{}
	for _, row := range rows {
		ids[row.Bench.ID] = true
	}
	return domain.Known(ids), nil
}

// The gear stockpiles and dumps are MaintainStockpiles' own roles (#724):
// fixed settings, never retired.
func init() {
	specs := map[string]domain.StockpileRoleSpec{}
	for _, spec := range domain.GearAndDumpRoles() {
		specs[spec.Role] = spec
	}
	source := func(_ StockpileRoleInput, role string) (policy.StockpileRoleState, bool) {
		spec, ok := specs[role]
		return policy.StockpileRoleState{Filter: spec.Filter, Priority: spec.Priority}, ok
	}
	for _, prefix := range []string{domain.ApparelRole, domain.WeaponsRole, "dump"} {
		RegisterStockpileRole(prefix, source)
	}
}

// RoutineStockpileSource refreshes one zone's presence and CAS token for
// each edit.
type RoutineStockpileSource interface {
	ReadZoneDeleteTarget(context.Context, *c.Identity, string) (bridge.ZoneDeleteTarget, bridge.Result, error)
}

// RoutineStockpilePlanner commits the MaintainStockpiles review's edits
// (#725) as one plan per cycle, one action per zone under that zone's
// fresh CAS token: zone_cell_edit to grow or shrink, stockpile_patch to
// retarget a zone or configure a shelf like its zone, zone_delete to
// delete or merge. A missing fixed-role zone (#724) is a zone_create
// admitted alone once no other edit stands. A plan still open holds the
// next cycle.
type RoutineStockpilePlanner struct {
	reviewer *RoutineReviewer
	native   RoutineStockpileSource
}

type RoutineStockpileResult struct {
	Reason RoutineBuildingReason
	Plan   domain.PlanID
	Edits  int
}

func NewRoutineStockpilePlanner(reviewer *RoutineReviewer, native RoutineStockpileSource) (*RoutineStockpilePlanner, error) {
	if reviewer == nil || native == nil || reviewer.native == nil {
		return nil, fmt.Errorf("%w: NewRoutineStockpilePlanner: reviewer == nil || native == nil || reviewer.native == nil", ErrControl)
	}
	return &RoutineStockpilePlanner{reviewer, native}, nil
}

func (r *RoutineStockpilePlanner) step(call, epoch context.Context, _ *stepArbiter) (RoutineStockpileResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutineStockpileResult{Reason: BuildingMethodDisabled}, nil
	}
	if !state.ObservationKnown {
		return RoutineStockpileResult{}, fmt.Errorf("%w: step: !state.ObservationKnown", ErrControl)
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutineStockpileResult{}, err
	}
	if !review.Enabled || review.Snapshot != state.Snapshot {
		return RoutineStockpileResult{Reason: BuildingMethodNoReview}, nil
	}
	goal, workable, err := p.journal.Workable(call, review, policy.MaintainStockpiles)
	if err != nil {
		return RoutineStockpileResult{}, err
	}
	if !workable {
		return RoutineStockpileResult{Reason: BuildingMethodNoDeficit}, nil
	}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoutineStockpileResult{}, err
		}
		if store.PlanOpen(plan) {
			return RoutineStockpileResult{Reason: BuildingMethodExistingWork}, nil
		}
	}
	expected, err := routineScope(call, r.reviewer.native)
	if err != nil {
		return RoutineStockpileResult{}, err
	}
	if !routineBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoutineStockpileResult{}, fmt.Errorf("%w: step: !routineBuildingBoundary(expected, state.Snapshot, review.Tick)", ErrControl)
	}
	claims, err := p.journal.ConstructionClaims(call, state.Snapshot, expected.Tick)
	if err != nil {
		return RoutineStockpileResult{}, err
	}
	observe := r.reviewer.observeOwned
	if r.reviewer.roomsEnabled() {
		observe = r.reviewer.observeRooms
	}
	read, err := observe(call, r.reviewer.native, expected, claims)
	if err != nil {
		return RoutineStockpileResult{}, err
	}
	projection := read.Projection
	request, known, err := r.reviewer.stockpileRequest(call, state.Snapshot, &projection)
	if err != nil {
		return RoutineStockpileResult{}, err
	}
	if !known {
		return RoutineStockpileResult{Reason: BuildingMethodUnknown}, nil
	}
	r.reviewer.stockpiles.fill(stockpileWorld(state.Snapshot), request.Zones)
	proposal := policy.PlanStockpileMaintenance(request)
	if !proposal.Active {
		return RoutineStockpileResult{Reason: BuildingMethodNoDeficit}, nil
	}
	tick := projection.Identity.Tick
	method := domain.MethodID(fmt.Sprintf("stockpiles-%d", tick))
	id := domain.MintPlanID()
	if _, err := p.journal.LoadGoalMethod(call, goal.Goal.ID, goal.Goal.Epoch, method); err == nil {
		return RoutineStockpileResult{Reason: BuildingMethodUsed}, nil
	} else if !errors.Is(err, store.ErrNotFound) {
		return RoutineStockpileResult{}, err
	}
	identity := boundary.Identity(state.Snapshot)
	var actions []domain.Action
	var creates []policy.StockpileEdit
	for _, e := range proposal.Edits {
		if e.Kind == policy.StockpileCreate {
			creates = append(creates, e)
			continue
		}
		present, err := r.editTargetPresent(call, identity, e)
		if err != nil {
			return RoutineStockpileResult{}, err
		}
		if !present {
			continue
		}
		action, err := stockpileEditAction(domain.ActionID(fmt.Sprintf("%s-%d", id, len(actions))), e)
		if err != nil {
			clockSchedulerLog("Stockpiles: %s %s dropped: %v", e.Kind, e.Zone, err)
			continue
		}
		actions = append(actions, action)
	}
	if len(actions) == 0 && len(creates) > 0 {
		// The new zones are admitted together, previewed, once the edits
		// of standing zones are done.
		return r.create(call, epoch, state, goal, projection, read.StartedAt, creates)
	}
	if len(actions) == 0 {
		return RoutineStockpileResult{Reason: BuildingMethodRefused}, nil
	}
	plan, err := domain.NewPlan(id, 1, actions)
	if err != nil {
		return RoutineStockpileResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutineStockpileResult{}, err
	}
	now := r.reviewer.clock.Now()
	if p.session.State() != state || now.Before(read.StartedAt) || now.Sub(read.StartedAt) > r.reviewer.maxAge {
		return RoutineStockpileResult{}, fmt.Errorf("%w: step: p.session.State() != state || now.Before(read.StartedAt) || now.Sub(read.StartedAt) > r.reviewer.maxAge", ErrControl)
	}
	if _, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, method, plan); err != nil {
		return RoutineStockpileResult{}, err
	}
	for _, e := range proposal.Edits {
		if e.Kind == policy.StockpileCreate {
			continue
		}
		clockEvent(call, "layout", "stockpiles", "stockpile edit admitted: "+e.Explanation, "zone", e.Zone, "kind", string(e.Kind), "plan", string(id))
	}
	return RoutineStockpileResult{Reason: BuildingMethodAdmitted, Plan: id, Edits: len(actions)}, nil
}

// create admits the missing zones (#724, and the opening stockpiles) as one
// method: each zone is previewed natively on the review's zone map token, a
// refused one is dropped, and the accepted ones are admitted together with
// their footprints reserved, like every routine zone. Zoning is instant and
// needs no worker, so a fresh colony's zones land in one step.
func (r *RoutineStockpilePlanner) create(call, epoch context.Context, state ControlState, goal store.GoalState, projection observation.ColonyProjection, started time.Time, edits []policy.StockpileEdit) (RoutineStockpileResult, error) {
	p := r.reviewer.player
	native, ok := r.native.(interface {
		PreviewZone(context.Context, *c.Identity, domain.ZoneCreate) (*op.ZonePreviewReply, bridge.Result, error)
	})
	if !ok {
		return RoutineStockpileResult{Reason: BuildingMethodUnknown}, nil
	}
	tick := projection.Identity.Tick
	roles := make([]string, len(edits))
	for i, e := range edits {
		roles[i] = e.Role
	}
	digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%d/create/%s/%d", goal.Goal.ID, goal.Goal.Epoch, strings.Join(roles, ","), tick)))
	id := domain.MintPlanID()
	method := domain.MethodID(fmt.Sprintf("stockpile-create-%x", digest[:8]))
	if _, err := p.journal.LoadGoalMethod(call, goal.Goal.ID, goal.Goal.Epoch, method); err == nil {
		return RoutineStockpileResult{Reason: BuildingMethodUsed}, nil
	} else if !errors.Is(err, store.ErrNotFound) {
		return RoutineStockpileResult{}, err
	}
	snapshot := state.Snapshot
	snapshot.Plan = id
	snapshot.Revision = 1
	var actions []domain.Action
	var previews []policy.Preview
	var admitted []policy.StockpileEdit
	for _, e := range edits {
		value, err := domain.NewFilteredStockpileZone(e.Filter, e.Priority, e.Cells)
		if err == nil {
			value, err = value.WithRole(e.Role)
		}
		if err != nil {
			return RoutineStockpileResult{}, err
		}
		reply, _, err := native.PreviewZone(call, boundary.Identity(snapshot), value)
		var refused *bridge.NativeFailure
		if errors.As(err, &refused) {
			clockEvent(call, "layout", "stockpiles", "stockpile create preview refused", "role", e.Role, "code", refused.Value.GetCode().String(), "detail", refused.Value.GetDetail())
			continue
		}
		if err != nil {
			return RoutineStockpileResult{}, err
		}
		v := reply.GetEvaluated()
		if v == nil || !v.GetAccepted() {
			clockEvent(call, "layout", "stockpiles", "stockpile create preview not accepted", "role", e.Role)
			continue
		}
		if _, err = boundary.Context(v.Context, snapshot); err != nil || domain.Tick(v.Context.GetTick()) < tick {
			return RoutineStockpileResult{}, fmt.Errorf("%w: create: err != nil || domain.Tick(v.Context.GetTick()) < tick", ErrControl)
		}
		action, err := domain.NewZoneCreateAction(domain.ActionID(fmt.Sprintf("%s-%d", id, len(actions))), value)
		if err != nil {
			return RoutineStockpileResult{}, err
		}
		actions = append(actions, action)
		previews = append(previews, policy.Preview{Action: action, Snapshot: snapshot, Tick: tick, CanPlace: domain.Known(true), SafeToPlace: domain.Known(true), MadeFromStuff: domain.Known(false), WatchCellsAccessible: domain.Known(true), Footprint: domain.Known(value.Cells()), Costs: domain.Known([]policy.Amount{})})
		admitted = append(admitted, e)
	}
	if len(actions) == 0 {
		return RoutineStockpileResult{Reason: BuildingMethodRefused}, nil
	}
	plan, err := domain.NewPlan(id, 1, actions)
	if err != nil {
		return RoutineStockpileResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutineStockpileResult{}, err
	}
	now := r.reviewer.clock.Now()
	if p.session.State() != state || now.Before(started) || now.Sub(started) > r.reviewer.maxAge {
		return RoutineStockpileResult{}, fmt.Errorf("%w: create: p.session.State() != state || now.Before(started) || now.Sub(started) > r.reviewer.maxAge", ErrControl)
	}
	decision, err := admitMethod(call, p.journal, store.BuildingMethodRequest{Goal: goal.Goal.ID, Revision: goal.Revision, Method: method, Plan: plan, Current: snapshot, Tick: tick, Bounds: domain.Known(projection.Bounds), Stock: policy.StockObservation{Snapshot: snapshot, Tick: tick}, Previews: previews, Purpose: policy.Routine})
	if err != nil {
		return RoutineStockpileResult{}, err
	}
	if !decision.Admitted {
		return RoutineStockpileResult{Reason: BuildingMethodRefused}, nil
	}
	for _, e := range admitted {
		clockEvent(call, "layout", "stockpiles", "stockpile edit admitted: "+e.Explanation, "role", e.Role, "kind", string(e.Kind), "plan", string(id))
	}
	return RoutineStockpileResult{Reason: BuildingMethodAdmitted, Plan: id, Edits: len(actions)}, nil
}

// shelfTargetSource reads a shelf's storage settings CAS token.
type shelfTargetSource interface {
	ReadStorageBuildingTarget(context.Context, *c.Identity, string) (bridge.StorageBuildingTarget, bridge.Result, error)
}

// editTargetPresent reports whether the edit's target (the stockpile zone,
// or the shelf for a shelf patch) is still there; false when the source
// cannot read it.
func (r *RoutineStockpilePlanner) editTargetPresent(ctx context.Context, identity *c.Identity, e policy.StockpileEdit) (bool, error) {
	if e.Kind == policy.StockpileShelfPatch {
		source, ok := r.native.(shelfTargetSource)
		if !ok {
			return false, nil
		}
		target, _, err := source.ReadStorageBuildingTarget(ctx, identity, e.Zone)
		return err == nil && target.Present, err
	}
	target, _, err := r.native.ReadZoneDeleteTarget(ctx, identity, e.Zone)
	return err == nil && target.Present && target.Type == "stockpile", err
}

// stockpileEditAction is one edit's action.
func stockpileEditAction(id domain.ActionID, e policy.StockpileEdit) (domain.Action, error) {
	switch e.Kind {
	case policy.StockpileGrow, policy.StockpileShrink:
		mode := domain.AddZoneCells
		if e.Kind == policy.StockpileShrink {
			mode = domain.RemoveZoneCells
		}
		edit, err := domain.NewZoneCellEdit(e.Zone, mode, e.Cells)
		if err != nil {
			return domain.Action{}, err
		}
		return domain.NewZoneCellEditAction(id, edit)
	case policy.StockpileRetarget:
		patch, err := domain.NewStockpilePatch(domain.StorageZoneTarget, e.Zone, e.Filter, e.Priority, e.Role)
		if err != nil {
			return domain.Action{}, err
		}
		return domain.NewStockpilePatchAction(id, patch)
	case policy.StockpileShelfPatch:
		patch, err := domain.NewStockpilePatch(domain.StorageBuildingTarget, e.Zone, e.Filter, e.Priority, e.Role)
		if err != nil {
			return domain.Action{}, err
		}
		return domain.NewStockpilePatchAction(id, patch)
	case policy.StockpileDelete, policy.StockpileMerge:
		del, err := domain.NewZoneDelete(e.Zone)
		if err != nil {
			return domain.Action{}, err
		}
		return domain.NewZoneDeleteAction(id, del)
	}
	return domain.Action{}, fmt.Errorf("unknown stockpile edit %q", e.Kind)
}
