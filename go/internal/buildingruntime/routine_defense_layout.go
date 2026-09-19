package buildingruntime

import (
	"context"
	"crypto/sha256"
	"errors"
	"fmt"
	"math"
	"sort"
	"strconv"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// defenseSiteHalfExtent keeps the census rectangle around the colony centre
// inside the native 2048-cell defense site bound (45 x 45 = 2025).
const defenseSiteHalfExtent = 22

// defenseDefinitions are the native buildings each tier places. Wood keeps
// the first layout affordable (native Sandbags need fabric or leather, which
// a young colony rarely holds; a wooden Barricade gives the same 0.55 cover);
// MaintainStoneShell upgrades flammable walls afterwards through its own goal.
// The wood floor under each shooter needs no research and keeps the firing
// cell free of the trees that grew onto it before (#224).
var defenseDefinitions = policy.DefenseDefinitions{Sandbag: "Barricade", SandbagStuff: "WoodLog", Wall: "Wall", WallStuff: "WoodLog", Fence: "Fence", FenceStuff: "WoodLog", Trap: "TrapSpike", TrapStuff: "WoodLog", Floor: "WoodPlankFloor"}

// The powered turret tier (#61): the mini turret needs no rearming, and a
// conduit chain connects it to the network. Both are planning definitions
// the reviewer's census is asked for, so availability (research, content),
// draw and cost are observed, never assumed. defenseMaxTurrets bounds the
// tier; each turret costs steel and components the colony may need first.
const (
	defenseTurretDefinition  = "Turret_MiniTurret"
	defenseConduitDefinition = "PowerConduit"
	defenseMaxTurrets        = 2
)

var defenseExtraDefinitions = []string{defenseTurretDefinition, defenseConduitDefinition}

// defenseTierOrder is the staged construction order; a tier without
// placements (the chokepoint reuses existing geometry) is complete as-is.
var defenseTierOrder = []policy.DefenseTierName{policy.TierChokepoint, policy.TierFiringLine, policy.TierFunnel, policy.TierTrapCorridor, policy.TierTurrets}

// RoutineDefenseLayoutSource is the native read set the planner needs beyond
// the reviewer's shared colony observation: the census rectangle, shooting
// lines, the access audit, defender gear and placement previews.
type RoutineDefenseLayoutSource interface {
	ReadDefenseSite(context.Context, *c.Identity, bridge.CellRect) (bridge.DefenseSite, bridge.Result, error)
	ReadLinesOfFire(context.Context, *c.Identity, []domain.Cell, []domain.Cell) (bridge.LinesOfFire, bridge.Result, error)
	ReadSpatialAccess(context.Context, *c.Identity, []domain.Cell, []domain.Cell, []string) (bridge.SpatialAccess, bridge.Result, error)
	ReadCombatPawns(context.Context, *c.Identity, []string) (*o.ListPawnsReply, bridge.Result, error)
	PreviewBuilding(context.Context, domain.Action, domain.GenerationSnapshot) (bridge.BuildingPreview, bridge.Result, error)
}
type RoutineDefenseLayoutPlanner struct {
	reviewer *RoutineReviewer
	native   RoutineDefenseLayoutSource
}
type RoutineDefenseLayoutResult struct {
	Reason RoutineBuildingReason
	Plan   domain.PlanID
	Tier   policy.DefenseTierName
	// NativeWorkTicks asks for a clock window without a plan of its own: a
	// tier's missing building already has a blueprint or frame on its cell
	// (a sprung trap's auto-rearm), so native construction restores it.
	NativeWorkTicks uint32
}

// defenseNativeWorkTicks is the window admitted while a tier waits on a
// blueprint the game placed itself.
const defenseNativeWorkTicks = 2500

func NewRoutineDefenseLayoutPlanner(reviewer *RoutineReviewer, native RoutineDefenseLayoutSource) (*RoutineDefenseLayoutPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, ErrControl
	}
	if reviewer.native == nil {
		return nil, ErrControl
	}
	return &RoutineDefenseLayoutPlanner{reviewer, native}, nil
}
func (r *RoutineDefenseLayoutPlanner) Step(ctx context.Context) (RoutineDefenseLayoutResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, false)
	if err != nil {
		return RoutineDefenseLayoutResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

// A tier's method is keyed by tier and attempt: a plan cancelled by an
// authority discontinuity (a letter pause) or refused natively is retried
// with a fresh method rather than counted as built, up to a small bound.
const maxDefenseTierAttempts = 4

func defenseTierPrefix(tier policy.DefenseTierName) string { return "defense-" + string(tier) + "-" }

func defenseTierMethodID(tier policy.DefenseTierName, attempt int) domain.MethodID {
	return domain.MethodID(fmt.Sprintf("%s%d", defenseTierPrefix(tier), attempt))
}

// defenseLayoutGoal finds the goal the planner serves: the review binding
// for EnsureDefensiveLayout when the routine policy opts in, otherwise the
// player's create_goal binding for the same kind.
func defenseLayoutGoal(ctx context.Context, p *Player, review store.RoutineReview, world store.World) (store.GoalState, bool, error) {
	for _, binding := range review.Goals {
		if binding.Need == policy.EnsureDefensiveLayout {
			goal, err := p.journal.LoadGoal(ctx, binding.Goal)
			return goal, err == nil, err
		}
	}
	goals, err := p.journal.PlayerGoals(ctx, world)
	if err != nil {
		return store.GoalState{}, false, err
	}
	id, ok := goals[domain.EnsureDefensiveLayoutGoal]
	if !ok {
		return store.GoalState{}, false, nil
	}
	goal, err := p.journal.LoadGoal(ctx, id)
	return goal, err == nil, err
}

func (r *RoutineDefenseLayoutPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoutineDefenseLayoutResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutineDefenseLayoutResult{Reason: BuildingMethodDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoutineDefenseLayoutResult{}, defenseControlErr(113)
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutineDefenseLayoutResult{}, err
	}
	if !review.Enabled || review.Snapshot != state.Snapshot {
		return RoutineDefenseLayoutResult{Reason: BuildingMethodNoReview}, nil
	}
	world := store.World{Colony: state.Snapshot.Colony, Load: state.Snapshot.Load, Map: state.Snapshot.Map}
	goal, found, err := defenseLayoutGoal(call, p, review, world)
	if err != nil {
		return RoutineDefenseLayoutResult{}, err
	}
	if !found || goal.Goal.Status != domain.GoalActive || goal.Goal.Need != domain.NeedDeficit {
		return RoutineDefenseLayoutResult{Reason: BuildingMethodNoDeficit}, nil
	}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoutineDefenseLayoutResult{}, err
		}
		if domain.GoalWorkOpen(plan.Progress) {
			return RoutineDefenseLayoutResult{Reason: BuildingMethodExistingWork}, nil
		}
	}
	// EnsureDefensiveLayout competes for the bounded concurrent-project
	// capacity with the other priority>=3 autopilot goals; admission would
	// conflict unless this review's arbitration selected it. Checked after
	// the existing-work loop: an in-flight tier is Committed, never
	// re-Selected, so an earlier check would refuse its own open work.
	selected := false
	for _, row := range review.Development.Rows {
		selected = selected || row.Goal == policy.EnsureDefensiveLayout && row.Selected
	}
	if !selected {
		return RoutineDefenseLayoutResult{Reason: BuildingMethodRefused}, nil
	}
	record, stored, err := p.journal.LoadDefenseLayout(call, world)
	if err != nil {
		return RoutineDefenseLayoutResult{}, err
	}
	if stored && record.World != world {
		// A reload of the same colony: the geometry is on the map, but which
		// tiers still stand is re-observed below before the record counts as
		// complete again (an older save may lack some of them).
		record.World, record.Complete = world, false
		if err = p.journal.SaveDefenseLayout(call, record); err != nil {
			return RoutineDefenseLayoutResult{}, err
		}
	}
	if stored && (record.Goal != goal.Goal.ID || record.Epoch != goal.Goal.Epoch) {
		// A new goal epoch (cancelled and re-created, e.g. by a letter pause)
		// keeps the stored geometry: re-proposing against a census that
		// already holds the earlier epoch's walls shifts the corridor by a
		// cell and lands traps beside the old ones, which can never place.
		// Built tiers stay built; pending tiers get a fresh retry budget.
		record.Goal, record.Epoch = goal.Goal.ID, goal.Goal.Epoch
		for i := range record.Tiers {
			if !record.Tiers[i].Built {
				record.Tiers[i].Attempts = 0
			}
		}
		if err = p.journal.SaveDefenseLayout(call, record); err != nil {
			return RoutineDefenseLayoutResult{}, err
		}
	}
	combat, err := defenseCombatKey(call, p, review)
	if err != nil {
		return RoutineDefenseLayoutResult{}, err
	}
	if stored && record.Complete && !defenseReverifyDue(record, review.Tick, combat) {
		return RoutineDefenseLayoutResult{Reason: BuildingMethodNoDeficit}, nil
	}
	identity, _, err := r.reviewer.native.Identity(call)
	if err != nil {
		return RoutineDefenseLayoutResult{}, err
	}
	expected, err := observation.DecodeIdentity(identity)
	if err != nil {
		return RoutineDefenseLayoutResult{}, err
	}
	if !routineBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoutineDefenseLayoutResult{}, defenseControlErr(168)
	}
	claims, err := p.journal.ConstructionClaims(call, state.Snapshot, expected.Tick)
	if err != nil {
		return RoutineDefenseLayoutResult{}, err
	}
	// The turret and conduit definitions are outside the reviewer's shared
	// census, so they are asked for only while a turret tier may be
	// proposed: a fresh proposal, or a stored layout without turrets whose
	// probe interval has elapsed.
	var definitions []string
	if !stored || defenseTurretsDue(record, expected.Tick) {
		definitions = defenseExtraDefinitions
	}
	read, err := r.reviewer.observeOwned(call, r.reviewer.native, expected, claims, definitions...)
	if err != nil {
		return RoutineDefenseLayoutResult{}, err
	}
	if !stored {
		layout, entrances, ok, err := r.propose(call, state, read)
		if err != nil {
			return RoutineDefenseLayoutResult{}, err
		}
		if !ok {
			return RoutineDefenseLayoutResult{Reason: BuildingMethodUnknown}, nil
		}
		record, err = store.NewDefenseLayoutRecord(world, goal.Goal.ID, goal.Goal.Epoch, layout, entrances)
		if err != nil {
			return RoutineDefenseLayoutResult{}, err
		}
		if err = p.journal.SaveDefenseLayout(call, record); err != nil {
			return RoutineDefenseLayoutResult{}, err
		}
	}
	// A tier counts as built only while every one of its buildings is
	// observed standing; a cancelled or unsuccessful attempt leaves it
	// pending for retry, and a building lost after the tier was built (a
	// breached wall, a sprung trap) re-opens it. Settled plans retire out
	// of goal.Methods, so the census, not the journal, is the source of
	// truth.
	census, err := r.observeTiers(call, state, read, &record)
	if err != nil {
		return RoutineDefenseLayoutResult{}, err
	}
	tick := read.Projection.Identity.Tick
	// A layout that stood before turrets could be placed (no research, no
	// network, no steel) gains the tier once the observed gates open.
	if len(definitions) > 0 && stored {
		request := defenseTurretRequest(read)
		if request.TurretGatesOpen() {
			if err = r.proposeTurrets(call, state, read, &record); err != nil {
				return RoutineDefenseLayoutResult{}, err
			}
		} else {
			clockSchedulerLog("defense-layout: turret gates closed %s", defenseTurretGates(request))
		}
	}
	for _, name := range defenseTierOrder {
		tier, buildings, ok := record.Tier(name)
		if !ok || len(buildings) == 0 || tier.Built {
			continue
		}
		if tier.Attempts >= maxDefenseTierAttempts {
			// Verified as far as it goes: the tier is retried after the
			// next combat or game hour, not on every step.
			record.VerifiedTick, record.VerifiedCombat = tick, combat
			if err = p.journal.SaveDefenseLayout(call, record); err != nil {
				return RoutineDefenseLayoutResult{}, err
			}
			if err = yieldDevelopment(call, p.journal, review, policy.EnsureDefensiveLayout); err != nil {
				return RoutineDefenseLayoutResult{}, err
			}
			return RoutineDefenseLayoutResult{Reason: BuildingMethodExhausted, Tier: name}, nil
		}
		buildings = defenseMissingBuildings(buildings, census)
		if len(buildings) == 0 {
			// The census re-opened the tier on a cell it cannot see
			// (fogged); nothing can be admitted until it can.
			return RoutineDefenseLayoutResult{Reason: BuildingMethodUnknown, Tier: name}, nil
		}
		return r.admit(call, epoch, goal, state, read, record, tier, buildings, defenseTierMethodID(name, tier.Attempts))
	}
	record.Complete, record.VerifiedTick, record.VerifiedCombat = true, tick, combat
	// Every tier stands, so combat holds the proven line; a standing turret
	// without power or with an empty barrel is still a deficit the layout
	// waits on (the network's fuel or generation is EnsureBasicPower's, a
	// lost conduit is re-placed above once the census misses it; an empty
	// barrel is rearmed below, or its fuel raised as a resource need).
	workers, _ := read.Projection.WorkPawns.Value()
	upkeep := policy.DefenseRearmTurrets(defenseTurretFacts(record, census), workers, read.Projection.Resources)
	record.FuelShortage = upkeep.Shortage
	if err = p.journal.SaveDefenseLayout(call, record); err != nil {
		return RoutineDefenseLayoutResult{}, err
	}
	if len(upkeep.Rearm) > 0 {
		return r.rearm(call, epoch, goal, review, state, read, upkeep.Rearm[0], arbiter)
	}
	if len(upkeep.Unpowered) > 0 || len(upkeep.Empty) > 0 {
		clockSchedulerLog("defense-layout: turrets unpowered at %v, unfuelled at %v (fuel shortage %v)", upkeep.Unpowered, upkeep.Empty, upkeep.Shortage)
		return RoutineDefenseLayoutResult{Reason: BuildingMethodUnknown, Tier: policy.TierTurrets}, nil
	}
	return RoutineDefenseLayoutResult{Reason: BuildingMethodNoDeficit}, nil
}

// A rearm method is keyed by turret and the tick it was ordered at: the
// layout goal keeps one epoch for as long as the operator opts in and a
// settled order retires out of goal.Methods, so an attempt counter would
// collide with the epoch's history the next time the same barrel empties.
// maxDefenseRearmAttempts bounds the orders per turret within
// defenseRearmWindowTicks (one game day); a refused or interrupted order is
// retried at a later tick until the bound.
const (
	maxDefenseRearmAttempts = 4
	defenseRearmWindowTicks = 60000
)

func defenseRearmPrefix(turret string) string { return "defense-rearm-" + turret + "-" }

// defenseRearmAttempts counts the epoch's rearm methods for the turret
// ordered within the window before tick.
func defenseRearmAttempts(history []domain.GoalMethod, turret string, tick domain.Tick) int {
	prefix := defenseRearmPrefix(turret)
	count := 0
	for _, m := range history {
		if !strings.HasPrefix(string(m.Method), prefix) {
			continue
		}
		at, err := strconv.ParseInt(strings.TrimPrefix(string(m.Method), prefix), 10, 64)
		if err == nil && tick-domain.Tick(at) < defenseRearmWindowTicks {
			count++
		}
	}
	return count
}

// rearm admits one forced refuel order for an empty tier barrel as a
// recovery_service action under the layout goal: the same native work-giver
// job a player's float-menu click issues, whose CAS token and pawn
// eligibility Hands re-check at dispatch.
func (r *RoutineDefenseLayoutPlanner) rearm(call, epoch context.Context, goal store.GoalState, review store.RoutineReview, state ControlState, read observation.RoutineReading, order policy.DefenseRearm, arbiter *stepArbiter) (RoutineDefenseLayoutResult, error) {
	p := r.reviewer.player
	tick := read.Projection.Identity.Tick
	history, err := p.journal.LoadGoalMethods(call, goal.Goal.ID, goal.Goal.Epoch)
	if err != nil {
		return RoutineDefenseLayoutResult{}, err
	}
	if defenseRearmAttempts(history, order.Turret, tick) >= maxDefenseRearmAttempts {
		clockSchedulerLog("defense-layout: rearm of %s at %v exhausted", order.Turret, order.Cell)
		if err = yieldDevelopment(call, p.journal, review, policy.EnsureDefensiveLayout); err != nil {
			return RoutineDefenseLayoutResult{}, err
		}
		return RoutineDefenseLayoutResult{Reason: BuildingMethodExhausted, Tier: policy.TierTurrets}, nil
	}
	if arbiter == nil || !arbiter.tryClaim([]domain.PawnID{domain.PawnID(order.Pawn)}, "defense-rearm:"+order.Turret) {
		return RoutineDefenseLayoutResult{Reason: BuildingMethodUsed, Tier: policy.TierTurrets}, nil
	}
	method := domain.MethodID(fmt.Sprintf("%s%d", defenseRearmPrefix(order.Turret), tick))
	digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%d/%s", goal.Goal.ID, goal.Goal.Epoch, method)))
	id := domain.PlanID(fmt.Sprintf("routine-defense-rearm-%x", digest[:16]))
	service, err := domain.NewRecoveryService(domain.PawnID(order.Pawn), order.Turret, domain.RecoveryServiceRefuel)
	if err != nil {
		return RoutineDefenseLayoutResult{}, err
	}
	action, err := domain.NewRecoveryServiceAction(domain.ActionID(fmt.Sprintf("%s-0", id)), service)
	if err != nil {
		return RoutineDefenseLayoutResult{}, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoutineDefenseLayoutResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutineDefenseLayoutResult{}, err
	}
	now := r.reviewer.clock.Now()
	if p.session.State() != state || now.Before(read.StartedAt) || now.Sub(read.StartedAt) > r.reviewer.maxAge {
		return RoutineDefenseLayoutResult{}, defenseControlErr(360)
	}
	if _, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, method, plan); err != nil {
		return RoutineDefenseLayoutResult{}, err
	}
	clockSchedulerLog("defense-layout: rearm %s at %v by %s with %s (%s)", order.Turret, order.Cell, order.Pawn, order.Fuel, method)
	return RoutineDefenseLayoutResult{Reason: BuildingMethodAdmitted, Plan: id, Tier: policy.TierTurrets}, nil
}

// defenseTurretFacts is the record's turret cells with the census's
// consumer facts on each: identity and service state from the power census,
// unknown where the census has no consumer on the cell.
func defenseTurretFacts(record store.DefenseLayoutRecord, census *defenseCensus) []policy.DefenseTurretFacts {
	if census == nil {
		return nil
	}
	var out []policy.DefenseTurretFacts
	for _, tier := range record.Tiers {
		if tier.Name != policy.TierTurrets {
			continue
		}
		for _, b := range tier.Buildings {
			if b.Definition == defenseConduitDefinition {
				continue
			}
			facts := policy.DefenseTurretFacts{Cell: b.Cell}
			if site, ok := census.consumers[b.Cell]; ok {
				facts.ID, facts.Powered, facts.OutOfFuel, facts.Fuel, facts.TargetFuel = site.ID, site.Powered, site.OutOfFuel, site.Fuel, site.TargetFuel
				for _, d := range site.FuelDefinitions {
					facts.FuelDefinitions = append(facts.FuelDefinitions, policy.Resource(d))
				}
			}
			out = append(out, facts)
		}
	}
	return out
}

// defenseTurretsDue reports whether a stored layout without a placed turret
// tier is due another proposal: never probed, or a reverify interval since.
func defenseTurretsDue(record store.DefenseLayoutRecord, tick domain.Tick) bool {
	turrets, _, ok := record.Tier(policy.TierTurrets)
	return (!ok || len(turrets.Buildings) == 0) && (record.TurretsProbedTick == 0 || tick-record.TurretsProbedTick >= defenseReverifyTicks)
}

// defenseCensus is one same-tick observation of the layout's cells: the
// edifice standing on and the terrain under each visible cell, the conduit
// cells (conduits are not edifices, so the power census carries them) and
// each power consumer by cell with its powered and refuelable service state.
type defenseCensus struct {
	edifice   map[domain.Cell]string
	terrain   map[domain.Cell]string
	conduits  map[domain.Cell]bool
	consumers map[domain.Cell]policy.PowerSite
}

// standing reports whether the building's cell carries it: a conduit by the
// conduit census, the firing-line floor by the cell's terrain, anything
// else by its edifice.
func (c *defenseCensus) standing(definition string, cell domain.Cell) bool {
	if definition == defenseConduitDefinition {
		return c.conduits[cell]
	}
	if definition == defenseDefinitions.Floor {
		return c.terrain[cell] == definition
	}
	return c.edifice[cell] == definition
}

// defenseTurretRequest is the turret tier's observed gates from the shared
// routine reading: the turret and conduit planning definitions (availability
// re-checked against the research snapshot's finished projects), the
// network with the most spare watts and the conduits that carry it, and the
// stock census.
func defenseTurretRequest(read observation.RoutineReading) policy.DefenseRequest {
	projection := read.Projection
	request := policy.DefenseRequest{Definitions: defenseDefinitions, UnitCosts: map[string][]policy.Amount{}}
	request.Turret = policy.DefenseTurretRequest{Definition: defenseTurretDefinition, Conduit: defenseConduitDefinition, Stock: projection.Resources, Max: defenseMaxTurrets}
	research, rk := projection.Facts.Research.Value()
	finished := map[policy.ResearchProjectID]bool{}
	for _, id := range research.Finished {
		finished[id] = true
	}
	for _, d := range projection.Definitions {
		if d.Name != defenseTurretDefinition && d.Name != defenseConduitDefinition {
			continue
		}
		if costs, known := d.Costs.Value(); known {
			request.UnitCosts[d.Name] = append([]policy.Amount{}, costs...)
		}
		if d.Name != defenseTurretDefinition {
			continue
		}
		available := d.Available
		if rk {
			for _, prerequisite := range d.Research {
				v, k := available.Value()
				available = domain.Known(k && v && finished[policy.ResearchProjectID(prerequisite)])
			}
		} else if len(d.Research) > 0 {
			available = domain.Unknown[bool]()
		}
		request.Turret.Available = available
		if stuff, known := d.Stuff.Value(); known {
			request.Turret.Stuff = stuff
		}
		if w, known := d.PowerW.Value(); known && w >= 0 {
			request.Turret.DrawW = domain.Known(w)
		}
	}
	topology, known := projection.PowerPlanning.Value()
	if !known {
		return request
	}
	best, spare := "", 0.0
	for _, net := range topology.Networks {
		generation, gk := net.GenerationW.Value()
		consumption, ck := net.ConsumptionW.Value()
		if gk && ck && generation > 0 && (best == "" || generation-consumption > spare) {
			best, spare = net.ID, generation-consumption
		}
	}
	if best == "" {
		return request
	}
	request.Turret.SpareW = domain.Known(spare)
	request.Turret.Transmitters = defenseNetworkConduits(topology, best)
	return request
}

// defenseTurretGates renders the turret request's gates for the scheduler
// log: which observation keeps the tier from being proposed.
func defenseTurretGates(r policy.DefenseRequest) string {
	q := r.Turret
	stock, sk := q.Stock.Value()
	_, ck := r.UnitCosts[q.Definition]
	return fmt.Sprintf("available=%v draw=%v spare=%v transmitters=%d costs_known=%v stock_known=%v steel=%d components=%d",
		q.Available, q.DrawW, q.SpareW, len(q.Transmitters), ck, sk, stock["Steel"], stock["ComponentIndustrial"])
}

// defenseNetworkConduits approximates which conduits carry the network the
// native way round: a consumer or producer connects to a transmitter within
// connector reach of its footprint, and conduits chain cardinally.
func defenseNetworkConduits(topology policy.PowerTopology, network string) []domain.Cell {
	conduit := map[domain.Cell]bool{}
	for _, c := range topology.Conduits {
		conduit[c] = true
	}
	seen := map[domain.Cell]bool{}
	var queue, out []domain.Cell
	for _, b := range topology.Buildings {
		if id, known := b.Network.Value(); !known || id != network {
			continue
		}
		for _, cell := range b.Occupied {
			for _, c := range topology.Conduits {
				if !seen[c] && abs32(c.X-cell.X) <= 6 && abs32(c.Z-cell.Z) <= 6 {
					seen[c] = true
					queue = append(queue, c)
				}
			}
		}
	}
	for len(queue) > 0 {
		c := queue[0]
		queue = queue[1:]
		out = append(out, c)
		for _, d := range []domain.Cell{{X: 1}, {X: -1}, {Z: 1}, {Z: -1}} {
			n := domain.Cell{X: c.X + d.X, Z: c.Z + d.Z}
			if conduit[n] && !seen[n] {
				seen[n] = true
				queue = append(queue, n)
			}
		}
	}
	sort.Slice(out, func(i, j int) bool { return out[i].X < out[j].X || out[i].X == out[j].X && out[i].Z < out[j].Z })
	return out
}

func abs32(v int32) int32 {
	if v < 0 {
		return -v
	}
	return v
}

// proposeTurrets reads the census around the colony and the turret
// candidates' lines of fire against the stored geometry and, when the tier
// places anything, records it pending with a fresh retry budget. The probe
// tick is recorded either way so an open gate without a placeable site is
// re-read once per reverify interval, not every step.
func (r *RoutineDefenseLayoutPlanner) proposeTurrets(call context.Context, state ControlState, read observation.RoutineReading, record *store.DefenseLayoutRecord) error {
	projection := read.Projection
	identity := boundary.Identity(state.Snapshot)
	region := defenseRegion(projection.Center, projection.Bounds)
	site, _, err := r.native.ReadDefenseSite(call, identity, region)
	if err != nil {
		return err
	}
	if err = r.sameTick(site.Context, state, projection.Identity.Tick); err != nil {
		return err
	}
	request := defenseTurretRequest(read)
	request.Bounds, request.Home = projection.Bounds, projection.Center
	request.Region = policy.Rectangle{X: region.Min.X, Z: region.Min.Z, Width: region.Max.X - region.Min.X + 1, Height: region.Max.Z - region.Min.Z + 1}
	for _, cell := range site.Cells {
		request.Cells = append(request.Cells, defenseCellFacts(cell))
	}
	geometry := defenseRecordGeometry(*record)
	record.TurretsProbedTick = projection.Identity.Tick
	_, candidates, err := policy.DefenseTurrets(request, geometry)
	if err != nil || len(candidates) == 0 {
		clockSchedulerLog("defense-layout: no turret candidate (firing=%v cells=%d err=%v %s)", geometry.Firing, len(request.Cells), err, defenseTurretGates(request))
		return r.reviewer.player.journal.SaveDefenseLayout(call, *record)
	}
	var probe []domain.Cell
	for _, c := range candidates {
		probe = append(probe, c.Cell)
	}
	lines, _, err := r.native.ReadLinesOfFire(call, identity, probe, record.TrapLane)
	if err != nil {
		return err
	}
	if err = r.sameTick(lines.Context, state, projection.Identity.Tick); err != nil {
		return err
	}
	for _, line := range lines.Lines {
		l := policy.DefenseLine{From: line.From, To: line.To}
		if line.Known {
			l.LineOfSight = domain.Known(line.LineOfSight)
		}
		request.Lines = append(request.Lines, l)
	}
	tier, verified, err := policy.DefenseTurrets(request, geometry)
	clockSchedulerLog("defense-layout: turret probe candidates=%+v lines=%d buildings=%d costs=%v err=%v %s", verified, len(lines.Lines), len(tier.Buildings), tier.Costs, err, defenseTurretGates(request))
	if err != nil || len(tier.Buildings) == 0 {
		return r.reviewer.player.journal.SaveDefenseLayout(call, *record)
	}
	record.SetTurretTier(tier)
	return r.reviewer.player.journal.SaveDefenseLayout(call, *record)
}

// defenseRecordGeometry is the stored layout as the turret tier sees it.
func defenseRecordGeometry(record store.DefenseLayoutRecord) policy.DefenseGeometry {
	g := policy.DefenseGeometry{Entry: record.Entry, Approach: append([]domain.Cell{}, record.TrapLane...), Toward: record.Toward, Firing: append([]domain.Cell{}, record.Firing...), Lanes: append(append([]domain.Cell{}, record.TrapLane...), record.SafeLane...)}
	for _, tier := range record.Tiers {
		if tier.Name == policy.TierTurrets {
			continue
		}
		g.Reserved = append(g.Reserved, tier.Reserved...)
		for _, b := range tier.Buildings {
			g.Reserved = append(g.Reserved, b.Cell)
		}
	}
	return g
}

// defenseReverifyTicks is how much simulation a Complete record's census
// stays trusted without a combat: nothing on the map changes while the
// clock is held, so the interval counts game ticks, not steps.
const defenseReverifyTicks = 2500

// defenseReverifyDue reports whether a Complete record's tiers must be
// re-observed: after a combat the record has not seen the end of, or once
// the reverify interval of simulation has passed since the last census.
func defenseReverifyDue(record store.DefenseLayoutRecord, tick domain.Tick, combat string) bool {
	return combat != record.VerifiedCombat || tick-record.VerifiedTick >= defenseReverifyTicks
}

// defenseCombatKey identifies the ActiveCombat goal epoch the review binds
// (goal/epoch): each raid that follows a recovery advances the epoch, so a
// changed key marks a combat whose aftermath the layout has not verified.
// Empty when the review binds no combat goal.
func defenseCombatKey(ctx context.Context, p *Player, review store.RoutineReview) (string, error) {
	for _, binding := range review.Goals {
		if binding.Need != policy.ActiveCombat {
			continue
		}
		goal, err := p.journal.LoadGoal(ctx, binding.Goal)
		if errors.Is(err, store.ErrNotFound) {
			return "", nil
		}
		if err != nil {
			return "", err
		}
		return fmt.Sprintf("%s/%d", goal.Goal.ID, goal.Goal.Epoch), nil
	}
	return "", nil
}

// defenseTierCensus applies one census to the record: a tier whose
// buildings all stand is Built; a Built tier that lost a building is
// re-opened with a fresh retry budget. It reports whether anything changed.
func defenseTierCensus(record *store.DefenseLayoutRecord, census *defenseCensus) bool {
	changed := false
	for _, tier := range record.Tiers {
		if len(tier.Buildings) == 0 {
			continue
		}
		standing := true
		for _, b := range tier.Buildings {
			standing = standing && census.standing(b.Definition, b.Cell)
		}
		if standing == tier.Built {
			continue
		}
		tier.Built = standing
		if !standing {
			tier.Attempts = 0
		}
		record.SetTier(tier)
		changed = true
	}
	return changed
}

// observeTiers reads the layout's census once and applies it to every
// tier's Built state, saving the record when anything changed. It returns
// the census so admission can be limited to the buildings actually missing.
func (r *RoutineDefenseLayoutPlanner) observeTiers(call context.Context, state ControlState, read observation.RoutineReading, record *store.DefenseLayoutRecord) (*defenseCensus, error) {
	placed := false
	for _, tier := range record.Tiers {
		placed = placed || len(tier.Buildings) > 0
	}
	if !placed {
		return nil, nil
	}
	projection := read.Projection
	site, _, err := r.native.ReadDefenseSite(call, boundary.Identity(state.Snapshot), defenseRecordRegion(*record, projection.Bounds))
	if err != nil {
		return nil, err
	}
	if err = r.sameTick(site.Context, state, projection.Identity.Tick); err != nil {
		return nil, err
	}
	census := &defenseCensus{edifice: map[domain.Cell]string{}, terrain: map[domain.Cell]string{}, conduits: map[domain.Cell]bool{}, consumers: map[domain.Cell]policy.PowerSite{}}
	for _, cell := range site.Cells {
		if !cell.Fogged {
			census.edifice[cell.Cell], census.terrain[cell.Cell] = cell.EdificeDefName, cell.Terrain
		}
	}
	if topology, known := projection.PowerPlanning.Value(); known {
		for _, c := range topology.Conduits {
			census.conduits[c] = true
		}
		for _, b := range topology.Buildings {
			census.consumers[b.Cell] = b
		}
	}
	if !defenseTierCensus(record, census) {
		return census, nil
	}
	return census, r.reviewer.player.journal.SaveDefenseLayout(call, *record)
}

// routineDefensiveLayoutStanding is the journal's view of the layout for
// development arbitration: known only while the goal is opted in and a
// record for this colony is stored; a record from another load is a reload
// whose tiers are re-observed before it counts. It also returns the
// record's turret fuel shortage as derived MaintainResource floors (#205).
func routineDefensiveLayoutStanding(ctx context.Context, journal *store.Store, p policy.RoutinePolicy, snapshot domain.GenerationSnapshot) (domain.Fact[bool], map[policy.Resource]int64, error) {
	if !p.DefensiveLayout {
		return domain.Unknown[bool](), nil, nil
	}
	world := store.World{Colony: snapshot.Colony, Load: snapshot.Load, Map: snapshot.Map}
	record, stored, err := journal.LoadDefenseLayout(ctx, world)
	if err != nil {
		return domain.Unknown[bool](), nil, err
	}
	if !stored {
		return domain.Known(false), nil, nil
	}
	if record.World != world {
		return domain.Known(false), nil, nil
	}
	var needs map[policy.Resource]int64
	for _, a := range record.FuelShortage {
		if needs == nil {
			needs = map[policy.Resource]int64{}
		}
		needs[a.Resource] += a.Count
	}
	return domain.Known(record.Standing()), needs, nil
}

// resourceTargets is the operator's MaintainResource floors, the stone-block
// floor the stock census derives (#231) and the journal's derived needs
// merged, the targets every resource planner dispatches on. An unknown
// census leaves the stone floor out.
func (r *RoutineReviewer) resourceTargets(ctx context.Context, snapshot domain.GenerationSnapshot, stock domain.Fact[[]policy.Amount]) (map[policy.Resource]int64, error) {
	_, needs, err := routineDefensiveLayoutStanding(ctx, r.player.journal, r.policy, snapshot)
	if err != nil {
		return nil, err
	}
	return r.policy.EffectiveResourceTargets(stock, needs)
}

// defenseMissingBuildings keeps the tier's buildings the census does not
// show standing. A tier re-opened by one lost building (a sprung trap, a
// breached wall) is repaired by that building alone: previewing a standing
// one is refused as an identical thing, which would hold the whole tier.
// Before any census (nil) every building is missing.
func defenseMissingBuildings(buildings []domain.Building, census *defenseCensus) []domain.Building {
	if census == nil {
		return buildings
	}
	var missing []domain.Building
	for _, b := range buildings {
		if !census.standing(b.Definition(), b.Cell()) {
			missing = append(missing, b)
		}
	}
	return missing
}

// propose reads the census around the colony centre, the defenders' gear and
// the firing lines, and returns the policy layout. ok is false when the
// colony has no verified killbox geometry yet (no ranged defender, no
// chokepoint, no line of sight), which is a wait rather than an error.
func (r *RoutineDefenseLayoutPlanner) propose(call context.Context, state ControlState, read observation.RoutineReading) (policy.DefenseLayout, []domain.Cell, bool, error) {
	projection := read.Projection
	identity := boundary.Identity(state.Snapshot)
	region := defenseRegion(projection.Center, projection.Bounds)
	site, _, err := r.native.ReadDefenseSite(call, identity, region)
	if err != nil {
		return policy.DefenseLayout{}, nil, false, err
	}
	if err = r.sameTick(site.Context, state, projection.Identity.Tick); err != nil {
		return policy.DefenseLayout{}, nil, false, err
	}
	request := defenseTurretRequest(read)
	request.Bounds, request.Home = projection.Bounds, projection.Center
	request.Region = policy.Rectangle{X: region.Min.X, Z: region.Min.Z, Width: region.Max.X - region.Min.X + 1, Height: region.Max.Z - region.Min.Z + 1}
	for _, cell := range site.Cells {
		request.Cells = append(request.Cells, defenseCellFacts(cell))
		if !cell.Fogged && cell.Door && cell.PlayerOwned {
			request.Entrances = append(request.Entrances, cell.Cell)
		}
	}
	colonistsComplete, ck := read.Emergency.ColonistsComplete.Value()
	if !ck || !colonistsComplete || len(read.Emergency.Colonists) == 0 {
		return policy.DefenseLayout{}, nil, false, nil
	}
	ids := make([]string, 0, len(read.Emergency.Colonists))
	for _, pawn := range read.Emergency.Colonists {
		ids = append(ids, string(pawn.ID))
	}
	reply, _, err := r.native.ReadCombatPawns(call, identity, ids)
	if err != nil {
		return policy.DefenseLayout{}, nil, false, err
	}
	observed := reply.GetObserved()
	if observed == nil || len(observed.Pawns) != len(ids) {
		return policy.DefenseLayout{}, nil, false, defenseControlErr(248)
	}
	if err = r.sameTick(observed.Context, state, projection.Identity.Tick); err != nil {
		return policy.DefenseLayout{}, nil, false, err
	}
	request.Defenders, request.MinRange = defenderRange(observed.Pawns)
	if request.Defenders == 0 {
		return policy.DefenseLayout{}, nil, false, nil
	}
	layout, err := policy.DefenseLayouts(request)
	if err != nil {
		return policy.DefenseLayout{}, nil, false, nil
	}
	firing, approach := layout.Probe()
	if len(firing) == 0 {
		return policy.DefenseLayout{}, nil, false, nil
	}
	lines, _, err := r.native.ReadLinesOfFire(call, identity, firing, approach)
	if err != nil {
		return policy.DefenseLayout{}, nil, false, err
	}
	if err = r.sameTick(lines.Context, state, projection.Identity.Tick); err != nil {
		return policy.DefenseLayout{}, nil, false, err
	}
	for _, line := range lines.Lines {
		l := policy.DefenseLine{From: line.From, To: line.To}
		if line.Known {
			l.LineOfSight = domain.Known(line.LineOfSight)
		}
		request.Lines = append(request.Lines, l)
	}
	layout, err = policy.DefenseLayouts(request)
	if err != nil || !layout.LinesVerified {
		return policy.DefenseLayout{}, nil, false, nil
	}
	return layout, request.Entrances, true, nil
}

// admit previews one tier's placements, audits colonist access with every
// tier's footprint impassable, and admits the tier as one Defense-purpose
// building method.
func (r *RoutineDefenseLayoutPlanner) admit(call, epoch context.Context, goal store.GoalState, state ControlState, read observation.RoutineReading, record store.DefenseLayoutRecord, tier store.DefenseTierRecord, buildings []domain.Building, key domain.MethodID) (RoutineDefenseLayoutResult, error) {
	p := r.reviewer.player
	projection := read.Projection
	digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%d/%s", goal.Goal.ID, goal.Goal.Epoch, key)))
	id := domain.PlanID(fmt.Sprintf("routine-defense-layout-%x", digest[:16]))
	snapshot := state.Snapshot
	snapshot.Plan, snapshot.Revision = id, 1
	var actions []domain.Action
	var previews []policy.Preview
	stock := policy.StockObservation{Snapshot: snapshot, Tick: projection.Identity.Tick}
	// A shooter floor the native preview refuses outright (terrain without
	// the floor affordance, say) is dropped from the tier instead of holding
	// it: the position keeps its cover and stays a firing cell, unfloored.
	unfloorable := map[domain.Cell]bool{}
	for _, building := range buildings {
		action, err := domain.NewBuildingAction(domain.ActionID(fmt.Sprintf("%s-%d", id, len(actions))), building)
		if err != nil {
			return RoutineDefenseLayoutResult{}, err
		}
		preview, ok, err := r.preview(call, action, snapshot, projection.Identity.Tick, building.Cell())
		if err != nil {
			return RoutineDefenseLayoutResult{}, err
		}
		if !ok && preview.NativeWorkPending {
			return RoutineDefenseLayoutResult{Reason: BuildingMethodUnknown, Tier: tier.Name, NativeWorkTicks: defenseNativeWorkTicks}, nil
		}
		if !ok && tier.Name == policy.TierFiringLine && building.Definition() == defenseDefinitions.Floor {
			unfloorable[building.Cell()] = true
			continue
		}
		if !ok {
			return RoutineDefenseLayoutResult{Reason: BuildingMethodUnknown, Tier: tier.Name}, nil
		}
		actions = append(actions, action)
		previews = append(previews, preview.Preview)
		if err = mergeRoutineStock(&stock, preview.Stock, len(actions) == 1); err != nil {
			return RoutineDefenseLayoutResult{}, err
		}
	}
	if len(unfloorable) > 0 {
		tier.Buildings = defenseWithoutFloors(tier.Buildings, unfloorable)
		record.SetTier(tier)
		clockSchedulerLog("defense-layout.admit: tier=%s floor refused natively on %v; those firing cells stay unfloored", tier.Name, unfloorable)
		if err := p.journal.SaveDefenseLayout(call, record); err != nil {
			return RoutineDefenseLayoutResult{}, err
		}
		if len(actions) == 0 {
			return RoutineDefenseLayoutResult{Reason: BuildingMethodUnknown, Tier: tier.Name}, nil
		}
	}
	// The audit blocks every impassable placement of the whole layout (walls,
	// fences and turrets), not the traps, conduits or floors: a spike trap
	// stays walkable and colonists cross their own with a negligible spring
	// chance, while the fenced safe lane leaves no trap-free route by
	// design, and a conduit or a floor lies under the pawn. Trap cells are
	// kept off the colonists' resting positions separately.
	blocked := map[domain.Cell]bool{}
	var blockedCells []domain.Cell
	for _, t := range record.Tiers {
		for _, b := range t.Buildings {
			if b.Definition == defenseDefinitions.Trap || b.Definition == defenseConduitDefinition || b.Definition == defenseDefinitions.Floor {
				continue
			}
			if !blocked[b.Cell] {
				blocked[b.Cell] = true
				blockedCells = append(blockedCells, b.Cell)
			}
		}
	}
	targets := append([]domain.Cell{record.Entry}, record.Entrances...)
	access, _, err := r.native.ReadSpatialAccess(call, boundary.Identity(state.Snapshot), blockedCells, targets, nil)
	var native *bridge.NativeUnavailable
	if errors.As(err, &native) {
		return RoutineDefenseLayoutResult{Reason: BuildingMethodUnknown, Tier: tier.Name}, nil
	}
	if err != nil {
		return RoutineDefenseLayoutResult{}, err
	}
	if err = r.sameTick(access.Context, state, projection.Identity.Tick); err != nil {
		return RoutineDefenseLayoutResult{}, err
	}
	if !access.Accepted() {
		return RoutineDefenseLayoutResult{Reason: BuildingMethodRefused, Tier: tier.Name}, nil
	}
	plan, err := domain.NewPlan(id, 1, actions)
	if err != nil {
		return RoutineDefenseLayoutResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutineDefenseLayoutResult{}, err
	}
	if p.session.State() != state {
		return RoutineDefenseLayoutResult{}, defenseControlErr(358)
	}
	last, _, err := r.reviewer.native.Identity(call)
	if err != nil {
		return RoutineDefenseLayoutResult{}, err
	}
	actual, err := observation.DecodeIdentity(last)
	if err != nil || !routineBuildingBoundary(actual, state.Snapshot, projection.Identity.Tick) {
		return RoutineDefenseLayoutResult{}, defenseControlErr(366)
	}
	now := r.reviewer.clock.Now()
	if now.Before(read.StartedAt) || now.Sub(read.StartedAt) > r.reviewer.maxAge {
		return RoutineDefenseLayoutResult{}, observation.ErrStale
	}
	decision, err := p.journal.AdmitBuildingMethod(call, store.BuildingMethodRequest{Goal: goal.Goal.ID, Revision: goal.Revision, Method: key, Plan: plan, Current: snapshot, Tick: projection.Identity.Tick, Bounds: domain.Known(projection.Bounds), Stock: stock, Rules: r.reviewer.rules, Previews: previews, Purpose: policy.Defense})
	if err != nil {
		return RoutineDefenseLayoutResult{}, err
	}
	reason := BuildingMethodRefused
	if decision.Admitted {
		reason = BuildingMethodAdmitted
		tier.Attempts++
		record.SetTier(tier)
		if err = p.journal.SaveDefenseLayout(call, record); err != nil {
			return RoutineDefenseLayoutResult{}, err
		}
	} else {
		clockSchedulerLog("defense-layout.admit: tier=%s refused=%+v", tier.Name, decision.Refused)
	}
	return RoutineDefenseLayoutResult{Reason: reason, Plan: id, Tier: tier.Name}, nil
}

// defenseWithoutFloors is the tier's buildings less the shooter floors on
// the given cells.
func defenseWithoutFloors(buildings []store.DefenseBuilding, cells map[domain.Cell]bool) []store.DefenseBuilding {
	kept := make([]store.DefenseBuilding, 0, len(buildings))
	for _, b := range buildings {
		if b.Definition != defenseDefinitions.Floor || !cells[b.Cell] {
			kept = append(kept, b)
		}
	}
	return kept
}

// defenseControlErr tags a control refusal with its source line so a sustained
// silent refusal is diagnosable from the clock worker's single log line.
func defenseControlErr(line int) error {
	return fmt.Errorf("%w (defense-layout:%d)", ErrControl, line)
}

func (r *RoutineDefenseLayoutPlanner) sameTick(observed *c.ObservationContext, state ControlState, tick domain.Tick) error {
	current, err := boundary.Context(observed, state.Snapshot)
	if err != nil || current.Native != state.Snapshot.Native || domain.Tick(observed.GetTick()) != tick {
		return defenseControlErr(388)
	}
	return nil
}

func (r *RoutineDefenseLayoutPlanner) preview(ctx context.Context, action domain.Action, snapshot domain.GenerationSnapshot, tick domain.Tick, cell domain.Cell) (bridge.BuildingPreview, bool, error) {
	preview, _, err := r.native.PreviewBuilding(ctx, action, snapshot)
	if err != nil {
		return bridge.BuildingPreview{}, false, err
	}
	v := preview.Preview
	if v.Action != action || !v.Snapshot.Matches(snapshot) || !v.Tick.FreshFor(tick) || !preview.Stock.Snapshot.Matches(snapshot) || !preview.Stock.Tick.FreshFor(tick) {
		return bridge.BuildingPreview{}, false, defenseControlErr(400)
	}
	footprint, fk := v.Footprint.Value()
	legal, lk := v.CanPlace.Value()
	safe, sk := v.SafeToPlace.Value()
	if _, mk := v.MadeFromStuff.Value(); !fk || !mk || !lk || !sk {
		return bridge.BuildingPreview{}, false, nil
	}
	if len(footprint) != 1 || footprint[0] != cell || !legal || !safe {
		// The refused preview is returned so the caller can tell a cell
		// already under native construction from one it cannot place on.
		return preview, false, nil
	}
	return preview, true, nil
}

// defenseRegion is the inclusive census rectangle centred on the colony,
// clipped to the map.
func defenseRegion(center domain.Cell, bounds policy.Bounds) bridge.CellRect {
	clamp := func(v, hi int32) int32 {
		if v < 0 {
			return 0
		}
		if v > hi {
			return hi
		}
		return v
	}
	return bridge.CellRect{Min: domain.Cell{X: clamp(center.X-defenseSiteHalfExtent, bounds.Width-1), Z: clamp(center.Z-defenseSiteHalfExtent, bounds.Height-1)},
		Max: domain.Cell{X: clamp(center.X+defenseSiteHalfExtent, bounds.Width-1), Z: clamp(center.Z+defenseSiteHalfExtent, bounds.Height-1)}}
}

// defenseRecordRegion is the census rectangle that covers every building the
// record placed, with a one-cell margin. The colonists' centre drifts as
// they work and wander, so a census around it can leave the layout outside
// the rectangle and count a standing tier as lost.
func defenseRecordRegion(record store.DefenseLayoutRecord, bounds policy.Bounds) bridge.CellRect {
	min, max := record.Chokepoint, record.Chokepoint
	for _, tier := range record.Tiers {
		for _, b := range tier.Buildings {
			min.X, min.Z = minInt32(min.X, b.Cell.X), minInt32(min.Z, b.Cell.Z)
			max.X, max.Z = maxInt32(max.X, b.Cell.X), maxInt32(max.Z, b.Cell.Z)
		}
	}
	clamp := func(v, hi int32) int32 { return maxInt32(0, minInt32(v, hi)) }
	return bridge.CellRect{Min: domain.Cell{X: clamp(min.X-1, bounds.Width-1), Z: clamp(min.Z-1, bounds.Height-1)},
		Max: domain.Cell{X: clamp(max.X+1, bounds.Width-1), Z: clamp(max.Z+1, bounds.Height-1)}}
}

// defenseCellFacts leaves every fact of a fogged cell unknown.
func defenseCellFacts(cell bridge.DefenseCell) policy.DefenseCell {
	out := policy.DefenseCell{Cell: cell.Cell}
	if cell.Fogged {
		return out
	}
	out.Walkable, out.Passable, out.BlocksSight = domain.Known(cell.Walkable), domain.Known(cell.Passable), domain.Known(cell.BlocksSight)
	out.PlayerOwned, out.NaturalRock, out.EdgeReachable = domain.Known(cell.PlayerOwned), domain.Known(cell.NaturalRock), domain.Known(cell.EdgeReachable)
	out.HomeArea, out.Door, out.CoverFill, out.Edifice = domain.Known(cell.HomeArea), domain.Known(cell.Door), domain.Known(cell.CoverFill), cell.EdificeDefName
	return out
}

// defenderRange counts colonists whose primary weapon is ranged and returns
// the shortest such range, unknown when nobody carries one.
func defenderRange(rows []*o.PawnState) (int, domain.Fact[float64]) {
	count := 0
	shortest := math.Inf(1)
	for _, row := range rows {
		equipment := row.GetEquipment()
		if equipment == nil || equipment.PrimaryId == nil {
			continue
		}
		for _, item := range equipment.Equipped {
			if item.GetThing().GetId() != equipment.GetPrimaryId() || !item.GetRanged() || item.Range == nil || item.GetRange() <= 0 {
				continue
			}
			count++
			shortest = math.Min(shortest, item.GetRange())
		}
	}
	if count == 0 {
		return 0, domain.Unknown[float64]()
	}
	if count > 8 {
		count = 8
	}
	return count, domain.Known(shortest)
}
