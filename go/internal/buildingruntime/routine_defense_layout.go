package buildingruntime

import (
	"context"
	"errors"
	"fmt"
	"math"
	"slices"
	"sort"
	"strconv"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/snapshot"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// defenseDefinitions are the native buildings each tier places. Wood keeps
// the first layout affordable (native Sandbags need fabric or leather, which
// a young colony rarely holds; a wooden Barricade gives the same 0.55 cover;
// policy.DefenseCoverChoice swaps in Sandbags once the stock covers them and
// names the Embrasure where the game has one, #868);
// MaintainStoneShell upgrades flammable walls afterwards through its own goal.
// The wood floor under each shooter needs no research and keeps the firing
// cell free of the trees that grew onto it before (#224).
var defenseDefinitions = policy.DefenseDefinitions{Sandbag: "Barricade", SandbagStuff: "WoodLog", Wall: "Wall", WallStuff: "WoodLog", Fence: "Fence", FenceStuff: "WoodLog", Trap: "TrapSpike", TrapStuff: "WoodLog", Door: "Door", DoorStuff: "WoodLog", Floor: "WoodPlankFloor", Bait: "Stool", BaitStuff: "WoodLog"}

// The powered turret tier (#61): the mini turret needs no rearming, and a
// conduit chain connects it to the network. Both are planning definitions
// the reviewer's census is asked for, so availability (research, content),
// draw and cost are observed, never assumed. policy.TurretBudget bounds the
// tier by the observed raid points (#396); each turret costs steel and
// components the colony may need first.
const (
	// defenseTurretDefinition is the ladder's bottom rung; the tier places
	// the highest rung the armory tier allows and its gates open (#1210).
	defenseTurretDefinition  = policy.TurretMini
	defenseConduitDefinition = "HiddenConduit"
	// defenseMortarDefinition is the mortar tier (#1206): research-gated
	// through its planning definition, unpowered, budgeted by
	// policy.MortarBudget.
	defenseMortarDefinition = "Turret_Mortar"
)

// The approach IEDs (#1209), each gated on its own research and content.
const (
	defenseIEDHighExplosive = "TrapIED_HighExplosive"
	defenseIEDIncendiary    = "TrapIED_Incendiary"
)

var defenseExtraDefinitions = []string{policy.TurretMini, policy.TurretAutocannon, policy.TurretSniper, defenseMortarDefinition, defenseConduitDefinition, policy.DefenseSandbags, policy.DefenseEmbrasure, defenseIEDHighExplosive, defenseIEDIncendiary}

// defenseDefinitionAvailable reports a planning definition the census
// observed as available, its research finished: a definition the game
// lacks (Embrasure before Ideology/1.4) has no row and is unavailable.
func defenseDefinitionAvailable(read observation.RoutineReading, name string) bool {
	research, rk := read.Projection.Facts.Research.Value()
	for _, d := range read.Projection.Definitions {
		if d.Name != name {
			continue
		}
		if v, k := d.Available.Value(); !k || !v || len(d.Research) > 0 && !rk {
			return false
		}
		for _, prerequisite := range d.Research {
			if !slices.Contains(research.Finished, policy.ResearchProjectID(prerequisite)) {
				return false
			}
		}
		return true
	}
	return false
}

// defenseTierOrder is the staged construction order; a tier without
// placements (the chokepoint reuses existing geometry) is complete as-is.
var defenseTierOrder = []policy.DefenseTierName{policy.TierChokepoint, policy.TierFiringLine, policy.TierFunnel, policy.TierTrapCorridor, policy.TierTurrets, policy.TierMortars, policy.TierBait, policy.TierIEDs}

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
		return nil, fmt.Errorf("%w: NewRoutineDefenseLayoutPlanner: reviewer == nil || native == nil", ErrControl)
	}
	if reviewer.native == nil {
		return nil, fmt.Errorf("%w: NewRoutineDefenseLayoutPlanner: reviewer.native == nil", ErrControl)
	}
	return &RoutineDefenseLayoutPlanner{reviewer, native}, nil
}

// A tier's method is keyed by tier, repair and attempt: a plan cancelled by
// an authority discontinuity (a letter pause) or refused natively is retried
// with a fresh method rather than counted as built, up to a small bound per
// repair. A tier the census re-opened after it stood (a sprung trap, a
// breached wall) restarts its attempts under the next repair number, so
// the repair's plan id never collides with the plan that built it (#331).
const maxDefenseTierAttempts = 4

func defenseTierPrefix(tier policy.DefenseTierName) string { return "defense-" + string(tier) + "-" }

func defenseTierMethodID(tier store.DefenseTierRecord) domain.MethodID {
	if tier.Reopened == 0 {
		return domain.MethodID(fmt.Sprintf("%s%d", defenseTierPrefix(tier.Name), tier.Attempts))
	}
	return domain.MethodID(fmt.Sprintf("%sr%d-%d", defenseTierPrefix(tier.Name), tier.Reopened, tier.Attempts))
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
	if !found || goal.Goal.Status != domain.GoalActive {
		return RoutineDefenseLayoutResult{Reason: BuildingMethodNoDeficit}, nil
	}
	// A fight waiting behind its rooms' doors (#1065) hardens them whether
	// or not the layout itself is short.
	wait, err := defenseWaitingFight(call, p.journal, world)
	if err != nil {
		return RoutineDefenseLayoutResult{}, err
	}
	if wait == nil && (goal.Goal.Need != domain.NeedDeficit || review.Veto(goal.Goal) != "") {
		return RoutineDefenseLayoutResult{Reason: BuildingMethodNoDeficit}, nil
	}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoutineDefenseLayoutResult{}, err
		}
		if store.PlanOpen(plan) {
			return RoutineDefenseLayoutResult{Reason: BuildingMethodExistingWork}, nil
		}
	}
	if wait != nil {
		return r.fight(call, epoch, goal, state, *wait)
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
	// A layout from before the plan's killbox (#789) is proposed afresh.
	stored = stored && record.Anchored
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
	expected, err := routineScope(call, r.reviewer.native)
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
	if !stored || defenseTurretsDue(record, expected.Tick) || defenseMortarsDue(record, expected.Tick) || defenseReplaceDue(record, expected.Tick) {
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
		if _, err = defensePerimeterTiers(&record, read); err != nil {
			return RoutineDefenseLayoutResult{}, err
		}
		if err = p.journal.SaveDefenseLayout(call, record); err != nil {
			return RoutineDefenseLayoutResult{}, err
		}
	} else if recut, err := defensePerimeterTiers(&record, read); err != nil {
		return RoutineDefenseLayoutResult{}, err
	} else if recut {
		// The plan's perimeter changed (#954): the new cut is built
		// behind removals of what it no longer wants.
		clockSchedulerLog("defense-layout: perimeter re-cut, revision %d", record.PerimeterRevision)
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
	if len(definitions) > 0 && stored && defenseTurretsDue(record, expected.Tick) {
		request := defenseTurretRequest(read)
		if request.TurretGatesOpen() {
			if err = r.proposeTurrets(call, state, read, &record); err != nil {
				return RoutineDefenseLayoutResult{}, err
			}
		} else {
			clockSchedulerLog("defense-layout: turret gates closed %s", defenseTurretGates(request))
		}
	}
	// The mortar tier follows the same way once mortar research and the
	// raid points' budget open its gates (#1206).
	if len(definitions) > 0 && defenseMortarsDue(record, expected.Tick) {
		if request := defenseMortarRequest(read); request.MortarGatesOpen() {
			if err = r.proposeMortars(call, state, read, &record); err != nil {
				return RoutineDefenseLayoutResult{}, err
			}
		}
	}
	// A standing turret below the armory's rung is replaced in place, one
	// at a time (#1210).
	if len(definitions) > 0 && stored && defenseReplaceDue(record, expected.Tick) {
		if err = r.replaceTurret(call, state, read, &record); err != nil {
			return RoutineDefenseLayoutResult{}, err
		}
	}
	order := append([]policy.DefenseTierName{}, defenseTierOrder...)
	for _, t := range record.Tiers {
		if policy.IsPerimeterTier(t.Name) {
			order = append(order, t.Name)
		}
		// A replacement's removal comes before the turret tier rebuilds.
		if strings.HasPrefix(string(t.Name), defenseTurretReplacePrefix) {
			order = slices.Insert(order, slices.Index(order, policy.TierTurrets), t.Name)
		}
	}
	for _, name := range order {
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
			return RoutineDefenseLayoutResult{Reason: BuildingMethodExhausted, Tier: name}, nil
		}
		if tier.Remove {
			return r.remove(call, epoch, goal, state, read, record, tier, census)
		}
		buildings = defenseMissingBuildings(buildings, census)
		if len(buildings) == 0 {
			// The census re-opened the tier on a cell it cannot see
			// (fogged); nothing can be admitted until it can.
			return RoutineDefenseLayoutResult{Reason: BuildingMethodUnknown, Tier: name}, nil
		}
		if policy.IsPerimeterTier(name) && defenseNeedsStone(buildings) {
			// The wall is stone: the stock's most plentiful block, waited
			// for the way MaintainStoneShell waits for its material. A
			// wall with its stuff set (wood on soft ground) and a bridge
			// keep theirs.
			stuff, ok := defensePerimeterStone(read.Projection, buildings)
			if !ok {
				if gate := policy.ResearchGate([]string{policy.StoneShellResearch}, read.Projection.Facts.Research); gate != "" {
					return RoutineDefenseLayoutResult{Reason: researchWaitReason(gate), Tier: name}, nil
				}
				return RoutineDefenseLayoutResult{Reason: defensePerimeterNoStone, Tier: name}, nil
			}
			for i, b := range buildings {
				if !defenseStoneBuilding(b) {
					continue
				}
				if buildings[i], err = domain.NewBuilding(b.Definition(), b.Cell(), b.Rotation(), stuff); err != nil {
					return RoutineDefenseLayoutResult{}, err
				}
			}
		}
		return r.admit(call, epoch, goal, state, read, record, tier, buildings, defenseTierMethodID(tier))
	}
	record.Complete, record.VerifiedTick, record.VerifiedCombat = true, tick, combat
	// Every tier stands, so combat holds the proven line; a standing turret
	// without power or with an empty barrel is still a deficit the layout
	// waits on (the network's fuel or generation is EnsureBasicPower's, a
	// lost conduit is re-placed above once the census misses it; an empty
	// barrel is rearmed below, or its fuel raised as a resource need). A
	// solar flare darkens every turret for the outage: the tier is absent
	// for its duration, not a deficit (#408).
	workers, _ := read.Projection.WorkPawns.Value()
	upkeep := policy.DefenseRearmTurrets(defenseTurretFacts(record, census), workers, read.Projection.Resources, policy.SolarFlareHold(read.Projection.Facts.DisasterConditions))
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
	// The line stands: raider cover inside its engagement zone is the
	// remaining deficit (#581).
	if result, handled, err := r.clearCover(call, epoch, goal, review, state, read, record); handled || err != nil {
		return result, err
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
		return RoutineDefenseLayoutResult{Reason: BuildingMethodExhausted, Tier: policy.TierTurrets}, nil
	}
	if arbiter == nil || !arbiter.tryClaim([]domain.PawnID{domain.PawnID(order.Pawn)}, "defense-rearm:"+order.Turret) {
		return RoutineDefenseLayoutResult{Reason: BuildingMethodUsed, Tier: policy.TierTurrets}, nil
	}
	method := domain.MethodID(fmt.Sprintf("%s%d", defenseRearmPrefix(order.Turret), tick))
	id := domain.MintPlanID()
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
				facts.Definition, facts.DPS = site.Definition, site.TurretDPS
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

// defenseMortarsDue is defenseTurretsDue for the mortar tier.
func defenseMortarsDue(record store.DefenseLayoutRecord, tick domain.Tick) bool {
	mortars, _, ok := record.Tier(policy.TierMortars)
	return (!ok || len(mortars.Buildings) == 0) && (record.MortarsProbedTick == 0 || tick-record.MortarsProbedTick >= defenseReverifyTicks)
}

// defenseMortarRequest is the mortar tier's observed gates: the mortar
// planning definition's availability re-checked against finished research,
// its stuff and cost, the stock census and the raid points' budget.
func defenseMortarRequest(read observation.RoutineReading) policy.DefenseRequest {
	projection := read.Projection
	request := policy.DefenseRequest{Definitions: defenseDefinitions, UnitCosts: map[string][]policy.Amount{}}
	request.Mortar = policy.DefenseMortarRequest{Definition: defenseMortarDefinition, Stock: projection.Resources, Max: policy.MortarBudget(projection.Facts.RaidPoints), Available: domain.Known(defenseDefinitionAvailable(read, defenseMortarDefinition))}
	for _, d := range projection.Definitions {
		if d.Name != defenseMortarDefinition {
			continue
		}
		if costs, known := d.Costs.Value(); known {
			request.UnitCosts[d.Name] = append([]policy.Amount{}, costs...)
		}
		if stuff, known := d.Stuff.Value(); known {
			request.Mortar.Stuff = stuff
		}
	}
	return request
}

// proposeMortars reads the census around the colony and sites the mortar
// tier against the stored geometry and turret tier, recording it pending
// when it places anything; the probe tick is recorded either way.
func (r *RoutineDefenseLayoutPlanner) proposeMortars(call context.Context, state ControlState, read observation.RoutineReading, record *store.DefenseLayoutRecord) error {
	projection := read.Projection
	identity := boundary.Identity(state.Snapshot)
	killbox, region, home, ok := defenseKillbox(projection)
	if !ok {
		return nil
	}
	site, _, err := r.native.ReadDefenseSite(call, identity, region)
	if err != nil {
		return err
	}
	if err = r.sameTick(site.Context, state, projection.Identity.Tick); err != nil {
		return err
	}
	request := defenseMortarRequest(read)
	request.Bounds, request.Home, request.Killbox = projection.Bounds, home, killbox
	request.Region = policy.Rectangle{X: region.Min.X, Z: region.Min.Z, Width: region.Max.X - region.Min.X + 1, Height: region.Max.Z - region.Min.Z + 1}
	for _, cell := range site.Cells {
		request.Cells = append(request.Cells, defenseCellFacts(cell))
	}
	geometry := defenseMortarGeometry(*record)
	record.MortarsProbedTick = projection.Identity.Tick
	tier, err := policy.DefenseMortars(request, geometry)
	clockSchedulerLog("defense-layout: mortar probe buildings=%d costs=%v max=%d err=%v", len(tier.Buildings), tier.Costs, request.Mortar.Max, err)
	if err == nil && len(tier.Buildings) > 0 {
		record.SetPolicyTier(tier)
	}
	return r.reviewer.player.journal.SaveDefenseLayout(call, *record)
}

// defenseMortarGeometry is the stored layout with the turret tier's cells,
// as the mortar tier sees it.
func defenseMortarGeometry(record store.DefenseLayoutRecord) policy.DefenseGeometry {
	g := defenseRecordGeometry(record)
	if turrets, _, ok := record.Tier(policy.TierTurrets); ok {
		g.Turrets = append(g.Turrets, turrets.Reserved...)
		for _, b := range turrets.Buildings {
			g.Turrets = append(g.Turrets, b.Cell)
		}
	}
	return g
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
	// cover identifies the removable thing on each cell, unbridging the
	// cells whose foundation is designated for removal (#954).
	cover      map[domain.Cell]*bridge.DefenseCover
	unbridging map[domain.Cell]bool
}

// standing reports whether the building's cell carries it: a conduit by the
// conduit census, the firing-line floor by the cell's terrain, anything
// else by its edifice.
func (c *defenseCensus) standing(definition string, cell domain.Cell) bool {
	if definition == defenseConduitDefinition {
		return c.conduits[cell]
	}
	if defenseTerrain(definition) {
		return c.terrain[cell] == definition
	}
	return c.edifice[cell] == definition
}

// defenseTurretRequest is the turret tier's observed gates from the shared
// routine reading: the turret and conduit planning definitions (availability
// re-checked against the research snapshot's finished projects), the
// network with the most spare watts and the conduits that carry it, the
// stock census, and the budget the census's observed raid points buy
// (#341); an unknown reading keeps the base budget.
func defenseTurretRequest(read observation.RoutineReading) policy.DefenseRequest {
	projection := read.Projection
	request := policy.DefenseRequest{Definitions: defenseDefinitions, UnitCosts: map[string][]policy.Amount{}}
	request.Turret = policy.DefenseTurretRequest{Definition: defenseTurretRung(read), Conduit: defenseConduitDefinition, Stock: projection.Resources, Max: policy.TurretBudget(projection.Facts.RaidPoints)}
	research, rk := projection.Facts.Research.Value()
	finished := map[policy.ResearchProjectID]bool{}
	for _, id := range research.Finished {
		finished[id] = true
	}
	for _, d := range projection.Definitions {
		if d.Name != request.Turret.Definition && d.Name != defenseConduitDefinition {
			continue
		}
		if costs, known := d.Costs.Value(); known {
			request.UnitCosts[d.Name] = append([]policy.Amount{}, costs...)
		}
		if d.Name != request.Turret.Definition {
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
		if size, known := d.Size.Value(); known {
			request.Turret.Size = size
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

// defenseTurretRung is the turret definition the tier places (#1210): the
// highest rung the armory tier (raid points capped by research) allows
// whose planning definition is observed available, else the mini turret.
func defenseTurretRung(read observation.RoutineReading) string {
	facts := read.Projection.Facts
	for _, rung := range policy.TurretRungs(policy.AssessArmory(facts.RaidPoints, facts.Research).Tier) {
		if defenseDefinitionAvailable(read, rung) {
			return rung
		}
	}
	return policy.TurretMini
}

// defenseTurretSize is a ladder definition's observed North footprint.
func defenseTurretSize(read observation.RoutineReading, definition string) policy.Bounds {
	for _, d := range read.Projection.Definitions {
		if d.Name == definition {
			size, _ := d.Size.Value()
			return size
		}
	}
	return policy.Bounds{}
}

// defenseTurretReplacePrefix names the removal tier that deconstructs one
// standing turret for a higher rung rebuilt on its cell (#1210); the name
// carries the cell and the new rung so each replacement's methods are
// keyed apart.
const defenseTurretReplacePrefix = "turrets-replace-"

func defenseTurretReplacing(record store.DefenseLayoutRecord) bool {
	for _, t := range record.Tiers {
		if strings.HasPrefix(string(t.Name), defenseTurretReplacePrefix) {
			return true
		}
	}
	return false
}

// defenseReplaceDue reports a built turret tier with nothing being
// replaced whose probe interval has elapsed.
func defenseReplaceDue(record store.DefenseLayoutRecord, tick domain.Tick) bool {
	turrets, _, ok := record.Tier(policy.TierTurrets)
	return ok && turrets.Built && len(turrets.Buildings) > 0 && !defenseTurretReplacing(record) && (record.TurretsProbedTick == 0 || tick-record.TurretsProbedTick >= defenseReverifyTicks)
}

// defenseReplaceTurret replaces one standing turret below the current rung
// in place (#1210): never while a hostile is on the map (combat or a raid
// on its way in), one at a time. The chosen turret goes into a removal tier
// ordered before the turret tier, and the turret tier records the new rung
// on the same anchor, so the census re-opens it and the missing building is
// admitted once the old one is gone.
func (r *RoutineDefenseLayoutPlanner) replaceTurret(call context.Context, state ControlState, read observation.RoutineReading, record *store.DefenseLayoutRecord) error {
	projection := read.Projection
	record.TurretsProbedTick = projection.Identity.Tick
	if hostiles, known := projection.Facts.Hostiles.Value(); !known || hostiles > 0 {
		return r.reviewer.player.journal.SaveDefenseLayout(call, *record)
	}
	request := defenseTurretRequest(read)
	tier, _, _ := record.Tier(policy.TierTurrets)
	var standing []policy.StandingTurret
	for _, b := range tier.Buildings {
		if policy.TurretRank(b.Definition) < 0 {
			continue
		}
		building, err := domain.NewBuilding(b.Definition, b.Cell, b.Rotation, b.Stuff)
		if err != nil {
			return err
		}
		standing = append(standing, policy.StandingTurret{Building: building, Cells: policy.TurretFootprint(b.Cell, defenseTurretSize(read, b.Definition))})
	}
	if len(standing) == 0 || slices.IndexFunc(standing, func(t policy.StandingTurret) bool {
		return policy.TurretRank(t.Building.Definition()) < policy.TurretRank(request.Turret.Definition)
	}) < 0 {
		return r.reviewer.player.journal.SaveDefenseLayout(call, *record)
	}
	identity := boundary.Identity(state.Snapshot)
	killbox, region, home, ok := defenseKillbox(projection)
	if !ok {
		return r.reviewer.player.journal.SaveDefenseLayout(call, *record)
	}
	site, _, err := r.native.ReadDefenseSite(call, identity, region)
	if err != nil {
		return err
	}
	if err = r.sameTick(site.Context, state, projection.Identity.Tick); err != nil {
		return err
	}
	request.Bounds, request.Home, request.Killbox = projection.Bounds, home, killbox
	request.Region = policy.Rectangle{X: region.Min.X, Z: region.Min.Z, Width: region.Max.X - region.Min.X + 1, Height: region.Max.Z - region.Min.Z + 1}
	for _, cell := range site.Cells {
		request.Cells = append(request.Cells, defenseCellFacts(cell))
	}
	old, replacement, ok, err := policy.TurretReplacement(request, defenseRecordGeometry(*record), standing)
	clockSchedulerLog("defense-layout: turret replacement rung=%s ok=%v old=%v err=%v %s", request.Turret.Definition, ok, old.Cell(), err, defenseTurretGates(request))
	if err != nil || !ok {
		return r.reviewer.player.journal.SaveDefenseLayout(call, *record)
	}
	applyTurretReplacement(record, old, replacement, standing, request.Turret.Size)
	return r.reviewer.player.journal.SaveDefenseLayout(call, *record)
}

// applyTurretReplacement records one in-place replacement: a removal tier
// for the old turret and the new rung on its anchor in the turret tier,
// whose reservation grows to the new footprint.
func applyTurretReplacement(record *store.DefenseLayoutRecord, old, replacement domain.Building, standing []policy.StandingTurret, size policy.Bounds) {
	tier, _, _ := record.Tier(policy.TierTurrets)
	for i, b := range tier.Buildings {
		if b.Cell == old.Cell() && b.Definition == old.Definition() {
			tier.Buildings[i] = store.DefenseBuilding{Definition: replacement.Definition(), Cell: replacement.Cell(), Rotation: replacement.Rotation(), Stuff: replacement.Stuff()}
		}
	}
	tier.Reserved = nil
	for _, t := range standing {
		if t.Building.Cell() == old.Cell() {
			tier.Reserved = append(tier.Reserved, policy.TurretFootprint(old.Cell(), size)...)
			continue
		}
		tier.Reserved = append(tier.Reserved, t.Cells...)
	}
	record.SetTier(tier)
	name := policy.DefenseTierName(fmt.Sprintf("%s%d-%d-%d", defenseTurretReplacePrefix, old.Cell().X, old.Cell().Z, policy.TurretRank(replacement.Definition())))
	record.Tiers = append(record.Tiers, store.DefenseTierRecord{Name: name, Remove: true, Buildings: []store.DefenseBuilding{{Definition: old.Definition(), Cell: old.Cell(), Rotation: old.Rotation(), Stuff: old.Stuff()}}})
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
	killbox, region, home, ok := defenseKillbox(projection)
	if !ok {
		return nil
	}
	site, _, err := r.native.ReadDefenseSite(call, identity, region)
	if err != nil {
		return err
	}
	if err = r.sameTick(site.Context, state, projection.Identity.Tick); err != nil {
		return err
	}
	request := defenseTurretRequest(read)
	request.Bounds, request.Home, request.Killbox = projection.Bounds, home, killbox
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
	recordLayoutSnapshot(call, state.Snapshot, projection.Identity.Tick, snapshot.Layout{Point: snapshot.LayoutTurrets, Request: request, Geometry: geometry, Record: record})
	tier, verified, err := policy.DefenseTurrets(request, geometry)
	clockSchedulerLog("defense-layout: turret probe candidates=%+v lines=%d buildings=%d costs=%v err=%v %s", verified, len(lines.Lines), len(tier.Buildings), tier.Costs, err, defenseTurretGates(request))
	if err != nil || len(tier.Buildings) == 0 {
		return r.reviewer.player.journal.SaveDefenseLayout(call, *record)
	}
	record.SetPolicyTier(tier)
	return r.reviewer.player.journal.SaveDefenseLayout(call, *record)
}

// defenseRecordGeometry is the stored layout as the turret tier sees it.
func defenseRecordGeometry(record store.DefenseLayoutRecord) policy.DefenseGeometry {
	g := policy.DefenseGeometry{Entry: record.Entry, Approach: append([]domain.Cell{}, record.TrapLane...), Toward: record.Toward, Firing: append([]domain.Cell{}, record.Firing...), Lanes: append(append([]domain.Cell{}, record.TrapLane...), record.SafeLane...)}
	for _, tier := range record.Tiers {
		if tier.Name == policy.TierTurrets || policy.IsPerimeterTier(tier.Name) || strings.HasPrefix(string(tier.Name), defenseTurretReplacePrefix) {
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

// defenseCombatKey identifies the world's latest ActiveCombat incident, open
// or closed (#1020): each raid opens a new one, so a changed key marks a
// combat whose aftermath the layout has not verified. Empty before the
// first combat.
func defenseCombatKey(ctx context.Context, p *Player, review store.RoutineReview) (string, error) {
	s := review.Snapshot
	latest, ok, err := p.journal.LatestIncident(ctx, store.World{Colony: s.Colony, Load: s.Load, Map: s.Map}, policy.ActiveCombat)
	if err != nil || !ok {
		return "", err
	}
	return string(latest.Incident.ID), nil
}

// defenseTierCensus applies one census to the record: a tier whose
// buildings all stand is Built; a Built tier that lost a building is
// re-opened with a fresh retry budget. A removal tier none of whose
// buildings stands any more is done and leaves the record (#954). It
// reports whether anything changed.
func defenseTierCensus(record *store.DefenseLayoutRecord, census *defenseCensus) bool {
	gone := len(record.Tiers)
	record.Tiers = slices.DeleteFunc(record.Tiers, func(t store.DefenseTierRecord) bool { return t.Remove && defenseRemovalGone(t, census) })
	changed := gone != len(record.Tiers)
	for _, tier := range record.Tiers {
		if len(tier.Buildings) == 0 || tier.Remove {
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
			tier.Attempts, tier.Reopened = 0, tier.Reopened+1
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
	census := &defenseCensus{edifice: map[domain.Cell]string{}, terrain: map[domain.Cell]string{}, conduits: map[domain.Cell]bool{}, consumers: map[domain.Cell]policy.PowerSite{},
		cover: map[domain.Cell]*bridge.DefenseCover{}, unbridging: map[domain.Cell]bool{}}
	// One read covers the killbox and every perimeter section: each native
	// read costs a frame on a running clock, so a read per section outlasts
	// the optional planner cutoff and the ring is never admitted (#1360).
	site, _, err := r.native.ReadDefenseSite(call, boundary.Identity(state.Snapshot), defenseCensusRegion(*record, projection.Bounds))
	if err != nil {
		return nil, err
	}
	if err = r.sameTick(site.Context, state, projection.Identity.Tick); err != nil {
		return nil, err
	}
	for _, cell := range site.Cells {
		if !cell.Fogged {
			census.edifice[cell.Cell], census.terrain[cell.Cell] = cell.EdificeDefName, cell.Terrain
			census.cover[cell.Cell], census.unbridging[cell.Cell] = cell.Cover, cell.Unbridging
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
	review, err := r.player.journal.LoadRoutineReview(ctx)
	if err != nil {
		return nil, err
	}
	if review.Enabled && review.Snapshot == snapshot {
		needs = policy.MedicineResourceNeeds(needs, review.MedicineTarget)
		needs = policy.ResourceGoalTargets(needs, review.DependencyNeeds)
		needs = policy.ResourceGoalTargets(needs, policy.WoodFloorNeeds(review.WoodFloor))
		needs = policy.ResourceGoalTargets(needs, policy.ResourceRunwayTargets(review.ResourceRunwayState()))
		if review.BrewingFinished {
			needs = policy.ResourceGoalTargets(needs, policy.SocialDrugTargets(domain.Known(policy.ResearchFacts{Finished: []policy.ResearchProjectID{"Brewing"}})))
		}
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
	killbox, region, home, ok := defenseKillbox(projection)
	if !ok {
		clockSchedulerLog("defense-layout: waiting for the layout plan's killbox")
		return policy.DefenseLayout{}, nil, false, nil
	}
	site, _, err := r.native.ReadDefenseSite(call, identity, region)
	if err != nil {
		return policy.DefenseLayout{}, nil, false, err
	}
	if err = r.sameTick(site.Context, state, projection.Identity.Tick); err != nil {
		return policy.DefenseLayout{}, nil, false, err
	}
	request := defenseTurretRequest(read)
	request.Bounds, request.Home, request.Killbox = projection.Bounds, home, killbox
	// Observed arrivals rank the sectors; the bait tier stands only on a
	// sector a raid used (#1063).
	request.Arrivals, request.Tick = defenseArrivals(site.Raids, region), projection.Identity.Tick
	request.Region = policy.Rectangle{X: region.Min.X, Z: region.Min.Z, Width: region.Max.X - region.Min.X + 1, Height: region.Max.Z - region.Min.Z + 1}
	for _, cell := range site.Cells {
		request.Cells = append(request.Cells, defenseCellFacts(cell))
		if !cell.Fogged && cell.Door && cell.PlayerOwned {
			request.Entrances = append(request.Entrances, cell.Cell)
		}
	}
	defenders, minRange, ok, err := r.defenderRange(call, state, read)
	if err != nil || !ok {
		return policy.DefenseLayout{}, nil, false, err
	}
	request.Defenders, request.MinRange = defenders, minRange
	stock, stockKnown := projection.Resources.Value()
	request.Definitions = policy.DefenseCoverChoice(request.Definitions, stock, stockKnown,
		defenseDefinitionAvailable(read, policy.DefenseSandbags), defenseDefinitionAvailable(read, policy.DefenseEmbrasure), defenders)
	defenseIEDRequest(read, &request)
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
	recordLayoutSnapshot(call, state.Snapshot, projection.Identity.Tick, snapshot.Layout{Point: snapshot.LayoutPropose, Request: request})
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
	id := domain.MintPlanID()
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
		if !ok && (tier.Name == policy.TierFiringLine && building.Definition() == defenseDefinitions.Floor || policy.IsPerimeterTier(tier.Name)) {
			// A perimeter cell the game refuses (natural rock, a building
			// in the way) is left out of the wall; the terrain holds it.
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
		tier.Buildings = defenseWithoutFloors(tier.Buildings, unfloorable, policy.IsPerimeterTier(tier.Name))
		record.SetTier(tier)
		clockSchedulerLog("defense-layout.admit: tier=%s placement refused natively on %v; left out of the tier", tier.Name, unfloorable)
		if err := p.journal.SaveDefenseLayout(call, record); err != nil {
			return RoutineDefenseLayoutResult{}, err
		}
		if len(actions) == 0 {
			return RoutineDefenseLayoutResult{Reason: BuildingMethodUnknown, Tier: tier.Name}, nil
		}
	}
	// The audit blocks every placement of the whole layout that colonists
	// cannot pass (walls, fences and turrets), not the traps, the safe
	// lane's doors, conduits or floors: a spike trap stays walkable, a
	// colonist opens the colony's own door, and a conduit or a floor lies
	// under the pawn. The corridor's pricing keeps colonists off the trap
	// cells (#619); trap cells are kept off their resting positions
	// separately.
	blocked := map[domain.Cell]bool{}
	var blockedCells []domain.Cell
	for _, t := range record.Tiers {
		for _, b := range t.Buildings {
			if b.Definition == defenseDefinitions.Trap || b.Definition == defenseDefinitions.Door || b.Definition == defenseDefinitions.Bait || b.Definition == defenseConduitDefinition || defenseTerrain(b.Definition) {
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
	actual, err := routineScope(call, r.reviewer.native)
	if err != nil || !routineBuildingBoundary(actual, state.Snapshot, projection.Identity.Tick) {
		return RoutineDefenseLayoutResult{}, defenseControlErr(366)
	}
	now := r.reviewer.clock.Now()
	if now.Before(read.StartedAt) || now.Sub(read.StartedAt) > r.reviewer.maxAge {
		return RoutineDefenseLayoutResult{}, observation.ErrStale
	}
	decision, err := admitMethod(call, p.journal, store.BuildingMethodRequest{Goal: goal.Goal.ID, Revision: goal.Revision, Method: key, Plan: plan, Current: snapshot, Tick: projection.Identity.Tick, Bounds: domain.Known(projection.Bounds), Stock: stock, Previews: previews, Purpose: policy.Defense})
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
// the given cells, or less every building there for a perimeter section.
func defenseWithoutFloors(buildings []store.DefenseBuilding, cells map[domain.Cell]bool, perimeter bool) []store.DefenseBuilding {
	kept := make([]store.DefenseBuilding, 0, len(buildings))
	for _, b := range buildings {
		if b.Definition != defenseDefinitions.Floor && !perimeter || !cells[b.Cell] {
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
	if err != nil || current.Native != state.Snapshot.Native || domain.Tick(observed.GetTick()) < tick {
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

// defensePerimeterNoStone is a perimeter section waiting on stone blocks
// once Stonecutting is done.
const defensePerimeterNoStone RoutineBuildingReason = "no_perimeter_stone"

// Native stuff costs of a stone Wall and Door.
const (
	defenseWallStone = 5
	defenseDoorStone = 25
)

// defensePerimeterBridge is what water under the wall takes: a heavy
// bridge (and a stone wall) once researched, else a plain bridge (and a
// wooden wall) (#949).
func defensePerimeterBridge(projection observation.ColonyProjection) string {
	if research, known := projection.Facts.Research.Value(); known && slices.Contains(research.Finished, policy.ResearchProjectID(policy.PerimeterHeavyResearch)) {
		return policy.PerimeterHeavyBridge
	}
	return policy.PerimeterBridge
}

// defenseStoneBuilding is a perimeter wall or door still waiting for its
// stone: its stuff is unset.
func defenseStoneBuilding(b domain.Building) bool {
	return b.Stuff() == "" && (b.Definition() == defenseDefinitions.Wall || b.Definition() == defenseDefinitions.Door)
}

func defenseNeedsStone(buildings []domain.Building) bool {
	return slices.ContainsFunc(buildings, defenseStoneBuilding)
}

// defenseTerrain is a definition laid as terrain, not an edifice: the
// firing-line floor and the perimeter's bridges (#949).
func defenseTerrain(definition string) bool {
	return definition == defenseDefinitions.Floor || definition == policy.PerimeterBridge || definition == policy.PerimeterHeavyBridge
}

// defensePerimeterStone picks the stone for a section: the stone-block
// resource the colony holds most of (cut from the rock nearby), when it
// covers the whole section.
func defensePerimeterStone(projection observation.ColonyProjection, buildings []domain.Building) (string, bool) {
	stock, known := projection.Resources.Value()
	if !known {
		return "", false
	}
	need := int64(0)
	for _, b := range buildings {
		if !defenseStoneBuilding(b) {
			continue
		}
		if b.Definition() == defenseDefinitions.Door {
			need += defenseDoorStone
		} else {
			need += defenseWallStone
		}
	}
	best, most := "", int64(0)
	for resource, count := range stock {
		name := string(resource)
		if strings.HasPrefix(name, "Blocks") && (count > most || count == most && name < best) {
			best, most = name, count
		}
	}
	return best, best != "" && most >= need
}

// defenseKillbox reads the layout plan's killbox opening (#789): the anchor
// the corridor stands in, the census region around it and the home cell
// deep inside. ok is false until the plan (B1) holds an opening; the layout
// waits for it.
func defenseKillbox(projection observation.ColonyProjection) (policy.DefenseKillbox, bridge.CellRect, domain.Cell, bool) {
	plan, known := projection.LayoutPlan.Value()
	if !known {
		return policy.DefenseKillbox{}, bridge.CellRect{}, domain.Cell{}, false
	}
	k, region, home, ok := policy.LayoutKillbox(plan, projection.Bounds)
	if !ok {
		return policy.DefenseKillbox{}, bridge.CellRect{}, domain.Cell{}, false
	}
	return k, bridge.CellRect{Min: domain.Cell{X: region.X, Z: region.Z}, Max: domain.Cell{X: region.X + region.Width - 1, Z: region.Z + region.Height - 1}}, home, true
}

// defenseRecordRegion is the census rectangle that covers every building the
// record placed, with a one-cell margin. The colonists' centre drifts as
// they work and wander, so a census around it can leave the layout outside
// the rectangle and count a standing tier as lost.
//
// With a perimeter section named it covers that section alone; otherwise
// the killbox's tiers, never the perimeter.
func defenseRecordRegion(record store.DefenseLayoutRecord, bounds policy.Bounds, section policy.DefenseTierName) bridge.CellRect {
	min, max := record.Chokepoint, record.Chokepoint
	for _, tier := range record.Tiers {
		if section != "" && tier.Name == section && len(tier.Buildings) > 0 {
			min, max = tier.Buildings[0].Cell, tier.Buildings[0].Cell
		}
	}
	for _, tier := range record.Tiers {
		if tier.Name != section && (section != "" || policy.IsPerimeterTier(tier.Name)) {
			continue
		}
		for _, b := range tier.Buildings {
			min.X, min.Z = minInt32(min.X, b.Cell.X), minInt32(min.Z, b.Cell.Z)
			max.X, max.Z = maxInt32(max.X, b.Cell.X), maxInt32(max.Z, b.Cell.Z)
		}
	}
	clamp := func(v, hi int32) int32 { return maxInt32(0, minInt32(v, hi)) }
	return bridge.CellRect{Min: domain.Cell{X: clamp(min.X-1, bounds.Width-1), Z: clamp(min.Z-1, bounds.Height-1)},
		Max: domain.Cell{X: clamp(max.X+1, bounds.Width-1), Z: clamp(max.Z+1, bounds.Height-1)}}
}

// defenseCensusRegion is the one rectangle the tier census reads: the
// killbox's region joined with every perimeter section's.
func defenseCensusRegion(record store.DefenseLayoutRecord, bounds policy.Bounds) bridge.CellRect {
	out := defenseRecordRegion(record, bounds, "")
	for _, tier := range record.Tiers {
		if !policy.IsPerimeterTier(tier.Name) || len(tier.Buildings) == 0 {
			continue
		}
		r := defenseRecordRegion(record, bounds, tier.Name)
		out.Min.X, out.Min.Z = minInt32(out.Min.X, r.Min.X), minInt32(out.Min.Z, r.Min.Z)
		out.Max.X, out.Max.Z = maxInt32(out.Max.X, r.Max.X), maxInt32(out.Max.Z, r.Max.Z)
	}
	return out
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
	out.Roofed = domain.Known(cell.Roofed)
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

// defenseIEDRequest adds the approach IEDs (#1209) the census allows, each
// gated independently and carrying its native explosive radius, and every
// stockpile cell as the flammable storage their blast must avoid. A zone
// census not yet held leaves storage unknown, which places no IED.
func defenseIEDRequest(read observation.RoutineReading, request *policy.DefenseRequest) {
	projection := read.Projection
	for _, d := range projection.Definitions {
		if d.Name != defenseIEDHighExplosive && d.Name != defenseIEDIncendiary {
			continue
		}
		radius, known := d.ExplosiveRadius.Value()
		if !known || !defenseDefinitionAvailable(read, d.Name) {
			continue
		}
		request.IEDs = append(request.IEDs, policy.DefenseIED{Definition: d.Name, Radius: radius, Incendiary: d.Name == defenseIEDIncendiary})
		if costs, known := d.Costs.Value(); known {
			request.UnitCosts[d.Name] = append([]policy.Amount{}, costs...)
		}
	}
	// The high-explosive IED is preferred where both fit.
	slices.SortFunc(request.IEDs, func(a, b policy.DefenseIED) int {
		return strings.Compare(a.Definition, b.Definition)
	})
	if !projection.Zones.Complete {
		return
	}
	stockpiles := map[string]bool{}
	for _, row := range projection.Zones.Value.Rows {
		if row.GetType() == "stockpile" {
			stockpiles[row.GetId()] = true
		}
	}
	storage := []domain.Cell{}
	for _, cell := range projection.Cells {
		if id, known := cell.ZoneID.Value(); known && stockpiles[id] {
			storage = append(storage, cell.Cell)
		}
	}
	request.FlammableStorage = domain.Known(storage)
}
