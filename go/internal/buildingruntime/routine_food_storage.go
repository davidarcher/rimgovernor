package buildingruntime

import (
	"context"
	"crypto/sha256"
	"errors"
	"fmt"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

type RoutineFoodStoragePlanner struct {
	reviewer *RoutineReviewer
	native   FieldNative
}
type RoutineFoodStorageResult struct {
	Reason RoutineBuildingReason
	Plan   domain.PlanID
}

func NewRoutineFoodStoragePlanner(reviewer *RoutineReviewer, native FieldNative) (*RoutineFoodStoragePlanner, error) {
	if reviewer == nil || native == nil {
		return nil, ErrControl
	}
	return &RoutineFoodStoragePlanner{reviewer: reviewer, native: native}, nil
}
func (r *RoutineFoodStoragePlanner) Step(ctx context.Context) (RoutineFoodStorageResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, false)
	if err != nil {
		return RoutineFoodStorageResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

// step furnishes the same starter shell EnsureInitialShelter already built,
// rather than selecting or building a new room: the player-selected shelter
// handoff (backlog row 894) is not composed yet, so this slice only closes the
// narrower starter-room fallback.
func (r *RoutineFoodStoragePlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoutineFoodStorageResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutineFoodStorageResult{Reason: BuildingMethodDisabled}, nil
	}
	if !state.ObservationKnown {
		return RoutineFoodStorageResult{}, ErrControl
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutineFoodStorageResult{}, err
	}
	if !review.Enabled || review.Snapshot != state.Snapshot {
		return RoutineFoodStorageResult{Reason: BuildingMethodNoReview}, nil
	}
	var goal store.GoalState
	found := false
	for _, binding := range review.Goals {
		if binding.Need == policy.EnsureFoodStorage {
			goal, err = p.journal.LoadGoal(call, binding.Goal)
			found = true
			break
		}
	}
	if err != nil {
		return RoutineFoodStorageResult{}, err
	}
	if !found || goal.Goal.Status != domain.GoalActive || goal.Goal.Need != domain.NeedDeficit {
		return RoutineFoodStorageResult{Reason: BuildingMethodNoDeficit}, nil
	}
	if goal.Goal.Priority >= 3 {
		selected := false
		for _, row := range review.Development.Rows {
			selected = selected || row.Goal == policy.EnsureFoodStorage && row.Selected
		}
		if !selected {
			return RoutineFoodStorageResult{Reason: BuildingMethodRefused}, nil
		}
	}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoutineFoodStorageResult{}, err
		}
		if domain.GoalWorkOpen(plan.Progress) {
			return RoutineFoodStorageResult{Reason: BuildingMethodExistingWork}, nil
		}
	}
	identity, _, err := r.reviewer.native.Identity(call)
	if err != nil {
		return RoutineFoodStorageResult{}, err
	}
	expected, err := observation.DecodeIdentity(identity)
	if err != nil {
		return RoutineFoodStorageResult{}, err
	}
	if !routineBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoutineFoodStorageResult{}, ErrControl
	}
	claims, err := p.journal.ConstructionClaims(call, state.Snapshot, expected.Tick)
	if err != nil {
		return RoutineFoodStorageResult{}, err
	}
	room, known := starterRoom(claims)
	if !known {
		return RoutineFoodStorageResult{Reason: BuildingMethodNoSpace}, nil
	}
	read, err := observation.ObserveRoutineOwned(call, r.reviewer.native, r.reviewer.clock, expected, r.reviewer.maxAge, claims)
	if err != nil {
		return RoutineFoodStorageResult{}, err
	}
	projection := read.Projection
	token, known := projection.ZoneMapToken.Value()
	if !known {
		return RoutineFoodStorageResult{Reason: BuildingMethodUnknown}, nil
	}
	held, err := p.journal.BuildingReservations(call, state.Snapshot)
	if err != nil {
		return RoutineFoodStorageResult{}, err
	}
	occupied := map[domain.Cell]bool{}
	for _, h := range held {
		for _, cell := range h.Footprint {
			occupied[cell] = true
		}
	}
	siteCells := map[domain.Cell]policy.SiteCell{}
	for _, cell := range projection.Cells {
		siteCells[cell.Cell] = cell
	}
	cells, known := foodStorageCells(room, siteCells, occupied)
	if !known {
		return RoutineFoodStorageResult{Reason: BuildingMethodNoSpace}, nil
	}
	value, err := domain.NewStockpileZone(domain.FoodPreset, domain.ImportantPriority, cells)
	if err != nil {
		return RoutineFoodStorageResult{}, err
	}
	hash := sha256.Sum256([]byte(fmt.Sprintf("%v", cells)))
	method := domain.MethodID(fmt.Sprintf("food-storage-%x", hash[:16]))
	if _, err = p.journal.LoadGoalMethod(call, goal.Goal.ID, goal.Goal.Epoch, method); err == nil {
		return RoutineFoodStorageResult{Reason: BuildingMethodUsed}, nil
	} else if !errors.Is(err, store.ErrNotFound) {
		return RoutineFoodStorageResult{}, err
	}
	digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%d/%s", goal.Goal.ID, goal.Goal.Epoch, method)))
	id := domain.PlanID(fmt.Sprintf("routine-food-storage-%x", digest[:16]))
	snapshot := state.Snapshot
	snapshot.Plan = id
	snapshot.Revision = 1
	action, err := domain.NewZoneCreateAction(domain.ActionID(fmt.Sprintf("%s-0", id)), value)
	if err != nil {
		return RoutineFoodStorageResult{}, err
	}
	reply, _, err := r.native.PreviewZone(call, boundary.Identity(snapshot), bridge.ZoneTarget{Zone: value, Token: token})
	if err != nil {
		return RoutineFoodStorageResult{}, err
	}
	v := reply.GetEvaluated()
	if v == nil || !v.GetAccepted() {
		return RoutineFoodStorageResult{Reason: BuildingMethodRefused}, nil
	}
	if _, err = boundary.Context(v.Context, snapshot); err != nil || domain.Tick(v.Context.GetTick()) != projection.Identity.Tick {
		return RoutineFoodStorageResult{}, ErrControl
	}
	preview := policy.Preview{Action: action, Snapshot: snapshot, Tick: projection.Identity.Tick, CanPlace: domain.Known(true), SafeToPlace: domain.Known(true), MadeFromStuff: domain.Known(false), WatchCellsAccessible: domain.Known(true), Footprint: domain.Known(cells), Costs: domain.Known([]policy.Amount{})}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoutineFoodStorageResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutineFoodStorageResult{}, err
	}
	if p.session.State() != state {
		return RoutineFoodStorageResult{}, ErrControl
	}
	last, _, err := r.reviewer.native.Identity(call)
	if err != nil {
		return RoutineFoodStorageResult{}, err
	}
	actual, err := observation.DecodeIdentity(last)
	if err != nil || !routineBuildingBoundary(actual, state.Snapshot, projection.Identity.Tick) {
		return RoutineFoodStorageResult{}, ErrControl
	}
	now := r.reviewer.clock.Now()
	if now.Before(read.StartedAt) || now.Sub(read.StartedAt) > r.reviewer.maxAge {
		return RoutineFoodStorageResult{}, observation.ErrStale
	}
	decision, err := p.journal.AdmitBuildingMethod(call, store.BuildingMethodRequest{Goal: goal.Goal.ID, Revision: goal.Revision, Method: method, Plan: plan, Current: snapshot, Tick: projection.Identity.Tick, Bounds: domain.Known(projection.Bounds), Stock: policy.StockObservation{Snapshot: snapshot, Tick: projection.Identity.Tick}, Rules: r.reviewer.rules, Previews: []policy.Preview{preview}, Purpose: policy.Routine})
	if err != nil {
		return RoutineFoodStorageResult{}, err
	}
	reason := BuildingMethodRefused
	if decision.Admitted {
		reason = BuildingMethodAdmitted
	}
	return RoutineFoodStorageResult{Reason: reason, Plan: id}, nil
}

// starterRoom recovers the completed starter shell's footprint from durable
// construction claims rather than recomputing candidate sites: once walls
// exist, StarterLayouts' own site-legality scan would no longer treat that
// ground as free, so the built room can only be identified by what is there.
func starterRoom(claims domain.Fact[[]policy.ConstructionClaim]) (policy.Rectangle, bool) {
	rows, known := claims.Value()
	if !known {
		return policy.Rectangle{}, false
	}
	byPlan := map[domain.PlanID][]domain.Cell{}
	for _, claim := range rows {
		def := claim.Building.Definition()
		if def != "Wall" && def != "Door" {
			continue
		}
		byPlan[claim.Plan] = append(byPlan[claim.Plan], claim.Building.Cell())
	}
	plans := make([]domain.PlanID, 0, len(byPlan))
	for id := range byPlan {
		plans = append(plans, id)
	}
	sort.Slice(plans, func(i, j int) bool { return plans[i] < plans[j] })
	for _, id := range plans {
		cells := byPlan[id]
		if len(cells) != 32 {
			continue
		}
		minX, minZ, maxX, maxZ := cells[0].X, cells[0].Z, cells[0].X, cells[0].Z
		for _, cell := range cells {
			minX, maxX = min(minX, cell.X), max(maxX, cell.X)
			minZ, maxZ = min(minZ, cell.Z), max(maxZ, cell.Z)
		}
		if maxX-minX != 8 || maxZ-minZ != 8 {
			continue
		}
		return policy.Rectangle{X: minX, Z: minZ, Width: 9, Height: 9}, true
	}
	return policy.Rectangle{}, false
}

// foodStorageCells prefers the same back-of-room 3x3 spot StarterLayouts
// reserves for this room (Storage: {room.X+3, room.Z+5, 3, 3}), then searches
// outward and shrinks toward single free cells only if that spot is occupied
// by something the starter layout's own reservation did not anticipate.
func foodStorageCells(room policy.Rectangle, cells map[domain.Cell]policy.SiteCell, occupied map[domain.Cell]bool) ([]domain.Cell, bool) {
	free := func(cell domain.Cell) bool {
		if occupied[cell] {
			return false
		}
		c, known := cells[cell]
		if !known {
			return false
		}
		indoors, ik := c.Indoors.Value()
		roofed, rk := c.Roofed.Value()
		walkable, wk := c.Walkable.Value()
		unoccupied, ok := c.Occupied.Value()
		unzoned, zk := c.Zone.Value()
		return ik && indoors && rk && roofed && wk && walkable && ok && !unoccupied && zk && !unzoned
	}
	target := domain.Cell{X: room.X + 3, Z: room.Z + 5}
	type candidate struct {
		dist int64
		x, z int32
	}
	for _, size := range []int32{3, 2, 1} {
		var candidates []candidate
		for x := room.X + 1; x+size <= room.X+room.Width-1; x++ {
			for z := room.Z + 1; z+size <= room.Z+room.Height-1; z++ {
				dx, dz := int64(x-target.X), int64(z-target.Z)
				candidates = append(candidates, candidate{dx*dx + dz*dz, x, z})
			}
		}
		sort.Slice(candidates, func(i, j int) bool {
			if candidates[i].dist != candidates[j].dist {
				return candidates[i].dist < candidates[j].dist
			}
			if candidates[i].x != candidates[j].x {
				return candidates[i].x < candidates[j].x
			}
			return candidates[i].z < candidates[j].z
		})
		for _, cand := range candidates {
			var block []domain.Cell
			legal := true
			for dx := int32(0); dx < size && legal; dx++ {
				for dz := int32(0); dz < size && legal; dz++ {
					cell := domain.Cell{X: cand.x + dx, Z: cand.z + dz}
					if !free(cell) {
						legal = false
						break
					}
					block = append(block, cell)
				}
			}
			if legal {
				return block, true
			}
		}
	}
	return nil, false
}
