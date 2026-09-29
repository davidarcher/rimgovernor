package buildingruntime

import (
	"context"
	"crypto/sha256"
	"errors"
	"fmt"
	"sort"

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
		return nil, fmt.Errorf("%w: NewRoutineFoodStoragePlanner: reviewer == nil || native == nil", ErrControl)
	}
	return &RoutineFoodStoragePlanner{reviewer: reviewer, native: native}, nil
}

// step zones food storage on any roofed floor (a non-bedroom room first,
// then the starter shelter, then any roofed cell) and, before any roofed
// floor exists, on an outdoor block beside the cooking bench. A starter room
// full of sleeping spots never blocks it.
func (r *RoutineFoodStoragePlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoutineFoodStorageResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutineFoodStorageResult{Reason: BuildingMethodDisabled}, nil
	}
	if !state.ObservationKnown {
		return RoutineFoodStorageResult{}, fmt.Errorf("%w: step: !state.ObservationKnown", ErrControl)
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutineFoodStorageResult{}, err
	}
	if !review.Enabled || review.Snapshot != state.Snapshot {
		return RoutineFoodStorageResult{Reason: BuildingMethodNoReview}, nil
	}
	goal, workable, err := p.journal.Workable(call, review, policy.MaintainFoodStorage)
	if err != nil {
		return RoutineFoodStorageResult{}, err
	}
	if !workable {
		return RoutineFoodStorageResult{Reason: BuildingMethodNoDeficit}, nil
	}
	if goal.Goal.Priority >= 3 {
		selected := false
		for _, row := range review.Development.Rows {
			selected = selected || row.Goal == policy.MaintainFoodStorage && row.Selected
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
		if store.PlanOpen(plan) {
			return RoutineFoodStorageResult{Reason: BuildingMethodExistingWork}, nil
		}
	}
	expected, err := routineScope(call, r.reviewer.native)
	if err != nil {
		return RoutineFoodStorageResult{}, err
	}
	if !routineBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoutineFoodStorageResult{}, fmt.Errorf("%w: step: !routineBuildingBoundary(expected, state.Snapshot, review.Tick)", ErrControl)
	}
	claims, err := p.journal.ConstructionClaims(call, state.Snapshot, expected.Tick)
	if err != nil {
		return RoutineFoodStorageResult{}, err
	}
	room, roomKnown := starterRoom(claims)
	read, err := r.reviewer.observeOwned(call, r.reviewer.native, expected, claims)
	if err != nil {
		return RoutineFoodStorageResult{}, err
	}
	projection := read.Projection
	// MaintainFoodStorage also stands open for the larder and reserve; this
	// planner places only the food stockpile a colony without one needs.
	if storage, known := projection.Facts.FoodStorage.Value(); known && storage {
		return RoutineFoodStorageResult{Reason: BuildingMethodNoDeficit}, nil
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
	claimRows, _ := claims.Value()
	for _, cell := range shellInteriors(nil, claimRows) {
		// A shell's floor belongs to its own furniture until it is roofed;
		// the roofed tiers below still reach it through the roof check.
		if c, ok := siteCells[cell]; ok {
			if roofed, rk := c.Roofed.Value(); !rk || !roofed {
				occupied[cell] = true
			}
		}
	}
	anchor, anchored := foodStorageAnchor(claimRows, room, roomKnown)
	priority := domain.ImportantPriority
	sleeping := policy.SleepingRoomCells(projection.Rooms)
	// Any roofed room that is not a bedroom first, then the starter shelter
	// (its canonical back-of-room block), then any other roofed floor.
	sites := foodStorageBlocks(siteCells, occupied, anchor, func(c policy.SiteCell) bool {
		return roofedIndoors(c) && !sleeping[c.Cell]
	})
	if roomKnown {
		sites = append(sites, foodStorageSites(room, siteCells, occupied)...)
	}
	sites = append(sites, foodStorageBlocks(siteCells, occupied, anchor, roofedIndoors)...)
	if len(sites) == 0 && anchored && len(goal.Methods) == 0 {
		// No roofed floor yet: an outdoor zone beside the cooking spot
		// consolidates food early. It stays below the indoor zone's
		// priority, so haulers carry the food indoors once one exists; the
		// goal (nine roofed cells) stays open until then.
		priority = domain.PreferredPriority
		sites = foodStorageBlocks(siteCells, occupied, anchor, func(policy.SiteCell) bool { return true })
	}
	if len(sites) > maxFoodStorageSites {
		sites = sites[:maxFoodStorageSites]
	}
	if len(sites) == 0 {
		return RoutineFoodStorageResult{Reason: BuildingMethodNoSpace}, nil
	}
	// The census cannot see everything native refuses (a pawn or a stack
	// that landed after the read), so each candidate is previewed in turn
	// and a refused site gives way to the next (#223).
	var cells []domain.Cell
	var id domain.PlanID
	var method domain.MethodID
	var snapshot domain.GenerationSnapshot
	var plan domain.PlanSpec
	var preview policy.Preview
	accepted := false
	for _, candidate := range sites {
		value, err := domain.NewFilteredStockpileZone(domain.FoodFilter(), priority, candidate)
		if err != nil {
			return RoutineFoodStorageResult{}, err
		}
		hash := sha256.Sum256([]byte(fmt.Sprintf("%v", candidate)))
		method = domain.MethodID(fmt.Sprintf("food-storage-%x", hash[:16]))
		if _, err = p.journal.LoadGoalMethod(call, goal.Goal.ID, goal.Goal.Epoch, method); err == nil {
			return RoutineFoodStorageResult{Reason: BuildingMethodUsed}, nil
		} else if !errors.Is(err, store.ErrNotFound) {
			return RoutineFoodStorageResult{}, err
		}
		id = domain.MintPlanID()
		snapshot = state.Snapshot
		snapshot.Plan = id
		snapshot.Revision = 1
		action, err := domain.NewZoneCreateAction(domain.ActionID(fmt.Sprintf("%s-0", id)), value)
		if err != nil {
			return RoutineFoodStorageResult{}, err
		}
		reply, _, err := r.native.PreviewZone(call, boundary.Identity(snapshot), value)
		if err != nil {
			return RoutineFoodStorageResult{}, err
		}
		v := reply.GetEvaluated()
		if v == nil {
			return RoutineFoodStorageResult{}, fmt.Errorf("%w: step: v == nil", ErrControl)
		}
		if !v.GetAccepted() {
			continue
		}
		if _, err = boundary.Context(v.Context, snapshot); err != nil || domain.Tick(v.Context.GetTick()) < projection.Identity.Tick {
			return RoutineFoodStorageResult{}, fmt.Errorf("%w: step: err != nil || domain.Tick(v.Context.GetTick()) < projection.Identity.Tick", ErrControl)
		}
		cells = candidate
		preview = policy.Preview{Action: action, Snapshot: snapshot, Tick: projection.Identity.Tick, CanPlace: domain.Known(true), SafeToPlace: domain.Known(true), MadeFromStuff: domain.Known(false), WatchCellsAccessible: domain.Known(true), Footprint: domain.Known(cells), Costs: domain.Known([]policy.Amount{})}
		if plan, err = domain.NewPlan(id, 1, []domain.Action{action}); err != nil {
			return RoutineFoodStorageResult{}, err
		}
		accepted = true
		break
	}
	if !accepted {
		return RoutineFoodStorageResult{Reason: BuildingMethodRefused}, nil
	}
	if err = p.current(call, epoch); err != nil {
		return RoutineFoodStorageResult{}, err
	}
	if p.session.State() != state {
		return RoutineFoodStorageResult{}, fmt.Errorf("%w: step: p.session.State() != state", ErrControl)
	}
	actual, err := routineScope(call, r.reviewer.native)
	if err != nil || !routineBuildingBoundary(actual, state.Snapshot, projection.Identity.Tick) {
		return RoutineFoodStorageResult{}, fmt.Errorf("%w: step: err != nil || !routineBuildingBoundary(actual, state.Snapshot, projection.Identity.Tick)", ErrControl)
	}
	now := r.reviewer.clock.Now()
	if now.Before(read.StartedAt) || now.Sub(read.StartedAt) > r.reviewer.maxAge {
		return RoutineFoodStorageResult{}, observation.ErrStale
	}
	decision, err := admitMethod(call, p.journal, store.BuildingMethodRequest{Goal: goal.Goal.ID, Revision: goal.Revision, Method: method, Plan: plan, Current: snapshot, Tick: projection.Identity.Tick, Bounds: domain.Known(projection.Bounds), Stock: policy.StockObservation{Snapshot: snapshot, Tick: projection.Identity.Tick}, Previews: []policy.Preview{preview}, Purpose: policy.Routine})
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
// exist the site-legality scan would no longer treat that ground as free,
// so the built room can only be identified by what is there: the plan whose
// Wall/Door claims are exactly one rectangle's ring.
func starterRoom(claims domain.Fact[[]policy.ConstructionClaim]) (policy.Rectangle, bool) {
	rows, known := claims.Value()
	if !known {
		return policy.Rectangle{}, false
	}
	type ring struct {
		cells map[domain.Cell]bool
		doors []domain.Cell
	}
	byPlan := map[domain.PlanID]*ring{}
	for _, claim := range rows {
		def := claim.Building.Definition()
		if def != "Wall" && def != "Door" {
			continue
		}
		r := byPlan[claim.Plan]
		if r == nil {
			r = &ring{cells: map[domain.Cell]bool{}}
			byPlan[claim.Plan] = r
		}
		r.cells[claim.Building.Cell()] = true
		if def == "Door" {
			r.doors = append(r.doors, claim.Building.Cell())
		}
	}
	plans := make([]domain.PlanID, 0, len(byPlan))
	for id := range byPlan {
		plans = append(plans, id)
	}
	sort.Slice(plans, func(i, j int) bool { return plans[i] < plans[j] })
	// Every shell stands on a planned rectangle (#1231): any plan whose
	// Wall/Door claims are exactly a door-bearing rectangle's ring is the
	// room.
	for _, id := range plans {
		if b, ok := rectangleRing(byPlan[id].cells, len(byPlan[id].doors) > 0); ok {
			return b, true
		}
	}
	return policy.Rectangle{}, false
}

// rectangleRing is the bounds of cells when they are exactly the full ring
// of a rectangle at least 5x5 (a 3x3 interior) and door is set.
func rectangleRing(cells map[domain.Cell]bool, door bool) (policy.Rectangle, bool) {
	if !door || len(cells) == 0 {
		return policy.Rectangle{}, false
	}
	first := true
	var minX, minZ, maxX, maxZ int32
	for c := range cells {
		if first {
			minX, minZ, maxX, maxZ, first = c.X, c.Z, c.X, c.Z, false
			continue
		}
		minX, minZ, maxX, maxZ = min(minX, c.X), min(minZ, c.Z), max(maxX, c.X), max(maxZ, c.Z)
	}
	w, h := maxX-minX+1, maxZ-minZ+1
	if w < 5 || h < 5 || len(cells) != int(2*(w+h)-4) {
		return policy.Rectangle{}, false
	}
	for c := range cells {
		if c.X != minX && c.X != maxX && c.Z != minZ && c.Z != maxZ {
			return policy.Rectangle{}, false
		}
	}
	return policy.Rectangle{X: minX, Z: minZ, Width: w, Height: h}, true
}

const maxFoodStorageSites = 8

func roofedIndoors(c policy.SiteCell) bool {
	indoors, ik := c.Indoors.Value()
	roofed, rk := c.Roofed.Value()
	return ik && indoors && rk && roofed
}

// foodStorageCookingDefinitions are the cooking benches an outdoor food
// zone sits beside.
var foodStorageCookingDefinitions = map[string]bool{"Campfire": true, "FueledStove": true, "ElectricStove": true}

// foodStorageAnchor is the cell food storage clusters around: the colony's
// cooking bench when one is claimed, else the starter shelter's middle.
func foodStorageAnchor(claims []policy.ConstructionClaim, room policy.Rectangle, roomKnown bool) (domain.Cell, bool) {
	for _, claim := range claims {
		if foodStorageCookingDefinitions[claim.Building.Definition()] {
			return claim.Building.Cell(), true
		}
	}
	if roomKnown {
		return domain.Cell{X: room.X + room.Width/2, Z: room.Z + room.Height/2}, true
	}
	return domain.Cell{}, false
}

// foodStorageBlocks lists up to maxFoodStorageSites free 3x3 blocks whose
// every cell passes allow, nearest anchor first. Free is the starter-room
// search's ground test without its roof requirement.
func foodStorageBlocks(cells map[domain.Cell]policy.SiteCell, occupied map[domain.Cell]bool, anchor domain.Cell, allow func(policy.SiteCell) bool) [][]domain.Cell {
	free := func(cell domain.Cell) bool {
		if occupied[cell] {
			return false
		}
		c, known := cells[cell]
		if !known || !allow(c) {
			return false
		}
		walkable, wk := c.Walkable.Value()
		taken, ok := c.Occupied.Value()
		zoned, zk := c.Zone.Value()
		empty, ek := c.StorageEmpty.Value()
		return wk && walkable && ok && !taken && zk && !zoned && ek && empty
	}
	type candidate struct {
		dist  int64
		block []domain.Cell
	}
	var candidates []candidate
	for origin := range cells {
		block := make([]domain.Cell, 0, 9)
	scan:
		for dx := int32(0); dx < 3; dx++ {
			for dz := int32(0); dz < 3; dz++ {
				cell := domain.Cell{X: origin.X + dx, Z: origin.Z + dz}
				if !free(cell) {
					break scan
				}
				block = append(block, cell)
			}
		}
		if len(block) != 9 {
			continue
		}
		dx, dz := int64(origin.X+1-anchor.X), int64(origin.Z+1-anchor.Z)
		candidates = append(candidates, candidate{dx*dx + dz*dz, block})
	}
	sort.Slice(candidates, func(i, j int) bool {
		a, b := candidates[i], candidates[j]
		if a.dist != b.dist {
			return a.dist < b.dist
		}
		if a.block[0].X != b.block[0].X {
			return a.block[0].X < b.block[0].X
		}
		return a.block[0].Z < b.block[0].Z
	})
	var sites [][]domain.Cell
	for _, c := range candidates {
		if len(sites) == maxFoodStorageSites {
			break
		}
		sites = append(sites, c.block)
	}
	return sites
}

// shellInteriors lists every cell inside the bounds of each wall ring a
// plan raises, built or not: the floor a shell encloses (or will enclose
// once its walls stand) belongs to the room's own furniture, and a planner
// that only avoids the walls' footprints would site over it (#217: the first
// field patch was laid across the hut's interior while the shell still
// waited for wood, and the stockpile and sleeping spots then found the
// room already zoned). Completed claims alone are too late for that, so the
// rings come from the shelter goal's plans while it is open, and from the
// completed claims once it is served (the goal then leaves the review, and
// the next field batch was drawn inside the finished hut).
func shellInteriors(plans []store.PlanState, claims []policy.ConstructionClaim) []domain.Cell {
	type bounds struct {
		minX, minZ, maxX, maxZ int32
	}
	byPlan := map[domain.PlanID]*bounds{}
	extend := func(id domain.PlanID, building domain.Building) {
		if def := building.Definition(); def != "Wall" && def != "Door" {
			return
		}
		cell := building.Cell()
		b := byPlan[id]
		if b == nil {
			byPlan[id] = &bounds{cell.X, cell.Z, cell.X, cell.Z}
			return
		}
		b.minX, b.minZ, b.maxX, b.maxZ = min(b.minX, cell.X), min(b.minZ, cell.Z), max(b.maxX, cell.X), max(b.maxZ, cell.Z)
	}
	for _, plan := range plans {
		id := plan.Spec.ID()
		for _, progress := range plan.Progress {
			building, isBuilding := progress.Action().Building()
			if !isBuilding || progress.View().Stage == domain.Cancelled {
				continue
			}
			extend(id, building)
		}
	}
	for _, claim := range claims {
		extend(claim.Plan, claim.Building)
	}
	ids := make([]domain.PlanID, 0, len(byPlan))
	for id := range byPlan {
		ids = append(ids, id)
	}
	sort.Slice(ids, func(i, j int) bool { return ids[i] < ids[j] })
	var cells []domain.Cell
	for _, id := range ids {
		b := byPlan[id]
		for x := b.minX; x <= b.maxX; x++ {
			for z := b.minZ; z <= b.maxZ; z++ {
				cells = append(cells, domain.Cell{X: x, Z: z})
			}
		}
	}
	return cells
}

// foodStorageSites prefers the same back-of-room 3x3 spot PlannedLayout
// reserves for this room (Storage: {room.X+3, room.Z+5, 3, 3} on the 9x9;
// the same upper-middle patch of any other shell's bounds), then searches
// outward. Smaller blocks are never offered: the colony's food-storage fact
// needs nine roofed cells in one zone, so a stockpile shrunk onto whatever
// floor is left never satisfies the goal and each new one is a fresh method
// (#217: eleven single-cell stockpiles filled the hut around a field
// patch). A cell must observe empty storage as well as free ground: a stack
// dropped on the floor refuses the zone natively. The result is the bounded,
// ordered list of legal blocks for the planner to preview in turn.
func foodStorageSites(room policy.Rectangle, cells map[domain.Cell]policy.SiteCell, occupied map[domain.Cell]bool) [][]domain.Cell {
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
		empty, ek := c.StorageEmpty.Value()
		return ik && indoors && rk && roofed && wk && walkable && ok && !unoccupied && zk && !unzoned && ek && empty
	}
	var sites [][]domain.Cell
	target := domain.Cell{X: room.X + room.Width/2 - 1, Z: room.Z + room.Height/2 + 1}
	type candidate struct {
		dist int64
		x, z int32
	}
	for _, size := range []int32{3} {
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
				sites = append(sites, block)
				if len(sites) == maxFoodStorageSites {
					return sites
				}
			}
		}
	}
	return sites
}
