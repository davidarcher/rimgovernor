package buildingruntime

import (
	"context"
	"errors"
	"fmt"
	"log/slog"
	"math"
	"slices"
	"sort"
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
	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
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
func defenseDefinitionAvailable(read observation.RoundsReading, name string) bool {
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

// defenseRoamerOwned reports a roamer (a race that needs a pen) in the animal
// census. An unknown census or row is not one: ordering stays unchanged (#2230).
func defenseRoamerOwned(projection observation.ColonyProjection) bool {
	animals, known := projection.Facts.AnimalUpkeep.Animals.Value()
	if !known {
		return false
	}
	for _, a := range animals {
		if pen, ok := a.RequiresPen.Value(); ok && pen {
			return true
		}
	}
	return false
}

// defenseTierSequence is the order tiers are built in: the fixed tiers, then
// the perimeter sections, with a replacement's removal before the turret
// tier. With a roamer owned, the core ring's unbuilt sections (and so its
// gates) come first, so the paddock closes sooner (#2230).
func defenseTierSequence(record store.DefenseLayoutRecord, roamerOwned bool) []policy.DefenseTierName {
	order := append([]policy.DefenseTierName{}, defenseTierOrder...)
	var ring []policy.DefenseTierName
	for _, t := range record.Tiers {
		if policy.IsPerimeterTier(t.Name) {
			if roamerOwned && !t.Built && !t.Remove && policy.IsCorePerimeterTier(t.Name) {
				ring = append(ring, t.Name)
			} else {
				order = append(order, t.Name)
			}
		}
		// A replacement's removal comes before the turret tier rebuilds.
		if strings.HasPrefix(string(t.Name), defenseTurretReplacePrefix) {
			order = slices.Insert(order, slices.Index(order, policy.TierTurrets), t.Name)
		}
	}
	return append(ring, order...)
}

// RoundsDefenseLayoutSource is the native read set the planner needs beyond
// the reviewer's shared colony observation: the census rectangle, shooting
// lines, the access audit, defender gear and placement previews.
type RoundsDefenseLayoutSource interface {
	ReadDefenseSite(context.Context, *c.Identity, bridge.CellRect) (bridge.DefenseSite, bridge.Result, error)
	ReadLinesOfFire(context.Context, *c.Identity, []domain.Cell, []domain.Cell) (bridge.LinesOfFire, bridge.Result, error)
	ReadSpatialAccess(context.Context, *c.Identity, []domain.Cell, []domain.Cell, []string) (bridge.SpatialAccess, bridge.Result, error)
	ReadCombatPawns(context.Context, *c.Identity, []string) (*o.ListPawnsReply, bridge.Result, error)
	PreviewBuildings(context.Context, []domain.Action, domain.GenerationSnapshot) ([]bridge.BuildingPreview, bridge.Result, error)
}
type RoundsDefenseLayoutPlanner struct {
	reviewer *Rounder
	native   RoundsDefenseLayoutSource
	// unbuilt is the last census summary logged per unbuilt tier, so a tier that
	// stays unbuilt is logged when its cells change, not on every step.
	unbuilt map[policy.DefenseTierName]string
}
type RoundsDefenseLayoutResult struct {
	Verdict
	Plan domain.PlanID
	Tier policy.DefenseTierName
	// NativeWorkTicks asks for a clock window without a plan of its own: a
	// tier's missing building already has a blueprint or frame on its cell
	// (a sprung trap's auto-rearm), so native construction restores it.
	NativeWorkTicks uint32
}

// defenseNativeWorkTicks is the window admitted while a tier waits on a
// blueprint the game placed itself.
const defenseNativeWorkTicks = 2500

func NewRoundsDefenseLayoutPlanner(reviewer *Rounder, native RoundsDefenseLayoutSource) (*RoundsDefenseLayoutPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, fmt.Errorf("%w: NewRoundsDefenseLayoutPlanner: reviewer == nil || native == nil", ErrControl)
	}
	if reviewer.native == nil {
		return nil, fmt.Errorf("%w: NewRoundsDefenseLayoutPlanner: reviewer.native == nil", ErrControl)
	}
	return &RoundsDefenseLayoutPlanner{reviewer: reviewer, native: native}, nil
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
// for EnsureDefensiveLayout when the routine policy opts in.
func defenseLayoutProject(ctx context.Context, p *Player, review store.Rounds) (store.ProjectState, bool, error) {
	if id, bound := review.ProjectFor(policy.EnsureDefensiveLayout); bound {
		project, err := p.journal.LoadProject(ctx, id)
		return project, err == nil, err
	}
	return store.ProjectState{}, false, nil
}

func (r *RoundsDefenseLayoutPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoundsDefenseLayoutResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoundsDefenseLayoutResult{Verdict: BuildingReasonDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoundsDefenseLayoutResult{}, defenseControlErr(113)
	}
	review, err := p.journal.LoadRounds(call)
	if err != nil {
		return RoundsDefenseLayoutResult{}, err
	}
	if !review.Enabled || review.Snapshot != state.Snapshot {
		return RoundsDefenseLayoutResult{Verdict: BuildingReasonNoReview}, nil
	}
	world := store.World{Colony: state.Snapshot.Colony, Load: state.Snapshot.Load, Map: state.Snapshot.Map}
	goal, found, err := defenseLayoutProject(call, p, review)
	if err != nil {
		return RoundsDefenseLayoutResult{}, err
	}
	if !found || goal.Project.Status != domain.ProjectOpen {
		return RoundsDefenseLayoutResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	// A fight waiting behind its rooms' doors (#1065) hardens them whether
	// or not the layout itself is short.
	wait, err := defenseWaitingFight(call, p.journal, world)
	if err != nil {
		return RoundsDefenseLayoutResult{}, err
	}
	if wait == nil && (goal.Project.Finding != domain.FindingUnmet || review.VetoProject(goal.Project) != "") {
		return RoundsDefenseLayoutResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	// Open perimeter tiers do not hold the layout: more of the wall is admitted
	// beside them while the stock funds it (#2316). Any other open plan, or a
	// fight, still does.
	pipe, pipelinable, err := defenseOpenTiers(goal, func(id domain.PlanID) (store.PlanState, error) { return p.journal.LoadPlan(call, id) })
	if err != nil {
		return RoundsDefenseLayoutResult{}, err
	}
	if !pipelinable || wait != nil && pipe.piped() {
		return RoundsDefenseLayoutResult{Verdict: BuildingReasonExistingWork}, nil
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
		selected = selected || row.Concern == policy.EnsureDefensiveLayout && row.Selected
	}
	if !selected {
		return RoundsDefenseLayoutResult{Verdict: awaitingSlot(string(policy.EnsureDefensiveLayout))}, nil
	}
	record, stored, err := p.journal.LoadDefenseLayout(call, world)
	if err != nil {
		return RoundsDefenseLayoutResult{}, err
	}
	// A layout from before the plan's killbox (#789) is proposed afresh.
	stored = stored && record.Anchored
	if stored && record.World != world {
		// A reload of the same colony: the geometry is on the map, but which
		// tiers still stand is re-observed below before the record counts as
		// complete again (an older save may lack some of them).
		record.World, record.Complete = world, false
		if err = p.journal.SaveDefenseLayout(call, record); err != nil {
			return RoundsDefenseLayoutResult{}, err
		}
	}
	if stored && record.Project != goal.Project.ID {
		// A new Episode (cancelled and re-created, e.g. by a letter pause)
		// keeps the stored geometry: re-proposing against a census that
		// already holds the earlier epoch's walls shifts the corridor by a
		// cell and lands traps beside the old ones, which can never place.
		// Built tiers stay built; pending tiers get a fresh retry budget.
		record.Project = goal.Project.ID
		for i := range record.Tiers {
			if !record.Tiers[i].Built {
				record.Tiers[i].Attempts = 0
			}
		}
		if err = p.journal.SaveDefenseLayout(call, record); err != nil {
			return RoundsDefenseLayoutResult{}, err
		}
	}
	combat, err := defenseCombatKey(call, p, review)
	if err != nil {
		return RoundsDefenseLayoutResult{}, err
	}
	if stored && record.Complete && !defenseReverifyDue(record, review.Tick, combat) {
		return RoundsDefenseLayoutResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	expected, err := stepScope(call, r.reviewer.native)
	if err != nil {
		return RoundsDefenseLayoutResult{}, err
	}
	if !roundsBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoundsDefenseLayoutResult{}, defenseControlErr(168)
	}
	claims, err := p.journal.ConstructionClaims(call, state.Snapshot, expected.Tick)
	if err != nil {
		return RoundsDefenseLayoutResult{}, err
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
		return RoundsDefenseLayoutResult{}, err
	}
	if !stored {
		layout, entrances, held, ok, err := r.propose(call, epoch, goal, review, state, read)
		if err != nil {
			return RoundsDefenseLayoutResult{}, err
		}
		if !ok {
			if held.IsZero() {
				return RoundsDefenseLayoutResult{}, defenseControlErr(289)
			}
			return RoundsDefenseLayoutResult{Verdict: held}, nil
		}
		record, err = store.NewDefenseLayoutRecord(world, goal.Project.ID, layout, entrances)
		if err != nil {
			return RoundsDefenseLayoutResult{}, err
		}
		if _, err = defensePerimeterTiers(&record, read); err != nil {
			return RoundsDefenseLayoutResult{}, err
		}
		if err = p.journal.SaveDefenseLayout(call, record); err != nil {
			return RoundsDefenseLayoutResult{}, err
		}
	} else if recut, err := defensePerimeterTiers(&record, read); err != nil {
		return RoundsDefenseLayoutResult{}, err
	} else if recut {
		// The plan's perimeter changed (#954): the new cut is built
		// behind removals of what it no longer wants.
		defenseAction(call, "defense-layout", slog.LevelInfo, "applied", "perimeter_recut", "perimeter", map[string]any{"revision": record.PerimeterRevision})
		if err = p.journal.SaveDefenseLayout(call, record); err != nil {
			return RoundsDefenseLayoutResult{}, err
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
		return RoundsDefenseLayoutResult{}, err
	}
	tick := read.Projection.Identity.Tick
	pipe.ledger = newFundingLedger(policy.StockObservation{Snapshot: state.Snapshot, Tick: tick})
	pipe.settle(census)
	// A layout that stood before turrets could be placed (no research, no
	// network, no steel) gains the tier once the observed gates open.
	if !pipe.piped() && len(definitions) > 0 && stored && defenseTurretsDue(record, expected.Tick) {
		request := defenseTurretRequest(read)
		if request.TurretGatesOpen() {
			if err = r.proposeTurrets(call, state, read, &record); err != nil {
				return RoundsDefenseLayoutResult{}, err
			}
		} else {
		}
	}
	// The mortar tier follows the same way once mortar research and the
	// raid points' budget open its gates (#1206).
	if !pipe.piped() && len(definitions) > 0 && defenseMortarsDue(record, expected.Tick) {
		if request := defenseMortarRequest(read); request.MortarGatesOpen() {
			if err = r.proposeMortars(call, state, read, &record); err != nil {
				return RoundsDefenseLayoutResult{}, err
			}
		}
	}
	// A standing turret below the armory's rung is replaced in place, one
	// at a time (#1210).
	if !pipe.piped() && len(definitions) > 0 && stored && defenseReplaceDue(record, expected.Tick) {
		if err = r.replaceTurret(call, state, read, &record); err != nil {
			return RoundsDefenseLayoutResult{}, err
		}
	}
	order := defenseTierSequence(record, defenseRoamerOwned(read.Projection))
	var admitted *RoundsDefenseLayoutResult
	for _, name := range order {
		tier, buildings, ok := record.Tier(name)
		if !ok || len(buildings) == 0 || tier.Built || defenseTierOpen(pipe.open, name) {
			continue
		}
		// Tiers after the first go on only while they are perimeter tiers the
		// stock funds beside the ones in flight; anything else waits for them.
		if pipe.piped() && (!policy.IsPerimeterTier(name) || tier.Remove) {
			break
		}
		if pipe.piped() && tier.Attempts >= maxDefenseTierAttempts {
			continue
		}
		if tier.Attempts >= maxDefenseTierAttempts {
			// Verified as far as it goes: the tier is retried after the
			// next combat or game hour, not on every step.
			record.VerifiedTick, record.VerifiedCombat = tick, combat
			if err = p.journal.SaveDefenseLayout(call, record); err != nil {
				return RoundsDefenseLayoutResult{}, err
			}
			return RoundsDefenseLayoutResult{Verdict: refuse(RefusalRetriesSpent, "maxDefenseTierAttempts", ""), Tier: name}, nil
		}
		if tier.Remove {
			return r.remove(call, epoch, goal, state, read, record, tier, census)
		}
		buildings = defenseMissingBuildings(buildings, census)
		if len(buildings) == 0 {
			if pipe.piped() {
				continue
			}
			// The census re-opened the tier on a cell it cannot see
			// (fogged); nothing can be admitted until it can.
			return RoundsDefenseLayoutResult{Verdict: fieldUnavailable("fogged_defense_cells"), Tier: name}, nil
		}
		if pipe.piped() && pipe.overlaps(buildings) {
			continue
		}
		if policy.IsPerimeterTier(name) && defenseNeedsStone(buildings) {
			// The wall is stone: the stock's most plentiful block, waited
			// for the way MaintainStoneShell waits for its material. A
			// wall with its stuff set (wood on soft ground) and a bridge
			// keep theirs.
			stuff, ok := defensePerimeterStone(read.Projection, buildings)
			if !ok {
				if pipe.piped() {
					break
				}
				if gate := policy.ResearchGate([]string{policy.StoneShellResearch}, read.Projection.Facts.Research); gate != "" {
					return RoundsDefenseLayoutResult{Verdict: researchWait(gate), Tier: name}, nil
				}
				return RoundsDefenseLayoutResult{Verdict: defensePerimeterNoStone, Tier: name}, nil
			}
			for i, b := range buildings {
				if !defenseStoneBuilding(b) {
					continue
				}
				if buildings[i], err = domain.NewBuilding(b.Definition(), b.Cell(), b.Rotation(), stuff); err != nil {
					return RoundsDefenseLayoutResult{}, err
				}
			}
		}
		result, err := r.admit(call, epoch, goal, state, read, record, tier, buildings, defenseTierMethodID(tier), pipe)
		if err != nil {
			return RoundsDefenseLayoutResult{}, err
		}
		if result.Verdict != BuildingReasonAdmitted {
			if admitted != nil {
				return *admitted, nil
			}
			if len(pipe.open) > 0 {
				// The tiers in flight are the work this one waits behind.
				return RoundsDefenseLayoutResult{Verdict: BuildingReasonExistingWork}, nil
			}
			return result, nil
		}
		admitted = &result
		if record, _, err = p.journal.LoadDefenseLayout(call, world); err != nil {
			return RoundsDefenseLayoutResult{}, err
		}
		// An admission moves the project's revision: the next tier of this step
		// is admitted against the project as it now stands, not the one read
		// before the first (a stale owner ended every step after one tier).
		if goal, _, err = defenseLayoutProject(call, p, review); err != nil {
			return RoundsDefenseLayoutResult{}, err
		}
	}
	if admitted != nil {
		return *admitted, nil
	}
	if len(pipe.open) > 0 {
		return RoundsDefenseLayoutResult{Verdict: BuildingReasonExistingWork}, nil
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
	upkeep := policy.DefenseRearmTurrets(defenseTurretFacts(record, census), workers, read.Projection.Resources, policy.PowerOutageHold(read.Projection.Facts.DisasterConditions))
	if err = p.journal.SaveDefenseLayout(call, record); err != nil {
		return RoundsDefenseLayoutResult{}, err
	}
	if len(upkeep.Rearm) > 0 {
		return r.rearm(call, epoch, goal, review, state, read, upkeep.Rearm[0], arbiter)
	}
	if len(upkeep.Unpowered) > 0 || len(upkeep.Empty) > 0 {
		subject := "turret_fuel"
		if len(upkeep.Unpowered) > 0 {
			subject = "turret_power"
		}
		return RoundsDefenseLayoutResult{Verdict: awaitingPlan(subject, ""), Tier: policy.TierTurrets}, nil
	}
	// The line stands: raider cover inside its engagement zone is the
	// remaining deficit (#581).
	if result, handled, err := r.clearCover(call, epoch, goal, review, state, read, record); handled || err != nil {
		return result, err
	}
	return RoundsDefenseLayoutResult{Verdict: BuildingReasonNoDeficit}, nil
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
func defenseRearmAttempts(history []domain.Method, turret string, tick domain.Tick) int {
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
func (r *RoundsDefenseLayoutPlanner) rearm(call, epoch context.Context, goal store.ProjectState, review store.Rounds, state ControlState, read observation.RoundsReading, order policy.DefenseRearm, arbiter *stepArbiter) (RoundsDefenseLayoutResult, error) {
	p := r.reviewer.player
	tick := read.Projection.Identity.Tick
	history, err := p.journal.LoadOwnerMethods(call, goal)
	if err != nil {
		return RoundsDefenseLayoutResult{}, err
	}
	if defenseRearmAttempts(history, order.Turret, tick) >= maxDefenseRearmAttempts {
		return RoundsDefenseLayoutResult{Verdict: refuse(RefusalRetriesSpent, "maxDefenseRearmAttempts", ""), Tier: policy.TierTurrets}, nil
	}
	if arbiter == nil || !arbiter.tryClaim([]domain.PawnID{domain.PawnID(order.Pawn)}, "defense-rearm:"+order.Turret) {
		return RoundsDefenseLayoutResult{Verdict: waitFor(WaitMethodUsed, "defense_rearm_claim"), Tier: policy.TierTurrets}, nil
	}
	method := domain.MethodID(fmt.Sprintf("%s%d", defenseRearmPrefix(order.Turret), tick))
	id := domain.MintPlanID()
	service, err := domain.NewRecoveryService(domain.PawnID(order.Pawn), order.Turret, domain.RecoveryServiceRefuel)
	if err != nil {
		return RoundsDefenseLayoutResult{}, err
	}
	action, err := domain.NewRecoveryServiceAction(domain.ActionID(fmt.Sprintf("%s-0", id)), service)
	if err != nil {
		return RoundsDefenseLayoutResult{}, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoundsDefenseLayoutResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoundsDefenseLayoutResult{}, err
	}
	now := r.reviewer.clock.Now()
	if p.session.State() != state || now.Before(read.StartedAt) || now.Sub(read.StartedAt) > r.reviewer.maxAge {
		return RoundsDefenseLayoutResult{}, defenseControlErr(360)
	}
	if _, err = p.journal.CommitProjectMethod(call, goal.Project.ID, goal.Revision, method, "", plan); err != nil {
		return RoundsDefenseLayoutResult{}, err
	}
	defenseAction(call, "defense-layout", slog.LevelInfo, "applied", "rearm", order.Turret, map[string]any{"pawn": order.Pawn, "fuel": order.Fuel, "x": order.Cell.X, "z": order.Cell.Z, "method": string(method)})
	return RoundsDefenseLayoutResult{Verdict: BuildingReasonAdmitted, Plan: id, Tier: policy.TierTurrets}, nil
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
				facts.ID, facts.Powered, facts.OutOfFuel = site.ID, site.Powered, site.OutOfFuel
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
func defenseMortarRequest(read observation.RoundsReading) policy.DefenseRequest {
	projection := read.Projection
	request := policy.DefenseRequest{Definitions: defenseDefinitions, UnitCosts: map[string][]policy.Amount{}}
	request.Mortar = policy.DefenseMortarRequest{Definition: defenseMortarDefinition, Stock: projection.Resources, Max: policy.MortarBudget(projection.Facts.RaidPoints), Available: domain.Known(defenseDefinitionAvailable(read, defenseMortarDefinition))}
	for _, d := range projection.Definitions {
		if d.Name != defenseMortarDefinition {
			continue
		}
		if price, ok := defenseUnitPrice(projection, d); ok {
			request.UnitCosts[d.Name] = price.Costs
			request.Mortar.Stuff = price.Stuff
		}
	}
	return request
}

// defenseUnitPrice prices a defense unit (#1731): a def not made from stuff
// from its cost list, a stuffed one from the allowed stocked stuff with the
// most hit points per cost (a rich colony's plasteel falls out of this). Not
// ok when a stuffed def has nothing stocked (an unknown stock stocks nothing)
// or the rows cannot state the choice: the unit is then unpriced and the tier
// does not place it.
func defenseUnitPrice(projection observation.ColonyProjection, d observation.PlanningDefinition) (observation.StuffPrice, bool) {
	stock, _ := projection.Stock()
	price, err := d.StuffChoice(observation.MaxHitPointsPerCost, stock)
	if err != nil {
		return observation.StuffPrice{}, false
	}
	return price, true
}

// proposeMortars reads the census around the colony and sites the mortar
// tier against the stored geometry and turret tier, recording it pending
// when it places anything; the probe tick is recorded either way.
func (r *RoundsDefenseLayoutPlanner) proposeMortars(call context.Context, state ControlState, read observation.RoundsReading, record *store.DefenseLayoutRecord) error {
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
	// fogged is the cells the census could not see: their facts are unknown, so a tier
	// standing on one never reads as built.
	fogged map[domain.Cell]bool
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
func defenseTurretRequest(read observation.RoundsReading) policy.DefenseRequest {
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
		price, priced := defenseUnitPrice(projection, d)
		if priced {
			request.UnitCosts[d.Name] = price.Costs
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
		if priced {
			request.Turret.Stuff = price.Stuff
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
func defenseTurretRung(read observation.RoundsReading) string {
	facts := read.Projection.Facts
	for _, rung := range policy.TurretRungs(policy.AssessArmory(facts.RaidPoints, facts.Research).Tier) {
		if defenseDefinitionAvailable(read, rung) {
			return rung
		}
	}
	return policy.TurretMini
}

// defenseTurretSize is a ladder definition's observed North footprint.
func defenseTurretSize(read observation.RoundsReading, definition string) policy.Bounds {
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
func (r *RoundsDefenseLayoutPlanner) replaceTurret(call context.Context, state ControlState, read observation.RoundsReading, record *store.DefenseLayoutRecord) error {
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
	if err != nil || !ok {
		return r.reviewer.player.journal.SaveDefenseLayout(call, *record)
	}
	applyTurretReplacement(record, old, replacement, standing, request.Turret.Size)
	defenseAction(call, "defense-layout", slog.LevelInfo, "applied", "turret_replace", request.Turret.Definition, map[string]any{"x": old.Cell().X, "z": old.Cell().Z})
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
func (r *RoundsDefenseLayoutPlanner) proposeTurrets(call context.Context, state ControlState, read observation.RoundsReading, record *store.DefenseLayoutRecord) error {
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
	tier, _, err := policy.DefenseTurrets(request, geometry)
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
			if tier.Name == policy.TierFunnel {
				g.Walls = append(g.Walls, b.Cell)
			}
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
func defenseCombatKey(ctx context.Context, p *Player, review store.Rounds) (string, error) {
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
func (r *RoundsDefenseLayoutPlanner) observeTiers(call context.Context, state ControlState, read observation.RoundsReading, record *store.DefenseLayoutRecord) (*defenseCensus, error) {
	placed := false
	for _, tier := range record.Tiers {
		placed = placed || len(tier.Buildings) > 0
	}
	if !placed {
		return nil, nil
	}
	projection := read.Projection
	census := &defenseCensus{edifice: map[domain.Cell]string{}, terrain: map[domain.Cell]string{}, conduits: map[domain.Cell]bool{}, consumers: map[domain.Cell]policy.PowerSite{},
		cover: map[domain.Cell]*bridge.DefenseCover{}, unbridging: map[domain.Cell]bool{}, fogged: map[domain.Cell]bool{}}
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
		census.fogged[cell.Cell] = cell.Fogged
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
	r.logUnbuiltTiers(call, *record, census)
	if !defenseTierCensus(record, census) {
		return census, nil
	}
	return census, r.reviewer.player.journal.SaveDefenseLayout(call, *record)
}

// roundsDefensiveLayoutStanding is the journal's view of the layout for
// development arbitration: known only while the goal is opted in and a
// record for this colony is stored; a record from another load is a reload
// whose tiers are re-observed before it counts.
func roundsDefensiveLayoutStanding(ctx context.Context, journal *store.Store, p policy.RoundsPolicy, snapshot domain.GenerationSnapshot) (domain.Fact[bool], error) {
	if !p.DefensiveLayout {
		return domain.Unknown[bool](), nil
	}
	world := store.World{Colony: snapshot.Colony, Load: snapshot.Load, Map: snapshot.Map}
	record, stored, err := journal.LoadDefenseLayout(ctx, world)
	if err != nil {
		return domain.Unknown[bool](), err
	}
	if !stored || record.World != world {
		return domain.Known(false), nil
	}
	return domain.Known(record.Standing()), nil
}

// constructionMemory is the construction material demand of the latest
// review of one world: derived state the Rounder keeps in memory, empty
// until the first review after a restart. serves maps the stuffs that may
// serve a clothing demand to the stuff it names (policy.ClothingRunway).
type constructionMemory struct {
	mu       sync.Mutex
	snapshot domain.GenerationSnapshot
	needs    map[policy.Resource]int64
	serves   map[policy.Resource]policy.Resource
}

func (m *constructionMemory) set(snapshot domain.GenerationSnapshot, needs map[policy.Resource]int64, serves map[policy.Resource]policy.Resource) {
	m.mu.Lock()
	defer m.mu.Unlock()
	m.snapshot, m.needs, m.serves = snapshot, needs, serves
}

// getServes is the clothing serves map the review of snapshot's world
// computed, none for another world.
func (m *constructionMemory) getServes(snapshot domain.GenerationSnapshot) map[policy.Resource]policy.Resource {
	m.mu.Lock()
	defer m.mu.Unlock()
	if m.snapshot != snapshot {
		return nil
	}
	return m.serves
}

// get is the demand the review of snapshot's world computed, none for
// another world.
func (m *constructionMemory) get(snapshot domain.GenerationSnapshot) map[policy.Resource]int64 {
	m.mu.Lock()
	defer m.mu.Unlock()
	if m.snapshot != snapshot {
		return nil
	}
	return m.needs
}

// resourceTargets is the operator's MaintainResource floors, the stone-block
// floor the stock census derives (#231) and the journal's derived needs
// merged, the targets every resource planner dispatches on. An unknown
// census leaves the stone floor out.
func (r *Rounder) resourceTargets(ctx context.Context, snapshot domain.GenerationSnapshot, stock domain.Fact[[]policy.Amount]) (map[policy.Resource]int64, error) {
	var needs map[policy.Resource]int64
	review, err := r.player.journal.LoadRounds(ctx)
	if err != nil {
		return nil, err
	}
	if review.Enabled && review.Snapshot == snapshot {
		items, err := r.itemFacts(ctx, snapshot)
		if err != nil {
			return nil, err
		}
		needs = policy.MedicineResourceNeeds(items, needs, review.MedicineTarget)
		needs = policy.ResourceConcernTargets(needs, r.construction.get(snapshot))
		needs = policy.ResourceConcernTargets(needs, policy.ResourceRunwayTargets(review.ResourceRunwayState()))
		if review.BrewingFinished {
			needs = policy.ResourceConcernTargets(needs, policy.SocialDrugTargets(domain.Known(policy.ResearchFacts{Finished: []policy.ResearchProjectID{"Brewing"}})))
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
// chokepoint, no line of sight), which is a wait rather than an error; the
// verdict then names which of those is missing.
func (r *RoundsDefenseLayoutPlanner) propose(call, epoch context.Context, goal store.ProjectState, review store.Rounds, state ControlState, read observation.RoundsReading) (policy.DefenseLayout, []domain.Cell, Verdict, bool, error) {
	projection := read.Projection
	identity := boundary.Identity(state.Snapshot)
	killbox, region, home, ok := defenseKillbox(projection)
	if !ok {
		return policy.DefenseLayout{}, nil, awaitingPlan("layout_plan", "killbox"), false, nil
	}
	site, _, err := r.native.ReadDefenseSite(call, identity, region)
	if err != nil {
		return policy.DefenseLayout{}, nil, Verdict{}, false, err
	}
	if err = r.sameTick(site.Context, state, projection.Identity.Tick); err != nil {
		return policy.DefenseLayout{}, nil, Verdict{}, false, err
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
		if err != nil {
			return policy.DefenseLayout{}, nil, Verdict{}, false, err
		}
		return policy.DefenseLayout{}, nil, noWorker("ranged_defender"), false, nil
	}
	request.Defenders, request.MinRange = defenders, minRange
	stock, stockKnown := projection.Resources.Value()
	request.Definitions = policy.DefenseCoverChoice(projection.Facts.Items, request.Definitions, stock, stockKnown,
		defenseDefinitionAvailable(read, policy.DefenseSandbags), defenseDefinitionAvailable(read, policy.DefenseEmbrasure), defenders)
	defenseIEDRequest(read, &request)
	if err = r.markWallSupport(call, identity, &request, site.Cells); err != nil {
		return policy.DefenseLayout{}, nil, Verdict{}, false, err
	}
	// Rock on the corridor and the defenders' ground is mined first through
	// the shared rock step (#1701, #1588); the layout waits on that dig.
	if rock, digErr := policy.DefenseRockCells(request); digErr == nil {
		result, handled, err := r.digKillbox(call, epoch, goal, review, state, read, request.Home, rock)
		if err != nil || handled {
			if err == nil {
			}
			return policy.DefenseLayout{}, nil, result.Verdict, false, err
		}
	}
	layout, err := policy.DefenseLayouts(request)
	if err != nil {
		return policy.DefenseLayout{}, nil, noSpace("defense_layout"), false, nil
	}
	firing, approach := layout.Probe()
	if len(firing) == 0 {
		return policy.DefenseLayout{}, nil, noSpace("firing_cell"), false, nil
	}
	lines, _, err := r.native.ReadLinesOfFire(call, identity, firing, approach)
	if err != nil {
		return policy.DefenseLayout{}, nil, Verdict{}, false, err
	}
	if err = r.sameTick(lines.Context, state, projection.Identity.Tick); err != nil {
		return policy.DefenseLayout{}, nil, Verdict{}, false, err
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
	if err != nil {
		return policy.DefenseLayout{}, nil, noSpace("defense_layout"), false, nil
	}
	if !layout.LinesVerified {
		return policy.DefenseLayout{}, nil, fieldUnavailable("lines_of_fire"), false, nil
	}
	return layout, request.Entrances, Verdict{}, true, nil
}

// digKillbox admits the rock the killbox's role cells stand on through the
// shared rock step (admitRockStep), reached from the yard behind the
// killbox. handled is false when nothing needs digging or the native side
// has nothing to dig (or no excavation read).
func (r *RoundsDefenseLayoutPlanner) digKillbox(call, epoch context.Context, goal store.ProjectState, review store.Rounds, state ControlState, read observation.RoundsReading, access domain.Cell, planned []policy.RoleCell) (RoundsBuildingResult, bool, error) {
	source, ok := r.native.(RoundsExcavationSource)
	if !ok {
		return RoundsBuildingResult{}, false, nil
	}
	dig := &RoundsBuildingPlanner{reviewer: r.reviewer, concern: policy.EnsureDefensiveLayout, excavation: source}
	step := excavationStep{state: state, review: review, owner: goal, facts: read.Projection, read: read.ColonyReading}
	p := r.reviewer.player
	check := func() error {
		if err := p.current(call, epoch); err != nil {
			return err
		}
		if p.session.State() != state {
			return fmt.Errorf("%w: digKillbox: session state changed", ErrControl)
		}
		return nil
	}
	return dig.admitRockStep(call, epoch, step, planned, access, "plan-dig-killbox", nil, check)
}

// admit previews one tier's placements, audits colonist access with every
// tier's footprint impassable, and admits the tier as one Defense-purpose
// building method.
func (r *RoundsDefenseLayoutPlanner) admit(call, epoch context.Context, goal store.ProjectState, state ControlState, read observation.RoundsReading, record store.DefenseLayoutRecord, tier store.DefenseTierRecord, buildings []domain.Building, key domain.MethodID, pipe *defensePipeline) (RoundsDefenseLayoutResult, error) {
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
	placed := 0
	var total []policy.Amount
	priceUnknown := false
	// One native call per placement batch previews the whole tier: a
	// perimeter tier is over a hundred cells, and a preview per cell
	// outlasted the optional wave's wall on a slow runner every step (#1248).
	candidates := make([]domain.Action, 0, len(buildings))
	for i, building := range buildings {
		action, err := domain.NewBuildingAction(domain.ActionID(fmt.Sprintf("%s-%d", id, i)), building)
		if err != nil {
			return RoundsDefenseLayoutResult{}, err
		}
		candidates = append(candidates, action)
	}
	var evaluated []bridge.BuildingPreview
	if len(candidates) > 0 {
		var err error
		if evaluated, _, err = r.native.PreviewBuildings(call, candidates, snapshot); err != nil {
			return RoundsDefenseLayoutResult{}, err
		}
	}
	for i, building := range buildings {
		action := candidates[i]
		preview, ok := classifyDefensePreview(evaluated[i], building.Cell())
		if !ok && preview.NativeWorkPending {
			// A wall cell whose blueprint or frame is already placed is in
			// flight: the rest of a perimeter tier is admitted beside it.
			if policy.IsPerimeterTier(tier.Name) {
				placed++
				continue
			}
			return RoundsDefenseLayoutResult{Verdict: BuildingReasonExistingWork, Tier: tier.Name, NativeWorkTicks: defenseNativeWorkTicks}, nil
		}
		if !ok && (tier.Name == policy.TierFiringLine && building.Definition() == defenseDefinitions.Floor || policy.IsPerimeterTier(tier.Name) || tier.Name == policy.TierIEDs) {
			// A perimeter cell the game refuses (natural rock, a building
			// in the way) is left out of the wall; the terrain holds it.
			// An IED cell it refuses is left out too: one bad cell on the
			// approach must not hold the whole tier (and the layout) open.
			unfloorable[building.Cell()] = true
			continue
		}
		if !ok {
			cell := building.Cell()
			defenseAction(call, "defense-layout", slog.LevelWarn, "refused", "placement_preview_refused", string(tier.Name), map[string]any{"definition": building.Definition(), "x": cell.X, "z": cell.Z, "native_reason": evaluated[i].Reason})
			return RoundsDefenseLayoutResult{Verdict: fieldUnavailable("placement_preview"), Tier: tier.Name}, nil
		}
		actions = append(actions, action)
		previews = append(previews, preview.Preview)
		if err := mergeRoundsStock(&stock, preview.Stock, len(actions) == 1); err != nil {
			return RoundsDefenseLayoutResult{}, err
		}
		if err := pipe.ledger.merge(preview.Stock, !pipe.merged); err != nil {
			return RoundsDefenseLayoutResult{}, err
		}
		pipe.merged = true
		if costs, known := preview.Preview.Costs.Value(); known {
			pipe.ledger.price(building.Definition()+"/"+building.Stuff(), costs)
			total = append(total, costs...)
		} else {
			priceUnknown = true
		}
	}
	if len(actions) == 0 && placed > 0 && len(unfloorable) == 0 {
		return RoundsDefenseLayoutResult{Verdict: BuildingReasonExistingWork, Tier: tier.Name, NativeWorkTicks: defenseNativeWorkTicks}, nil
	}
	if len(unfloorable) > 0 {
		tier.Buildings = defenseWithoutFloors(tier.Buildings, unfloorable, policy.IsPerimeterTier(tier.Name) || tier.Name == policy.TierIEDs)
		record.SetTier(tier)
		defenseAction(call, "defense-layout", slog.LevelInfo, "refused", "placement_refused_natively", string(tier.Name), map[string]any{"cells": len(unfloorable)})
		if err := p.journal.SaveDefenseLayout(call, record); err != nil {
			return RoundsDefenseLayoutResult{}, err
		}
		if len(actions) == 0 {
			return RoundsDefenseLayoutResult{Verdict: noSpace("defense_tier"), Tier: tier.Name}, nil
		}
	}
	// A tier admitted beside others in flight is admitted only while the stock
	// still pays for it after what they have claimed; the first tier goes whole,
	// as a lone tier always did.
	if pipe.piped() {
		pipe.claimOpen()
		if priceUnknown || !pipe.ledger.funded(total) {
			return RoundsDefenseLayoutResult{Verdict: BuildingReasonExistingWork, Tier: tier.Name}, nil
		}
	}
	// The audit blocks every placement of the whole layout that colonists
	// cannot pass (defenseAuditBlocks). Colonists price their known traps
	// and the hallway keeps them a trap-free route (#619, #1544); trap
	// cells are kept off their resting positions separately.
	blocked := map[domain.Cell]bool{}
	var blockedCells []domain.Cell
	for _, t := range record.Tiers {
		for _, b := range t.Buildings {
			if !defenseAuditBlocks(b.Definition) {
				continue
			}
			if !blocked[b.Cell] {
				blocked[b.Cell] = true
				blockedCells = append(blockedCells, b.Cell)
			}
		}
	}
	// A perimeter section skips it: a wall blueprint is walkable and the native
	// guard (#2314) builds a thick wall inner layer first, so placing a section
	// walls no colonist in, and the gates are the layout's own design.
	if !policy.IsPerimeterTier(tier.Name) {
		targets := append([]domain.Cell{record.Entry}, record.Entrances...)
		access, _, err := r.native.ReadSpatialAccess(call, boundary.Identity(state.Snapshot), blockedCells, targets, nil)
		var native *bridge.NativeUnavailable
		if errors.As(err, &native) {
			return RoundsDefenseLayoutResult{Verdict: fieldUnavailable("spatial_access"), Tier: tier.Name}, nil
		}
		if err != nil {
			return RoundsDefenseLayoutResult{}, err
		}
		if err = r.sameTick(access.Context, state, projection.Identity.Tick); err != nil {
			return RoundsDefenseLayoutResult{}, err
		}
		if refusal := access.Refusal(); refusal != "" {
			defenseAction(call, "defense-layout", slog.LevelInfo, "refused", "access_audit", string(tier.Name), map[string]any{"blocked_cells": len(blockedCells), "detail": refusal})
			return RoundsDefenseLayoutResult{Verdict: noSpace("walkable_layout"), Tier: tier.Name}, nil
		}
	}
	plan, err := domain.NewPlan(id, 1, actions)
	if err != nil {
		return RoundsDefenseLayoutResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoundsDefenseLayoutResult{}, err
	}
	if p.session.State() != state {
		return RoundsDefenseLayoutResult{}, defenseControlErr(358)
	}
	if !pipe.scoped {
		if pipe.scope, err = stepScope(call, r.reviewer.native); err != nil {
			return RoundsDefenseLayoutResult{}, defenseControlErr(366)
		}
		pipe.scoped = true
	}
	actual := pipe.scope
	if !roundsBuildingBoundary(actual, state.Snapshot, projection.Identity.Tick) {
		return RoundsDefenseLayoutResult{}, defenseControlErr(366)
	}
	now := r.reviewer.clock.Now()
	if now.Before(read.StartedAt) || now.Sub(read.StartedAt) > r.reviewer.maxAge {
		return RoundsDefenseLayoutResult{}, observation.ErrStale
	}
	decision, err := admitMethod(call, p.journal, store.BuildingMethodRequest{Owner: goal, Method: key, Plan: plan, Current: snapshot, Tick: projection.Identity.Tick, Bounds: domain.Known(projection.Bounds), Stock: stock, Previews: previews, Purpose: policy.Defense})
	if err != nil {
		return RoundsDefenseLayoutResult{}, err
	}
	reason := admissionRefused(decision)
	if decision.Admitted {
		reason = BuildingReasonAdmitted
		tier.Attempts++
		record.SetTier(tier)
		pipe.ledger.claim(total)
		pipe.admitted++
		for _, action := range actions {
			if b, ok := action.Building(); ok {
				pipe.cells[b.Cell()] = true
			}
		}
		if err = p.journal.SaveDefenseLayout(call, record); err != nil {
			return RoundsDefenseLayoutResult{}, err
		}
	} else {
	}
	return RoundsDefenseLayoutResult{Verdict: reason, Plan: id, Tier: tier.Name}, nil
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

func (r *RoundsDefenseLayoutPlanner) sameTick(observed *c.ObservationContext, state ControlState, tick domain.Tick) error {
	current, err := boundary.Context(observed, state.Snapshot)
	if err != nil || current.Native != state.Snapshot.Native || domain.Tick(observed.GetTick()) < tick {
		return defenseControlErr(388)
	}
	return nil
}

// preview evaluates one building natively (the fight builds are a handful).
func (r *RoundsDefenseLayoutPlanner) preview(ctx context.Context, action domain.Action, snapshot domain.GenerationSnapshot, _ domain.Tick, cell domain.Cell) (bridge.BuildingPreview, bool, error) {
	previews, _, err := r.native.PreviewBuildings(ctx, []domain.Action{action}, snapshot)
	if err != nil {
		return bridge.BuildingPreview{}, false, err
	}
	preview, ok := classifyDefensePreview(previews[0], cell)
	return preview, ok, nil
}

// classifyDefensePreview reports whether a native preview places the one
// building on its own cell. The refused preview is returned so the caller
// can tell a cell already under native construction from one it cannot
// place on.
func classifyDefensePreview(preview bridge.BuildingPreview, cell domain.Cell) (bridge.BuildingPreview, bool) {
	v := preview.Preview
	footprint, fk := v.Footprint.Value()
	legal, lk := v.CanPlace.Value()
	safe, sk := v.SafeToPlace.Value()
	if _, mk := v.MadeFromStuff.Value(); !fk || !mk || !lk || !sk {
		return bridge.BuildingPreview{}, false
	}
	if len(footprint) != 1 || footprint[0] != cell || !legal || !safe {
		return preview, false
	}
	return preview, true
}

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
// defenseAuditBlocks reports whether a layout placement is impassable to
// colonists in the access audit: walls and turrets are. A spike trap is
// walked, a fence or a barricade climbed (the killbox's fence bar is on
// the raiders' route too, priced by pathFenceCost, #1544), a colonist
// opens the colony's own door, and a conduit or a floor lies under the
// pawn.
func defenseAuditBlocks(definition string) bool {
	switch definition {
	case defenseDefinitions.Trap, defenseDefinitions.Fence, defenseDefinitions.Sandbag, defenseDefinitions.Door, defenseDefinitions.Bait, defenseConduitDefinition:
		return false
	}
	return !defenseTerrain(definition)
}

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
	out.Walkable, out.Passable = domain.Known(cell.Walkable), domain.Known(cell.Passable)
	out.PlayerOwned, out.NaturalRock, out.EdgeReachable = domain.Known(cell.PlayerOwned), domain.Known(cell.NaturalRock), domain.Known(cell.EdgeReachable)
	out.Door, out.CoverFill, out.Edifice = domain.Known(cell.Door), domain.Known(cell.CoverFill), cell.EdificeDefName
	out.Roofed = domain.Known(cell.Roofed)
	return out
}

// defenderRange counts colonists whose primary weapon is ranged and returns
// the shortest such range, unknown when nobody carries one.
func defenderRange(rows []*o.PawnState, arms armament) (int, domain.Fact[float64], error) {
	count := 0
	shortest := math.Inf(1)
	for _, row := range rows {
		reach, err := arms.primaryRange(row.GetEquipment())
		if err != nil {
			return 0, domain.Unknown[float64](), err
		}
		if reach <= 0 {
			continue
		}
		count++
		shortest = math.Min(shortest, reach)
	}
	if count == 0 {
		return 0, domain.Unknown[float64](), nil
	}
	if count > 8 {
		count = 8
	}
	return count, domain.Known(shortest), nil
}

// defenseIEDRequest adds the approach IEDs (#1209) the census allows, each
// gated independently and carrying its native explosive radius, and every
// stockpile cell as the flammable storage their blast must avoid. A zone
// census not yet held leaves storage unknown, which places no IED.
func defenseIEDRequest(read observation.RoundsReading, request *policy.DefenseRequest) {
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
		if price, ok := defenseUnitPrice(projection, d); ok {
			request.UnitCosts[d.Name] = price.Costs
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

// logUnbuiltTiers logs what the census shows on each unbuilt tier's missing
// cells (fogged, or the edifice found there), when that changes: a tier that
// never reads as built is otherwise silent about why.
func (r *RoundsDefenseLayoutPlanner) logUnbuiltTiers(ctx context.Context, record store.DefenseLayoutRecord, census *defenseCensus) {
	if r.unbuilt == nil {
		r.unbuilt = map[policy.DefenseTierName]string{}
	}
	for _, tier := range record.Tiers {
		if tier.Built || tier.Remove || len(tier.Buildings) == 0 {
			continue
		}
		counts := map[string]int{}
		for _, b := range tier.Buildings {
			switch {
			case census.standing(b.Definition, b.Cell):
				counts["standing"]++
			case census.fogged[b.Cell]:
				counts["fogged"]++
			default:
				counts["found:"+census.edifice[b.Cell]]++
			}
		}
		summary := fmt.Sprint(counts)
		if r.unbuilt[tier.Name] == summary {
			continue
		}
		r.unbuilt[tier.Name] = summary
		slog.InfoContext(ctx, "defense tier unbuilt", telemetry.ComponentKey, "defense-layout", "tier", tier.Name, "buildings", len(tier.Buildings), "census", summary)
	}
}
