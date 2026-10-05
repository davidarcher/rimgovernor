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
	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
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
	// demand and incinerator are the storage planner's latest layout demands
	// for demandWorld: layout reads them to add the armory and wardrobe
	// (#1773) and to reserve the incinerator (#1814).
	demand      policy.RoomDemand
	incinerator policy.IncineratorSite
	demandWorld string
	// siteErr is the last storage-plan site report logged, so a standing
	// failure logs once and again when it changes or clears.
	siteErr string
}

// siteErrChanged records err and reports a non-nil report that differs from
// the last one logged.
func (m *stockpileMemory) siteErrChanged(err error) bool {
	m.mu.Lock()
	defer m.mu.Unlock()
	text := ""
	if err != nil {
		text = err.Error()
	}
	changed := text != "" && text != m.siteErr
	m.siteErr = text
	return changed
}

// setDemand records the planner's layout demands for world.
func (m *stockpileMemory) setDemand(world string, demand policy.RoomDemand, incinerator policy.IncineratorSite) {
	m.mu.Lock()
	defer m.mu.Unlock()
	m.demand, m.incinerator, m.demandWorld = demand, incinerator, world
}

// layoutDemand is the recorded layout demands; none for another world.
func (m *stockpileMemory) layoutDemand(world string) (policy.RoomDemand, policy.IncineratorSite) {
	m.mu.Lock()
	defer m.mu.Unlock()
	if m.demandWorld != world {
		return policy.RoomDemand{}, policy.IncineratorSite{}
	}
	return m.demand, m.incinerator
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
func stockpileRequest(projection *observation.ColonyProjection, owned []store.OwnedZone, patches map[string]store.AppliedStockpile, benches domain.Fact[map[string]bool], inputs []policy.BenchInput, gear *policy.GearStore, protected []domain.Cell) policy.StockpileRequest {
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
	request := policy.StockpileRequest{Tick: projection.Identity.Tick, Roles: stockpileRoles(StockpileRoleInput{Projection: projection, Benches: benches}), Cells: projection.Cells, Bounds: projection.Bounds, Protected: protected, Colonists: projection.Facts.Colonists, Rooms: domain.Unknown[[]policy.Room]()}
	if core, planned := planCore(*projection); planned {
		request.Anchor = core
	}
	if plan, known := projection.LayoutPlan.Value(); known {
		request.Planned = policy.PlannedRoomGround(plan)
	}
	if rooms, ok := projection.Rooms.Value(); ok {
		request.Rooms = domain.Known(rooms.Rooms)
	}
	for _, module := range []policy.ModuleRole{policy.ModuleStorage, policy.ModuleArmory, policy.ModuleWardrobe} {
		if _, owed := plannedRoomOwed(*projection, module); owed {
			request.Shells = append(request.Shells, module)
		}
	}
	request.Opening = true
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
	storage := storageRequest(projection, request.Protected)
	storage.Gear = gear
	storage.Zones = request.Zones
	if rooms, ok := request.Rooms.Value(); ok {
		storage.Dumps = &policy.DumpStore{Needs: policy.DumpNeeds(projection.Facts), Rooms: rooms, Anchor: request.Anchor, Incinerator: standingIncinerator(*projection), Planned: request.Planned}
	}
	storage.BenchInputs = inputs
	plan := policy.PlanStorage(storage)
	request.Sited, request.RoomDemand, request.Incinerator, request.SiteErr = plan.Sites, plan.RoomDemand, plan.Incinerator, plan.Err
	return request
}

// reviewStockpiles serves the MaintainStockpiles review (#725) on the
// projection when the method is served; otherwise the fact stays unknown
// and the goal is never assessed active.
func (r *Rounder) reviewStockpiles(ctx context.Context, snapshot domain.GenerationSnapshot, projection *observation.ColonyProjection) error {
	projection.Facts.Stockpiles = domain.Unknown[policy.StockpileReview]()
	if !r.methodEnabled(policy.MaintainStockpiles) {
		return nil
	}
	request, missing, err := r.stockpileRequest(ctx, snapshot, projection)
	if err != nil || missing != "" {
		return err
	}
	r.stockpiles.observe(stockpileWorld(snapshot), request.Tick, request.Zones)
	r.stockpiles.setDemand(stockpileWorld(snapshot), request.RoomDemand, request.Incinerator)
	r.stockpiles.siteErrChanged(request.SiteErr)
	review := policy.PlanStockpileMaintenance(request)
	projection.Facts.Stockpiles = domain.Known(review)
	for _, e := range review.Edits {
		telemetry.Decide(ctx, stockpileEditDecision("proposed", "", e.Zone, map[string]any{"kind": string(e.Kind), "hauls": e.Hauls, "detail": e.Explanation}))
	}
	return nil
}

// stockpileRequest assembles the maintenance request; the string names the
// fact still unread (empty when the request is whole).
func (r *Rounder) stockpileRequest(ctx context.Context, snapshot domain.GenerationSnapshot, projection *observation.ColonyProjection) (policy.StockpileRequest, string, error) {
	tick := projection.Identity.Tick
	claims, err := r.player.journal.ZoneClaims(ctx, snapshot, tick)
	if err != nil {
		return policy.StockpileRequest{}, "", err
	}
	owned, ok := claims.Value()
	if !ok {
		return policy.StockpileRequest{}, "zone_claims", nil
	}
	if len(projection.Cells) == 0 {
		return policy.StockpileRequest{}, "site_cells", nil
	}
	patches, err := r.player.journal.StockpilePatches(ctx, snapshot, tick)
	if err != nil {
		return policy.StockpileRequest{}, "", err
	}
	roles := map[string]bool{}
	for _, z := range owned {
		roles[z.Role] = true
	}
	census, err := r.benchCensus(ctx, snapshot, projection.Identity)
	if err != nil {
		return policy.StockpileRequest{}, "", err
	}
	benches := make(map[string]bool, len(census))
	for _, row := range census {
		benches[row.Bench.ID] = true
	}
	zoneGoal := map[string]domain.ConcernID{}
	for _, z := range owned {
		zoneGoal[z.ID] = z.Concern
	}
	gear, err := r.gearStore(ctx, snapshot, projection)
	if err != nil {
		return policy.StockpileRequest{}, "", err
	}
	protected, err := r.reservedGround(ctx, snapshot, projection)
	if err != nil {
		return policy.StockpileRequest{}, "", err
	}
	request := stockpileRequest(projection, owned, patches, domain.Known(benches), benchInputs(census, projection), gear, protected)
	for _, z := range request.Zones {
		shelves, _, err := zoneShelves(ctx, r.player.journal, zoneGoal[z.ID], z.ID, projection.Facts.CurrentConstruction)
		if err != nil {
			return policy.StockpileRequest{}, "", err
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
	return request, "", nil
}

// reservedGround is the ground no stockpile site takes (#1795): the
// footprints of held building reservations and the unroofed floor inside a
// shell's wall ring, which belongs to the shell's own furniture until a roof
// stands. Every sited role reads it as the planner's protected set.
func (r *Rounder) reservedGround(ctx context.Context, snapshot domain.GenerationSnapshot, projection *observation.ColonyProjection) ([]domain.Cell, error) {
	held, err := r.player.journal.BuildingReservations(ctx, snapshot)
	if err != nil {
		return nil, err
	}
	var protected []domain.Cell
	for _, h := range held {
		protected = append(protected, h.Footprint...)
	}
	claims, err := r.player.journal.ConstructionClaims(ctx, snapshot, projection.Identity.Tick)
	if err != nil {
		return nil, err
	}
	rows, _ := claims.Value()
	unroofed := map[domain.Cell]bool{}
	for _, c := range projection.Cells {
		if roofed, known := c.Roofed.Value(); known && !roofed {
			unroofed[c.Cell] = true
		}
	}
	for _, cell := range shellInteriors(nil, rows) {
		if unroofed[cell] {
			protected = append(protected, cell)
		}
	}
	return protected, nil
}

// gearStore is the serviceable gear the colony holds for the armory and
// wardrobe (#1774): the stored apparel the gear census counts, split into
// armor and clothing by the catalog (ItemFacts.Armor), and the weapons lying
// on the map. The weapons read is skipped once layout plans the armory, which
// no longer needs the count. A catalog naming no armor fails with
// policy.ErrNoArmorDefs; an unknown stored census counts nothing.
func (r *Rounder) gearStore(ctx context.Context, snapshot domain.GenerationSnapshot, projection *observation.ColonyProjection) (*policy.GearStore, error) {
	if len(projection.Facts.Items.Armor) == 0 {
		return nil, policy.ErrNoArmorDefs
	}
	weapons := 0
	if plan, known := projection.LayoutPlan.Value(); !known || len(policy.GearRoomsOwed(plan, policy.RoomDemand{Armory: true})) > 0 {
		var err error
		if weapons, err = r.looseWeapons(ctx, snapshot, projection.Bounds); err != nil {
			return nil, err
		}
	}
	var stored []policy.GearStock
	if gear, ok := projection.Facts.Gear.Value(); ok {
		stored, _ = gear.Stored.Value()
	}
	held, err := policy.NewGearStore(projection.Facts.Items, stored, weapons)
	if err != nil {
		return nil, err
	}
	return &held, nil
}

// weaponCensus is the loose-weapon read the weapons role counts from.
type weaponCensus interface {
	ReadEquipWeapons(context.Context, *c.Identity, domain.Cell, domain.Cell) (bridge.EquipRead, bridge.Result, error)
}

// looseWeapons counts the unbiocoded weapons by trade lying on the map; a
// source without the read counts none.
func (r *Rounder) looseWeapons(ctx context.Context, snapshot domain.GenerationSnapshot, bounds policy.Bounds) (int, error) {
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
	catalog, err := pawnCatalog(ctx, r.native, boundary.Identity(snapshot))
	if err != nil {
		return 0, err
	}
	count := 0
	for _, w := range read.Targets {
		if w.Biocoded {
			continue
		}
		facts, err := catalog.WeaponOf(w.Definition)
		if err != nil {
			return 0, err
		}
		if policy.ClassifyWeapon(facts.ByTrade, facts.Ranged, facts.Melee) != policy.WeaponMakeshift {
			count++
		}
	}
	return count, nil
}

// benchCensus is the bench census (bills and recipes per bench).
func (r *Rounder) benchCensus(ctx context.Context, snapshot domain.GenerationSnapshot, expected observation.Identity) ([]bridge.GearBenchRead, error) {
	native, ok := r.native.(RoundsWorkBenchSource)
	if !ok {
		return nil, fmt.Errorf("%w: benchCensus: native lacks the bench census", ErrControl)
	}
	rows, _, err := r.benchSource(native, expected, false).ReadGearBenches(ctx, boundary.Identity(snapshot))
	return rows, err
}

// The dumps are MaintainStockpiles' own roles (#724): fixed settings, never
// retired.
func init() {
	specs := map[string]domain.StockpileRoleSpec{}
	for _, spec := range domain.DumpRoles() {
		specs[spec.Role] = spec
	}
	RegisterStockpileRole("dump", func(_ StockpileRoleInput, role string) (policy.StockpileRoleState, bool) {
		spec, ok := specs[role]
		return policy.StockpileRoleState{Filter: spec.Filter, Priority: spec.Priority}, ok
	})
}

// RoundsStockpileSource refreshes one zone's presence and CAS token for
// each edit.
type RoundsStockpileSource interface {
	ReadZoneDeleteTarget(context.Context, *c.Identity, string) (bridge.ZoneDeleteTarget, bridge.Result, error)
}

// RoundsStockpilePlanner commits the MaintainStockpiles review's edits
// (#725) as one plan per cycle, one action per zone under that zone's
// fresh CAS token: zone_cell_edit to grow or shrink, stockpile_patch to
// retarget a zone or configure a shelf like its zone, zone_delete to
// delete or merge. A missing fixed-role zone (#724) is a zone_create
// admitted alone once no other edit stands. A plan still open holds the
// next cycle.
type RoundsStockpilePlanner struct {
	reviewer *Rounder
	native   RoundsStockpileSource
	// building shells the planned armory and wardrobe (#1774); nil for a
	// source that cannot preview buildings.
	building *RoundsBuildingPlanner
}

type RoundsStockpileResult struct {
	Verdict
	Plan  domain.PlanID
	Edits int
}

func NewRoundsStockpilePlanner(reviewer *Rounder, native RoundsStockpileSource) (*RoundsStockpilePlanner, error) {
	if reviewer == nil || native == nil || reviewer.native == nil {
		return nil, fmt.Errorf("%w: NewRoundsStockpilePlanner: reviewer == nil || native == nil || reviewer.native == nil", ErrControl)
	}
	planner := &RoundsStockpilePlanner{reviewer: reviewer, native: native}
	if source, ok := reviewer.native.(RoundsBuildingSource); ok {
		planner.building = &RoundsBuildingPlanner{reviewer: reviewer, native: source, concern: policy.MaintainStockpiles, definition: policy.ShellWallDefinition}
	}
	return planner, nil
}

// shell raises the planned gear room of edit through the planned-room shell
// path. handled is false when nothing was admitted (the room stands, its
// shell was tried this Episode, no space, refused or a fact is missing),
// with the verdict saying why.
func (r *RoundsStockpilePlanner) shell(call, epoch context.Context, state ControlState, review store.Rounds, goal store.StandardState, read observation.RoundsReading, edit policy.StockpileEdit) (RoundsStockpileResult, bool, error) {
	if r.building == nil {
		return RoundsStockpileResult{Verdict: fieldUnavailable("building_source")}, false, nil
	}
	room, owed := plannedRoomOwed(read.Projection, policy.ModuleRole(edit.Role))
	if !owed {
		return RoundsStockpileResult{Verdict: waitFor(WaitMethodUsed, "stockpile_room_built")}, false, nil
	}
	result, err := r.building.shellRoom(call, epoch, state, review, goal, read.ColonyReading, room, plannedRoomMethod(room), "storage-planner room")
	telemetry.Decide(call, stockpileEditDecision("proposed", fmt.Sprint(result.Verdict), edit.Role, map[string]any{"kind": "room", "detail": edit.Explanation}))
	if err != nil || shellLeavesZoneEdits(result.Verdict) {
		return RoundsStockpileResult{Verdict: result.Verdict}, false, err
	}
	return RoundsStockpileResult{Verdict: result.Verdict}, true, nil
}

// shellLeavesZoneEdits reports a room-shell verdict that lets the zone edits
// go on this step. Zoning is instant and needs no builder, so a shell that is
// already being worked (WaitExistingWork) must not hold the food stockpile
// back: it stood uncreated for as long as the room's shell was unbuilt, with
// the clock stopped waiting for it.
func shellLeavesZoneEdits(v Verdict) bool {
	return v.Is(WaitMethodUsed) || v.Is(WaitExistingWork) || v.Is(RefusalNoSpace) || v.Is(RefusalFieldUnavailable) || v.Is(RefusalSharedAdmission)
}

// isStockpileEditMethod reports a zone-edit method of MaintainStockpiles (the
// grow, shrink, retarget and delete batch, or a create batch), not a room shell.
func isStockpileEditMethod(m domain.MethodID) bool {
	return strings.HasPrefix(string(m), "stockpiles-") || strings.HasPrefix(string(m), "stockpile-create-")
}

func (r *RoundsStockpilePlanner) step(call, epoch context.Context, _ *stepArbiter) (RoundsStockpileResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoundsStockpileResult{Verdict: BuildingReasonDisabled}, nil
	}
	if !state.ObservationKnown {
		return RoundsStockpileResult{}, fmt.Errorf("%w: step: !state.ObservationKnown", ErrControl)
	}
	review, err := p.journal.LoadRounds(call)
	if err != nil {
		return RoundsStockpileResult{}, err
	}
	if !review.Enabled || review.Snapshot != state.Snapshot {
		return RoundsStockpileResult{Verdict: BuildingReasonNoReview}, nil
	}
	goal, workable, err := p.journal.Workable(call, review, policy.MaintainStockpiles)
	if err != nil {
		return RoundsStockpileResult{}, err
	}
	if !workable {
		return RoundsStockpileResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	for _, method := range goal.Methods {
		// A room shell is building work that takes days; zoning is instant, so
		// only an open zone-edit plan holds the next edits.
		if !isStockpileEditMethod(method.Method) {
			continue
		}
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoundsStockpileResult{}, err
		}
		if store.PlanOpen(plan) {
			return RoundsStockpileResult{Verdict: BuildingReasonExistingWork}, nil
		}
	}
	expected, err := stepScope(call, r.reviewer.native)
	if err != nil {
		return RoundsStockpileResult{}, err
	}
	if !roundsBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoundsStockpileResult{}, fmt.Errorf("%w: step: !roundsBuildingBoundary(expected, state.Snapshot, review.Tick)", ErrControl)
	}
	claims, err := p.journal.ConstructionClaims(call, state.Snapshot, expected.Tick)
	if err != nil {
		return RoundsStockpileResult{}, err
	}
	observe := r.reviewer.observeOwned
	if r.reviewer.roomsEnabled() {
		observe = r.reviewer.observeRooms
	}
	// The medicine store reads the medical beds, so the census carries them.
	// The gear rooms are shelled from the same reading, so the census carries
	// the wall and door definitions.
	read, err := observe(call, r.reviewer.native, expected, claims, policy.ShellWallDefinition, policy.ShellDoorDefinition)
	if err != nil {
		return RoundsStockpileResult{}, err
	}
	projection := read.Projection
	request, missing, err := r.reviewer.stockpileRequest(call, state.Snapshot, &projection)
	if err != nil {
		return RoundsStockpileResult{}, err
	}
	if missing != "" {
		return RoundsStockpileResult{Verdict: fieldUnavailable(missing)}, nil
	}
	r.reviewer.stockpiles.fill(stockpileWorld(state.Snapshot), request.Zones)
	proposal := policy.PlanStockpileMaintenance(request)
	if !proposal.Active {
		return RoundsStockpileResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	// A planned gear room not yet standing is raised first; while its shell
	// waits (already tried, no space, refused) the zone edits go on.
	var edits []policy.StockpileEdit
	var shells []policy.StockpileEdit
	for _, e := range proposal.Edits {
		if e.Kind == policy.StockpileShell {
			shells = append(shells, e)
		} else {
			edits = append(edits, e)
		}
	}
	proposal.Edits = edits
	var waiting Verdict
	if len(shells) > 0 {
		result, handled, err := r.shell(call, epoch, state, review, goal, read, shells[0])
		if err != nil || handled {
			return result, err
		}
		waiting = result.Verdict
	}
	if len(edits) == 0 {
		return RoundsStockpileResult{Verdict: waiting}, nil
	}
	tick := projection.Identity.Tick
	method := domain.MethodID(fmt.Sprintf("stockpiles-%d", tick))
	id := domain.MintPlanID()
	if _, err := p.journal.LoadMethod(call, goal.Standard.ID, goal.Standard.Episode, method); err == nil {
		return RoundsStockpileResult{Verdict: waitFor(WaitMethodUsed, "stockpile_edits")}, nil
	} else if !errors.Is(err, store.ErrNotFound) {
		return RoundsStockpileResult{}, err
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
			return RoundsStockpileResult{}, err
		}
		if !present {
			continue
		}
		action, err := stockpileEditAction(domain.ActionID(fmt.Sprintf("%s-%d", id, len(actions))), e)
		if err != nil {
			telemetry.Decide(call, stockpileEditDecision("refused", "action_invalid", e.Zone, map[string]any{"kind": string(e.Kind), "error": err}))
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
		return RoundsStockpileResult{Verdict: fieldUnavailable("stockpile_edit_targets")}, nil
	}
	plan, err := domain.NewPlan(id, 1, actions)
	if err != nil {
		return RoundsStockpileResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoundsStockpileResult{}, err
	}
	now := r.reviewer.clock.Now()
	if p.session.State() != state || now.Before(read.StartedAt) || now.Sub(read.StartedAt) > r.reviewer.maxAge {
		return RoundsStockpileResult{}, fmt.Errorf("%w: step: p.session.State() != state || now.Before(read.StartedAt) || now.Sub(read.StartedAt) > r.reviewer.maxAge", ErrControl)
	}
	if _, err = p.journal.CommitMethod(call, goal.Standard.ID, goal.Revision, method, plan); err != nil {
		return RoundsStockpileResult{}, err
	}
	for _, e := range proposal.Edits {
		if e.Kind == policy.StockpileCreate {
			continue
		}
		telemetry.Decide(call, stockpileEditDecision("admitted", "", e.Zone, map[string]any{"kind": string(e.Kind), "plan": string(id), "detail": e.Explanation}))
	}
	return RoundsStockpileResult{Verdict: BuildingReasonAdmitted, Plan: id, Edits: len(actions)}, nil
}

// create admits the missing zones (#724, and the opening stockpiles) as one
// method: each zone is previewed natively on the review's zone map token, a
// refused one is dropped, and the accepted ones are admitted together with
// their footprints reserved, like every routine zone. Zoning is instant and
// needs no worker, so a fresh colony's zones land in one step.
func (r *RoundsStockpilePlanner) create(call, epoch context.Context, state ControlState, goal store.StandardState, projection observation.ColonyProjection, started time.Time, edits []policy.StockpileEdit) (RoundsStockpileResult, error) {
	p := r.reviewer.player
	native, ok := r.native.(interface {
		PreviewZone(context.Context, *c.Identity, domain.ZoneCreate) (*op.ZonePreviewReply, bridge.Result, error)
	})
	if !ok {
		return RoundsStockpileResult{Verdict: fieldUnavailable("zone_preview")}, nil
	}
	tick := projection.Identity.Tick
	roles := make([]string, len(edits))
	for i, e := range edits {
		roles[i] = e.Role
	}
	digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%d/create/%s/%d", goal.Standard.ID, goal.Standard.Episode, strings.Join(roles, ","), tick)))
	id := domain.MintPlanID()
	method := domain.MethodID(fmt.Sprintf("stockpile-create-%x", digest[:8]))
	if _, err := p.journal.LoadMethod(call, goal.Standard.ID, goal.Standard.Episode, method); err == nil {
		return RoundsStockpileResult{Verdict: waitFor(WaitMethodUsed, "stockpile_create")}, nil
	} else if !errors.Is(err, store.ErrNotFound) {
		return RoundsStockpileResult{}, err
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
			return RoundsStockpileResult{}, err
		}
		reply, _, err := native.PreviewZone(call, boundary.Identity(snapshot), value)
		var refused *bridge.NativeFailure
		if errors.As(err, &refused) {
			telemetry.Decide(call, stockpileEditDecision("refused", refused.Value.GetCode().String(), e.Role, map[string]any{"kind": "create", "detail": refused.Value.GetDetail()}))
			continue
		}
		if err != nil {
			return RoundsStockpileResult{}, err
		}
		v := reply.GetEvaluated()
		if v == nil || !v.GetAccepted() {
			telemetry.Decide(call, stockpileEditDecision("refused", "preview_not_accepted", e.Role, map[string]any{"kind": "create", "detail": v.GetReason()}))
			continue
		}
		if _, err = boundary.Context(v.Context, snapshot); err != nil || domain.Tick(v.Context.GetTick()) < tick {
			return RoundsStockpileResult{}, fmt.Errorf("%w: create: err != nil || domain.Tick(v.Context.GetTick()) < tick", ErrControl)
		}
		action, err := domain.NewZoneCreateAction(domain.ActionID(fmt.Sprintf("%s-%d", id, len(actions))), value)
		if err != nil {
			return RoundsStockpileResult{}, err
		}
		actions = append(actions, action)
		previews = append(previews, policy.Preview{Action: action, Snapshot: snapshot, Tick: tick, CanPlace: domain.Known(true), SafeToPlace: domain.Known(true), MadeFromStuff: domain.Known(false), WatchCellsAccessible: domain.Known(true), Footprint: domain.Known(value.Cells()), Costs: domain.Known([]policy.Amount{})})
		admitted = append(admitted, e)
	}
	if len(actions) == 0 {
		return RoundsStockpileResult{Verdict: noSpace("stockpile_zone")}, nil
	}
	plan, err := domain.NewPlan(id, 1, actions)
	if err != nil {
		return RoundsStockpileResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoundsStockpileResult{}, err
	}
	now := r.reviewer.clock.Now()
	if p.session.State() != state || now.Before(started) || now.Sub(started) > r.reviewer.maxAge {
		return RoundsStockpileResult{}, fmt.Errorf("%w: create: p.session.State() != state || now.Before(started) || now.Sub(started) > r.reviewer.maxAge", ErrControl)
	}
	decision, err := admitMethod(call, p.journal, store.BuildingMethodRequest{Owner: goal, Method: method, Plan: plan, Current: snapshot, Tick: tick, Bounds: domain.Known(projection.Bounds), Stock: policy.StockObservation{Snapshot: snapshot, Tick: tick}, Previews: previews, Purpose: policy.Rounds})
	if err != nil {
		return RoundsStockpileResult{}, err
	}
	if !decision.Admitted {
		return RoundsStockpileResult{Verdict: admissionRefused(decision)}, nil
	}
	for _, e := range admitted {
		telemetry.Decide(call, stockpileEditDecision("admitted", "", e.Role, map[string]any{"kind": string(e.Kind), "plan": string(id), "detail": e.Explanation}))
	}
	return RoundsStockpileResult{Verdict: BuildingReasonAdmitted, Plan: id, Edits: len(actions)}, nil
}

// shelfTargetSource reads a shelf's storage settings CAS token.
type shelfTargetSource interface {
	ReadStorageBuildingTarget(context.Context, *c.Identity, string) (bridge.StorageBuildingTarget, bridge.Result, error)
}

// editTargetPresent reports whether the edit's target (the stockpile zone,
// or the shelf for a shelf patch) is still there; false when the source
// cannot read it.
func (r *RoundsStockpilePlanner) editTargetPresent(ctx context.Context, identity *c.Identity, e policy.StockpileEdit) (bool, error) {
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

// stockpileEditDecision is the layout_edit row (family stockpile) of a
// stockpile edit proposed, admitted or refused: target the zone or role,
// reason the refusal code, attrs the edit kind, plan and detail.
func stockpileEditDecision(verdict, reason, target string, attrs map[string]any) telemetry.Decision {
	attrs["family"] = "stockpile"
	return telemetry.Decision{Kind: "layout_edit", Component: "layout", Verdict: verdict, Reason: reason, Target: target, Attrs: attrs}
}
