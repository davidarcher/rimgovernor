package buildingruntime

import (
	"context"
	"fmt"
	"os"
	"strconv"
	"strings"
	"sync"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/snapshot"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

// MaintainFirebreak (#1548): each review classifies the firebreak ring's
// ground from defense-site reads, plans the ring (policy.PlanFirebreak) and
// reads the plant cut census over its cut cells; the goal is in deficit
// while a cut cell holds a standing plant or a wooden ruin stands
// undesignated. The planner orders one area cut over those cells and
// deconstructs up to maxDefenseCoverBatch ruins through cover clearance,
// at most maxFirebreakAttempts methods per game day so a player who keeps
// undesignating does not cause a loop.
const (
	maxFirebreakAttempts = 4
	firebreakWindowTicks = 60000
	firebreakPrefix      = "firebreak-"
	// firebreakTile is the side of one defense-site read: the ring's
	// bounding box is read in tiles, only those holding ring cells.
	firebreakTile = 32
)

// RoutineFirebreakSource is the native reads the firebreak review takes.
type RoutineFirebreakSource interface {
	ReadDefenseSite(context.Context, *c.Identity, bridge.CellRect) (bridge.DefenseSite, bridge.Result, error)
	ReadPlantCutCensus(context.Context, *c.Identity, []domain.Cell) (bridge.PlantCutCensus, bridge.Result, error)
}

// firebreakMemory is the firebreak review's state, in memory only (#1536):
// the dwell clock (each ring cell's first tick in the ring) and the latest
// review's work, ruins and pave cells for one world. A new world (start,
// reload) restarts the clock.
type firebreakMemory struct {
	native RoutineFirebreakSource
	mu     sync.Mutex
	world  string
	dwell  map[domain.Cell]domain.Tick
	tick   domain.Tick
	work   policy.FirebreakWork
	ruins  map[domain.Cell]domain.CoverClearance
	pave   []domain.Cell
}

func (m *firebreakMemory) enter(world string) {
	if m.world != world {
		m.world, m.dwell, m.work, m.ruins, m.pave = world, nil, policy.FirebreakWork{}, nil, nil
	}
}

// Pave is the latest review's pave cells still natural ground with no
// building ordered on them: MaintainFlooring's firebreak tier.
func (m *firebreakMemory) Pave() []domain.Cell {
	m.mu.Lock()
	defer m.mu.Unlock()
	return append([]domain.Cell(nil), m.pave...)
}

// take returns the work the review at tick left for world.
func (m *firebreakMemory) take(world string, tick domain.Tick) (policy.FirebreakWork, map[domain.Cell]domain.CoverClearance, bool) {
	m.mu.Lock()
	defer m.mu.Unlock()
	if m.world != world || m.tick != tick {
		return policy.FirebreakWork{}, nil, false
	}
	return m.work, m.ruins, true
}

// firebreakRequest is the ring request the projection supplies: the
// footprint inputs, the growing zones and the layout plan's footprint.
func firebreakRequest(projection observation.ColonyProjection) policy.FirebreakRequest {
	farms := map[string]bool{}
	for _, f := range projection.Farms {
		farms[f.ID] = true
	}
	zones := []domain.Cell{}
	for _, cell := range projection.Cells {
		if id, ok := cell.ZoneID.Value(); ok && farms[id] {
			zones = append(zones, cell.Cell)
		}
	}
	f := projection.Facts
	r := policy.FirebreakRequest{Bounds: domain.Known(projection.Bounds), Construction: f.CurrentConstruction, Claims: f.ConstructionClaims, Home: f.HomeCoverage, GrowingZones: domain.Known(zones), Now: projection.Identity.Tick}
	planned := []domain.Cell{}
	if plan, ok := projection.LayoutPlan.Value(); ok {
		planned = layoutFootprint(plan)
	}
	r.Planned = domain.Known(planned)
	return r
}

// layoutFootprint is the accepted layout plan's built footprint: every
// room with its walls and every hallway run.
func layoutFootprint(plan policy.LayoutPlan) []domain.Cell {
	var out []domain.Cell
	for _, room := range plan.AllRooms() {
		in := room.Interior
		for x := in.X - 1; x <= in.X+in.Width; x++ {
			for z := in.Z - 1; z <= in.Z+in.Height; z++ {
				out = append(out, domain.Cell{X: x, Z: z})
			}
		}
	}
	for _, s := range plan.Hallways() {
		for x := min(s.From.X, s.To.X); x <= max(s.From.X, s.To.X); x++ {
			for z := min(s.From.Z, s.To.Z); z <= max(s.From.Z, s.To.Z); z++ {
				out = append(out, domain.Cell{X: x, Z: z})
			}
		}
	}
	return out
}

// firebreakGround classifies one defense-site cell. A fogged cell is
// unreachable rock as far as the ring is concerned.
func firebreakGround(cell bridge.DefenseCell) policy.FirebreakGround {
	switch {
	case cell.Fogged || cell.NaturalRock:
		return policy.FirebreakNaturalRock
	case strings.Contains(cell.Terrain, "Water"):
		return policy.FirebreakWater
	case cell.EdificeDefName == "":
		return policy.FirebreakOpen
	case cell.PlayerOwned:
		return policy.FirebreakPlayerEdifice
	case cell.EdificeStuff == "WoodLog":
		return policy.FirebreakWoodenRuin
	}
	return policy.FirebreakStoneRuin
}

// firebreakTiles covers cells' bounding box with firebreakTile squares and
// keeps those holding a cell.
func firebreakTiles(cells []domain.Cell) []bridge.CellRect {
	held := map[domain.Cell]bool{}
	var out []bridge.CellRect
	for _, cell := range cells {
		key := domain.Cell{X: cell.X / firebreakTile, Z: cell.Z / firebreakTile}
		if held[key] {
			continue
		}
		held[key] = true
		out = append(out, bridge.CellRect{Min: domain.Cell{X: key.X * firebreakTile, Z: key.Z * firebreakTile}})
	}
	return out
}

// firebreakClaimed is the material queued construction claims, priced by
// each claimed definition's planning cost list; an unpriced one adds none.
func firebreakClaimed(projection observation.ColonyProjection) map[policy.Resource]int64 {
	costs := map[string][]policy.Amount{}
	for _, d := range projection.Definitions {
		if v, ok := d.Costs.Value(); ok {
			costs[d.Name] = v
		}
	}
	claimed := map[policy.Resource]int64{}
	claims, _ := projection.Facts.ConstructionClaims.Value()
	for _, claim := range claims {
		for _, a := range costs[claim.Building.Definition()] {
			claimed[a.Resource] += a.Count
		}
	}
	return claimed
}

// review plans the ring for the projection and reports whether work is
// owed; unknown when the ring or its ground is. busy are the cells an open
// building action targets, held out of the pave cells.
func (m *firebreakMemory) review(ctx context.Context, identity *c.Identity, current domain.GenerationSnapshot, projection observation.ColonyProjection, stage policy.ColonyStage, p policy.FlooringPolicy, busy map[domain.Cell]bool) (domain.Fact[bool], error) {
	// A colony still at its Foothold has no shelter, fields or stores worth a
	// ring yet, and its few hands are better spent on them: nothing is owed.
	if stage == policy.StageFoothold {
		return domain.Known(false), nil
	}
	world := stockpileWorld(current)
	tick := projection.Identity.Tick
	request := firebreakRequest(projection)
	ring, err := policy.FirebreakRing(request)
	if err != nil {
		return domain.Unknown[bool](), err
	}
	cells, known := ring.Value()
	if !known {
		return domain.Unknown[bool](), nil
	}
	// Without MaintainFlooring's census nothing paves, so the ring stays
	// cut rather than waiting on floors no one read.
	flooring, flooringKnown := projection.Facts.Upkeep.Flooring.Value()
	if !flooringKnown {
		stage = min(stage, policy.StageStable)
	}
	request.Stage, request.Policy = domain.Known(stage), p
	request.Stock, request.Claimed = projection.Resources, domain.Known(firebreakClaimed(projection))
	request.Floors = map[string]policy.FloorDefinition{}
	for _, d := range projection.Definitions {
		request.Floors[d.Name] = policy.FloorDefinition{Available: d.Available, Terrain: d.Terrain, Cleanliness: d.Cleanliness, Beauty: d.Beauty, Flammability: d.Flammability, PathCost: d.PathCost, Costs: d.Costs, WorkToBuild: d.WorkToBuild}
	}
	request.Ground = map[domain.Cell]domain.Fact[policy.FirebreakGround]{}
	site := map[domain.Cell]bridge.DefenseCell{}
	for _, tile := range firebreakTiles(cells) {
		tile.Max = domain.Cell{X: min(tile.Min.X+firebreakTile, projection.Bounds.Width) - 1, Z: min(tile.Min.Z+firebreakTile, projection.Bounds.Height) - 1}
		read, _, err := m.native.ReadDefenseSite(ctx, identity, tile)
		if err != nil {
			return domain.Unknown[bool](), err
		}
		if _, err = boundary.Context(read.Context, current); err != nil || domain.Tick(read.Context.GetTick()) < tick {
			return domain.Unknown[bool](), fmt.Errorf("%w: firebreak review: defense site context", ErrControl)
		}
		for _, cell := range read.Cells {
			site[cell.Cell] = cell
			request.Ground[cell.Cell] = domain.Known(firebreakGround(cell))
		}
	}
	m.mu.Lock()
	m.enter(world)
	dwell := m.dwell
	m.mu.Unlock()
	fact, next, err := policy.PlanFirebreak(request, dwell)
	if err != nil {
		return domain.Unknown[bool](), err
	}
	plan, known := fact.Value()
	if !known {
		return domain.Unknown[bool](), nil
	}
	var cut []domain.Cell
	for _, cell := range plan.Cells {
		if cell.Treatment == policy.FirebreakCut {
			cut = append(cut, cell.Cell)
		}
	}
	standing := map[domain.Cell]bool{}
	for start := 0; start < len(cut); start += domain.MaxAreaPlantCutCells {
		census, _, err := m.native.ReadPlantCutCensus(ctx, identity, cut[start:min(start+domain.MaxAreaPlantCutCells, len(cut))])
		if err != nil {
			return domain.Unknown[bool](), err
		}
		for _, plant := range census.Plants {
			standing[plant.Cell] = true
		}
	}
	open := map[domain.Cell]bool{}
	ruins := map[domain.Cell]domain.CoverClearance{}
	for _, cell := range plan.Deconstruct {
		cover := site[cell].Cover
		if cover == nil || cover.Designated || cover.Designation() != domain.CoverClearanceDeconstruct {
			continue
		}
		clearance, err := domain.NewCoverClearance(cover.ThingID, cover.DefName, cover.Designation(), cell)
		if err != nil {
			return domain.Unknown[bool](), err
		}
		open[cell], ruins[cell] = true, clearance
	}
	work := policy.FirebreakOwed(plan, standing, open)
	var pave []domain.Cell
	if flooringKnown {
		for _, cell := range plan.Cells {
			if cell.Treatment == policy.FirebreakPave && flooring.Terrains[site[cell.Cell].Terrain].Natural && !busy[cell.Cell] {
				pave = append(pave, cell.Cell)
			}
		}
	}
	if dir := os.Getenv(snapshot.DirEnv); dir != "" {
		f := snapshot.NewFirebreak(request, dwell, mapCells(standing), mapCells(open))
		f.Recorded, f.Snapshot = fmt.Sprintf("colony %s load %s map %d tick %d", current.Colony, current.Load, current.Map, tick), current
		if err := snapshot.RecordFirebreak(dir, f); err != nil {
			clockEvent(ctx, "firebreak", "snapshot", "firebreak snapshot not recorded: "+err.Error())
		}
	}
	m.mu.Lock()
	defer m.mu.Unlock()
	m.enter(world)
	m.dwell, m.tick, m.work, m.ruins, m.pave = next, tick, work, ruins, pave
	return domain.Known(work.Owed()), nil
}

func mapCells(set map[domain.Cell]bool) []domain.Cell {
	out := make([]domain.Cell, 0, len(set))
	for cell := range set {
		out = append(out, cell)
	}
	return out
}

// firebreakBusy lists the cells an open building action targets.
func firebreakBusy(plans []store.PlanState, current domain.GenerationSnapshot, player map[domain.PlanID]uint64) map[domain.Cell]bool {
	busy := map[domain.Cell]bool{}
	routineOpenActions(plans, current, player, func(a domain.Action) {
		if b, ok := a.Building(); ok {
			busy[b.Cell()] = true
		}
	})
	return busy
}

// firebreakAttempts counts the epoch's firebreak methods ordered within the
// window before tick.
func firebreakAttempts(history []domain.GoalMethod, tick domain.Tick) int {
	count := 0
	for _, m := range history {
		rest, ok := strings.CutPrefix(string(m.Method), firebreakPrefix)
		if !ok {
			continue
		}
		at, err := strconv.ParseInt(rest, 10, 64)
		if err == nil && tick-domain.Tick(at) < firebreakWindowTicks {
			count++
		}
	}
	return count
}

// RoutineFirebreakPlanner is MaintainFirebreak's planner: it orders the
// work the review found.
type RoutineFirebreakPlanner struct {
	reviewer *RoutineReviewer
	memory   *firebreakMemory
}
type RoutineFirebreakResult struct {
	Reason RoutineBuildingReason
	Plan   domain.PlanID
}

// NewRoutineFirebreakPlanner composes the planner and has reviewer run the
// firebreak review over native.
func NewRoutineFirebreakPlanner(reviewer *RoutineReviewer, native RoutineFirebreakSource) (*RoutineFirebreakPlanner, error) {
	if reviewer == nil || native == nil || !reviewer.methodEnabled(policy.MaintainFirebreak) {
		return nil, fmt.Errorf("%w: NewRoutineFirebreakPlanner: reviewer == nil || native == nil || !reviewer.methodEnabled(policy.MaintainFirebreak)", ErrControl)
	}
	reviewer.firebreak = &firebreakMemory{native: native}
	return &RoutineFirebreakPlanner{reviewer, reviewer.firebreak}, nil
}

// Pave supplies MaintainFlooring's firebreak tier (SetFirebreakPave).
func (r *RoutineFirebreakPlanner) Pave() []domain.Cell { return r.memory.Pave() }

func (r *RoutineFirebreakPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoutineFirebreakResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutineFirebreakResult{Reason: BuildingMethodDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoutineFirebreakResult{}, fmt.Errorf("%w: step: !state.ObservationKnown || state.Snapshot.Validate() != nil", ErrControl)
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutineFirebreakResult{}, err
	}
	if !review.Enabled || review.Snapshot != state.Snapshot {
		return RoutineFirebreakResult{Reason: BuildingMethodNoReview}, nil
	}
	goal, workable, err := p.journal.Workable(call, review, policy.MaintainFirebreak)
	if err != nil {
		return RoutineFirebreakResult{}, err
	}
	if !workable {
		return RoutineFirebreakResult{Reason: BuildingMethodNoDeficit}, nil
	}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoutineFirebreakResult{}, err
		}
		if store.PlanOpen(plan) {
			return RoutineFirebreakResult{Reason: BuildingMethodExistingWork}, nil
		}
	}
	work, ruins, ok := r.memory.take(stockpileWorld(state.Snapshot), review.Tick)
	if !ok {
		return RoutineFirebreakResult{Reason: BuildingMethodNoReview}, nil
	}
	if !work.Owed() {
		return RoutineFirebreakResult{Reason: BuildingMethodUsed}, nil
	}
	history, err := p.journal.LoadGoalMethods(call, goal.Goal.ID, goal.Goal.Epoch)
	if err != nil {
		return RoutineFirebreakResult{}, err
	}
	if firebreakAttempts(history, review.Tick) >= maxFirebreakAttempts {
		clockSchedulerLog("firebreak: exhausted for the day (%d cells, %d ruins waiting)", len(work.Cut), len(work.Deconstruct))
		return RoutineFirebreakResult{Reason: BuildingMethodExhausted}, nil
	}
	actions, err := firebreakActions(domain.MintPlanID(), work, ruins)
	if err != nil {
		return RoutineFirebreakResult{}, err
	}
	plan, err := domain.NewPlan(actions.id, 1, actions.actions)
	if err != nil {
		return RoutineFirebreakResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutineFirebreakResult{}, err
	}
	if p.session.State() != state {
		return RoutineFirebreakResult{}, fmt.Errorf("%w: step: p.session.State() != state", ErrControl)
	}
	method := domain.MethodID(fmt.Sprintf("%s%d", firebreakPrefix, review.Tick))
	if _, err = p.journal.CommitGoalMethodReason(call, goal.Goal.ID, goal.Revision, method, fmt.Sprintf("firebreak cut %d cells, deconstruct %d ruins", actions.cut, actions.ruins), plan); err != nil {
		return RoutineFirebreakResult{}, err
	}
	return RoutineFirebreakResult{Reason: BuildingMethodAdmitted, Plan: actions.id}, nil
}

type firebreakPlan struct {
	id         domain.PlanID
	actions    []domain.Action
	cut, ruins int
}

// firebreakActions is one area cut over the owed cut cells (at most one
// sweep's worth) and up to maxDefenseCoverBatch ruin deconstructions.
func firebreakActions(id domain.PlanID, work policy.FirebreakWork, ruins map[domain.Cell]domain.CoverClearance) (firebreakPlan, error) {
	out := firebreakPlan{id: id}
	if len(work.Cut) > 0 {
		cells := work.Cut[:min(len(work.Cut), domain.MaxAreaPlantCutCells)]
		cut, err := domain.NewAreaPlantCut(cells)
		if err != nil {
			return out, err
		}
		action, err := domain.NewAreaPlantCutAction(domain.ActionID(fmt.Sprintf("%s-cut", id)), cut)
		if err != nil {
			return out, err
		}
		out.actions, out.cut = append(out.actions, action), len(cells)
	}
	for i, cell := range work.Deconstruct {
		if out.ruins == maxDefenseCoverBatch {
			break
		}
		clearance, ok := ruins[cell]
		if !ok {
			continue
		}
		action, err := domain.NewCoverClearanceAction(domain.ActionID(fmt.Sprintf("%s-ruin-%d", id, i)), clearance)
		if err != nil {
			return out, err
		}
		out.actions, out.ruins = append(out.actions, action), out.ruins+1
	}
	return out, nil
}
