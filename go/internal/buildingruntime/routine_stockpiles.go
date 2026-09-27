package buildingruntime

import (
	"context"
	"crypto/sha256"
	"errors"
	"fmt"
	"sync"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
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
// claims, their settings superseded by the latest patch of each.
func stockpileRequest(projection *observation.ColonyProjection, owned []store.OwnedZone, patches map[string]store.AppliedStockpile) policy.StockpileRequest {
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
	request := policy.StockpileRequest{Tick: projection.Identity.Tick, Roles: stockpileRoles(projection), Cells: projection.Cells, Bounds: projection.Bounds, Protected: layoutProtected(*projection, nil), Colonists: projection.Facts.Colonists}
	for _, z := range owned {
		entry := byZone[z.ID]
		if z.Kind != domain.StockpileZone || entry == nil {
			continue
		}
		zone := policy.StockpileZone{ID: z.ID, Role: z.Role, Cells: entry.all, Stored: entry.stored, Filter: z.Filter, Priority: z.Priority}
		if patch, ok := patches[z.ID]; ok {
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
	return stockpileRequest(projection, owned, patches), true, nil
}

// RoutineStockpileSource refreshes one zone's presence and CAS token for
// each edit.
type RoutineStockpileSource interface {
	ReadZoneDeleteTarget(context.Context, *c.Identity, string) (bridge.ZoneDeleteTarget, bridge.Result, error)
}

// RoutineStockpilePlanner commits the MaintainStockpiles review's edits
// (#725) as one plan per cycle, one action per zone under that zone's
// fresh CAS token: zone_cell_edit to grow or shrink, stockpile_patch to
// retarget, zone_delete to delete or merge. A plan still open holds the
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
		return nil, ErrControl
	}
	return &RoutineStockpilePlanner{reviewer, native}, nil
}

func (r *RoutineStockpilePlanner) Step(ctx context.Context) (RoutineStockpileResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, false)
	if err != nil {
		return RoutineStockpileResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

func (r *RoutineStockpilePlanner) step(call, epoch context.Context, _ *stepArbiter) (RoutineStockpileResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutineStockpileResult{Reason: BuildingMethodDisabled}, nil
	}
	if !state.ObservationKnown {
		return RoutineStockpileResult{}, ErrControl
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutineStockpileResult{}, err
	}
	if !review.Enabled || review.Snapshot != state.Snapshot {
		return RoutineStockpileResult{Reason: BuildingMethodNoReview}, nil
	}
	var goal store.GoalState
	found := false
	for _, binding := range review.Goals {
		if binding.Need == policy.MaintainStockpiles {
			if goal, err = p.journal.LoadGoal(call, binding.Goal); err != nil {
				return RoutineStockpileResult{}, err
			}
			found = true
			break
		}
	}
	if !found || goal.Goal.Status != domain.GoalActive || goal.Goal.Need != domain.NeedDeficit {
		return RoutineStockpileResult{Reason: BuildingMethodNoDeficit}, nil
	}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoutineStockpileResult{}, err
		}
		if domain.GoalWorkOpen(plan.Progress) {
			return RoutineStockpileResult{Reason: BuildingMethodExistingWork}, nil
		}
	}
	expected, err := routineScope(call, r.reviewer.native)
	if err != nil {
		return RoutineStockpileResult{}, err
	}
	if !routineBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoutineStockpileResult{}, ErrControl
	}
	claims, err := p.journal.ConstructionClaims(call, state.Snapshot, expected.Tick)
	if err != nil {
		return RoutineStockpileResult{}, err
	}
	read, err := r.reviewer.observeOwned(call, r.reviewer.native, expected, claims)
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
	digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%d/%s", goal.Goal.ID, goal.Goal.Epoch, method)))
	id := domain.PlanID(fmt.Sprintf("routine-stockpiles-%x", digest[:16]))
	if _, err := p.journal.LoadPlan(call, id); err == nil {
		return RoutineStockpileResult{Reason: BuildingMethodUsed}, nil
	} else if !errors.Is(err, store.ErrNotFound) {
		return RoutineStockpileResult{}, err
	}
	identity := boundary.Identity(state.Snapshot)
	var actions []domain.Action
	for _, e := range proposal.Edits {
		target, _, err := r.native.ReadZoneDeleteTarget(call, identity, e.Zone)
		if err != nil {
			return RoutineStockpileResult{}, err
		}
		if !target.Present || target.Type != "stockpile" || target.Token == "" {
			continue
		}
		action, err := stockpileEditAction(domain.ActionID(fmt.Sprintf("%s-%d", id, len(actions))), e, target.Token)
		if err != nil {
			clockSchedulerLog("Stockpiles: %s %s dropped: %v", e.Kind, e.Zone, err)
			continue
		}
		actions = append(actions, action)
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
		return RoutineStockpileResult{}, ErrControl
	}
	if _, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, method, plan); err != nil {
		return RoutineStockpileResult{}, err
	}
	for _, e := range proposal.Edits {
		clockEvent(call, "layout", "stockpiles", "stockpile edit admitted: "+e.Explanation, "zone", e.Zone, "kind", string(e.Kind), "plan", string(id))
	}
	return RoutineStockpileResult{Reason: BuildingMethodAdmitted, Plan: id, Edits: len(actions)}, nil
}

// stockpileEditAction is one edit's action under the zone's token.
func stockpileEditAction(id domain.ActionID, e policy.StockpileEdit, token string) (domain.Action, error) {
	switch e.Kind {
	case policy.StockpileGrow, policy.StockpileShrink:
		mode := domain.AddZoneCells
		if e.Kind == policy.StockpileShrink {
			mode = domain.RemoveZoneCells
		}
		edit, err := domain.NewZoneCellEdit(e.Zone, token, mode, e.Cells)
		if err != nil {
			return domain.Action{}, err
		}
		return domain.NewZoneCellEditAction(id, edit)
	case policy.StockpileRetarget:
		patch, err := domain.NewStockpilePatch(domain.StorageZoneTarget, e.Zone, token, e.Filter, e.Priority, e.Role)
		if err != nil {
			return domain.Action{}, err
		}
		return domain.NewStockpilePatchAction(id, patch)
	case policy.StockpileDelete, policy.StockpileMerge:
		del, err := domain.NewZoneDelete(e.Zone, token)
		if err != nil {
			return domain.Action{}, err
		}
		return domain.NewZoneDeleteAction(id, del)
	}
	return domain.Action{}, fmt.Errorf("unknown stockpile edit %q", e.Kind)
}
