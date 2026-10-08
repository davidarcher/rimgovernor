package buildingruntime

import (
	"context"
	"fmt"
	"hash"
	"log/slog"
	"reflect"
	"slices"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	snap "github.com/davidarcher/RimGovernor/go/internal/snapshot"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	mirrorpb "github.com/davidarcher/RimGovernor/go/internal/wire/mirrorpb"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
)

type RoundsDefenseSource interface {
	// CombatOrders sends a stop's changed orders (#850, #852).
	CombatOrders(context.Context, *c.Identity, string, *op.CombatOrders) ([]bridge.CombatOrderResult, error)
	CombatGeometry(context.Context, *mirrorpb.CombatGeometryRequest) (*mirrorpb.CombatGeometry, bridge.Result, error)
	// ReadCombat is the newest snapshot frame's combat state and the
	// fight's other inputs (#851, #853, #858).
	ReadCombat(context.Context, *c.Identity) (bridge.Combat, error)
	// ReadDefenseSite censuses a burn-out's fuel (#1120).
	burnFuelSource
}
type RoundsDefensePlanner struct {
	reviewer *Rounder
	native   RoundsDefenseSource
}
type RoundsDefenseResult struct {
	Verdict
	Plan domain.PlanID
}

func NewRoundsDefensePlanner(reviewer *Rounder, native RoundsDefenseSource) (*RoundsDefensePlanner, error) {
	if reviewer == nil || native == nil {
		return nil, fmt.Errorf("%w: NewRoundsDefensePlanner: reviewer == nil || native == nil", ErrControl)
	}
	return &RoundsDefensePlanner{reviewer, native}, nil
}
func (r *RoundsDefensePlanner) decide(call, epoch context.Context, arbiter *stepArbiter) (RoundsDefenseResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoundsDefenseResult{Verdict: BuildingReasonDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoundsDefenseResult{}, fmt.Errorf("%w: decide: !state.ObservationKnown || state.Snapshot.Validate() != nil", ErrControl)
	}
	review, err := p.journal.LoadRounds(call)
	if err != nil {
		return RoundsDefenseResult{}, err
	}
	if !review.Enabled || review.Snapshot != state.Snapshot {
		return RoundsDefenseResult{Verdict: BuildingReasonNoReview}, nil
	}
	incident, need, found, err := roundsIncident(call, p.journal, review, policy.ActiveCombat)
	if err != nil {
		return RoundsDefenseResult{}, err
	}
	if found && need == domain.SituationClear {
		// The raid ended before every action was issued: a squad draft the
		// worker prepared but never dispatched, and the moves and attacks
		// waiting on it or on a target that is now dead, would hold the
		// epoch open forever, so the goal could never satisfy and the next
		// raid could never open a fresh epoch (#226). Nothing native was
		// ordered for an unissued action, so cancelling it settles it.
		if err = r.settleUnissuedWork(call, incident); err != nil {
			return RoundsDefenseResult{}, err
		}
		// Its downed raiders are stripped, and the ones not worth
		// capturing finished, before it closes (#1079).
		if result, held, err := r.postFight(call, epoch, incident, state, review.Tick, arbiter); err != nil || held {
			return result, err
		}
		// The fight is over: closing it lets the undraft sweep undraft its
		// defenders (#939).
		for _, method := range incident.Methods {
			if strings.HasPrefix(string(method.Method), combatMethodPrefix) {
				if err = r.clearFightDoors(call, state, method.Plan); err != nil {
					return RoundsDefenseResult{}, err
				}
				r.clearFightAnimals(call, state, method.Plan)
				if err = p.journal.CloseCombatFight(call, method.Plan); err != nil {
					return RoundsDefenseResult{}, err
				}
			}
		}
		return RoundsDefenseResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	if !found || need != domain.SituationActive || review.VetoIncident(incident.Incident) != "" {
		return RoundsDefenseResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	// One ActiveCombat plan owns the fight (#852): its combat method whose
	// fight is open is the fight the stop decides for. Any other open work
	// waits.
	var fight *store.CombatFight
	var fightPlan domain.PlanID
	for _, method := range incident.Methods {
		if strings.HasPrefix(string(method.Method), combatMethodPrefix) {
			record, ok, err := p.journal.LoadCombatFight(call, method.Plan)
			if err != nil {
				return RoundsDefenseResult{}, err
			}
			if !ok || !record.Open {
				continue
			}
			if fight != nil {
				return RoundsDefenseResult{Verdict: BuildingReasonExistingWork}, nil
			}
			fight, fightPlan = &record, method.Plan
			continue
		}
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoundsDefenseResult{}, err
		}
		if store.PlanOpen(plan) {
			return RoundsDefenseResult{Verdict: BuildingReasonExistingWork}, nil
		}
	}
	started := r.reviewer.clock.Now()
	identity := boundary.Identity(state.Snapshot)
	// The fight decides from one frame (#853): the census, the combat
	// detail rows, the building lines of fire and the combat pawns and
	// events are all as of its tick.
	combat, err := r.native.ReadCombat(call, identity)
	if err != nil {
		return RoundsDefenseResult{}, err
	}
	if _, err = boundary.Context(combat.Context, state.Snapshot); err != nil || combat.Emergency.Context == nil || combat.Context.GetTick() < int64(review.Tick) {
		return RoundsDefenseResult{}, fmt.Errorf("%w: decide: err != nil || combat.Emergency.Context == nil || combat.Context.GetTick() < int64(review.Tick)", ErrControl)
	}
	in, reason, err := combatFrameInputs(combat, store.HuntPrey(incident.Incident))
	if err != nil || !reason.IsZero() {
		return RoundsDefenseResult{Verdict: reason}, err
	}
	if in.needed, err = plannedDrafts(call, p.journal); err != nil {
		return RoundsDefenseResult{}, err
	}
	emergency, rows := combat.Emergency, in.rows
	if result, err := r.planBreak(call, epoch, incident, state, started, arbiter, emergency.Facts, rows); err != nil || !result.Verdict.IsZero() {
		return result, err
	}
	var orderable []domain.PawnID
	var memory policy.CombatMemory
	if fight != nil {
		memory = fight.Memory
		orderable = fightOrderable(*fight, rows)
	}
	// A complete record from an earlier load of this colony still holds:
	// its geometry is on the map and its completion was census-verified
	// when written. Combat cannot wait for the layout review to adopt it
	// (that review does not run under a raid), and a wall lost since is the
	// same degradation a raider causes mid-session.
	layout, ok, err := p.journal.LoadDefenseLayout(call, store.World{Colony: state.Snapshot.Colony, Load: state.Snapshot.Load, Map: state.Snapshot.Map})
	if err != nil {
		return RoundsDefenseResult{}, err
	}
	var held domain.Fact[policy.CombatLayout]
	if ok && layout.Complete {
		combatLayout := policy.CombatLayout{Firing: layout.Firing, Retreat: layout.Retreat, Toward: layout.Toward}
		if n := len(layout.TrapLane); n > 0 {
			// The snake runs entry to exit; its last cell is the mouth
			// into the kill zone, where blockers hold (#864, #1544).
			combatLayout.Choke = domain.Known(layout.TrapLane[n-1])
		}
		held = domain.Known(combatLayout)
	}
	items, err := r.reviewer.itemFacts(call, state.Snapshot)
	if err != nil {
		return RoundsDefenseResult{}, err
	}
	burn, err := combatBurnSite(call, r.native, identity, memory, combat.Context.GetTick(), items)
	if err != nil {
		return RoundsDefenseResult{}, err
	}
	drug, err := r.reviewer.combatDrug(call, state.Snapshot)
	if err != nil {
		return RoundsDefenseResult{}, err
	}
	view := combatView(combat, in, orderable, held)
	view.Burn, view.Royalty, view.Drug = burn, r.reviewer.census.remembered(), drug
	tick := view.Tick
	stop := combatStop(combat, memory.Tick)
	orders, ask, next := policy.DecideCombat(view, policy.GeometryReply{}, stop, memory)
	recorded := snap.CombatStop{Tick: tick, Stop: stop, Orderable: orderable, Ask: ask, MemoryIn: memory}
	if l, known := held.Value(); known {
		recorded.Layout = &l
	}
	if ask != nil {
		recorded.Reply = r.answerGeometry(call, boundary.Identity(state.Snapshot), ask)
		orders, _, next = policy.DecideCombat(view, recorded.Reply, stop, memory)
	}
	recorded.MemoryOut = next
	if next.Formed == tick && next.Refusal != "" && next.Tactic != policy.TacticHold {
		// Squad defense follows; say which gate refused the hold (#714).
		defenseAction(call, "routine-defense", slog.LevelInfo, "refused", "hold_refused", string(incident.Incident.ID), map[string]any{"incident": string(incident.Incident.ID), "gate": next.Refusal})
	}
	if fight == nil {
		result, err := r.admitFight(call, epoch, incident, state, started, arbiter, fightAdmission{combat: combat, in: in, held: held, reply: recorded.Reply, stop: stop, memory: next})
		if err == nil && result.Verdict == BuildingReasonAdmitted {
			recorded.Plan = result.Plan
			recordCombatStop(call, combat, recorded)
		}
		return result, err
	}
	id := fightPlan
	recorded.Plan = id
	// A role pawn that is not a drafted defender (a later formation's
	// evacuee or responder, #911) is drafted in this stop's batch, and the
	// stop decides as it will be once drafted, as the admission does.
	var drafts []domain.PawnID
	unclaimed := undraftedRoles(next, orderable, view)
	if len(unclaimed) > 0 {
		// A pawn still taking its loadout (#1115) drafts once it settles.
		plan, err := p.journal.LoadPlan(call, id)
		if err != nil {
			return RoundsDefenseResult{}, err
		}
		pending := loadoutPending(plan)
		unclaimed = slices.DeleteFunc(unclaimed, func(pawn domain.PawnID) bool { return pending[pawn] })
	}
	if len(unclaimed) > 0 && arbiter.tryClaim(unclaimed) {
		drafts = unclaimed
		recorded.Orderable = append(slices.Clone(orderable), drafts...)
		slices.Sort(recorded.Orderable)
		view = combatView(combat, in, recorded.Orderable, held)
		view.Burn, view.Royalty, view.Drug = burn, r.reviewer.census.remembered(), drug
		var more *policy.GeometryRequest
		orders, more, next = policy.DecideCombat(view, recorded.Reply, stop, memory)
		if more != nil && recorded.Ask == nil {
			recorded.Ask = more
			recorded.Reply = r.answerGeometry(call, boundary.Identity(state.Snapshot), more)
			orders, more, next = policy.DecideCombat(view, recorded.Reply, stop, memory)
		}
		if more != nil {
			// A second ask waits for the next stop; this batch only drafts.
			orders, next = nil, memory
		}
		recorded.MemoryOut = next
	}
	// The stop's permit calls (#1608) are Ability actions on a method of
	// their own; the memory marking them called is saved with the stop.
	orders, permits := splitPermitCalls(orders)
	if err = r.commitPermitCalls(call, epoch, incident, permits); err != nil {
		return RoundsDefenseResult{}, err
	}
	// Guard mechs fight with the fight's stop (#1736): their drafts join the
	// roster like any draft, so the undraft sweep releases them.
	guards, err := combatMechGuards(combat, in.hostileIDs, in.rows)
	if err != nil {
		return RoundsDefenseResult{}, err
	}
	drafts = append(drafts, guards.Drafts...)
	orders = append(orders, guards.Orders...)
	if len(orders)+len(drafts) == 0 && len(permits) > 0 {
		if err = p.journal.SaveCombatMemory(call, id, next); err != nil {
			return RoundsDefenseResult{}, err
		}
		recordCombatStop(call, combat, recorded)
		return RoundsDefenseResult{Verdict: BuildingReasonAdmitted, Plan: id}, nil
	}
	if len(orders)+len(drafts) == 0 {
		// A stop that changes nothing writes nothing; a re-formation that
		// could order no one yet keeps its roles for the next stop.
		if next.Formed != memory.Formed || next.Tactic != memory.Tactic || !reflect.DeepEqual(next.Roles, memory.Roles) {
			if err = p.journal.SaveCombatMemory(call, id, next); err != nil {
				return RoundsDefenseResult{}, err
			}
			recordCombatStop(call, combat, recorded)
			if memory.Tactic == policy.TacticHold && next.Tactic != policy.TacticHold {
				return RoundsDefenseResult{Verdict: BuildingReasonHoldFallback, Plan: id}, nil
			}
		}
		return RoundsDefenseResult{Verdict: BuildingReasonExistingWork, Plan: id}, nil
	}
	if err = p.current(call, epoch); err != nil {
		return RoundsDefenseResult{}, err
	}
	record, next, err := r.issueCombatOrders(call, state, id, view, drafts, orders, next)
	if err != nil {
		return RoundsDefenseResult{}, err
	}
	record.Stop = stop
	if err = p.journal.RecordCombatStop(call, id, record, next); err != nil {
		return RoundsDefenseResult{}, err
	}
	// MemoryOut stays DecideCombat's: the refused orders' Forget is the
	// caller's, read from the evidence.
	recorded.Evidence = true
	recordCombatStop(call, combat, recorded)
	return RoundsDefenseResult{Verdict: BuildingReasonCombatOrders, Plan: id}, nil
}

// fightOrderable is the fight's orderable defenders (#939): its roster
// pawns the frame shows drafted, sorted.
func fightOrderable(fight store.CombatFight, rows map[string]*n.PawnState) []domain.PawnID {
	var orderable []domain.PawnID
	for pawn := range fight.Roster {
		if row := rows[string(pawn)]; row != nil && boundary.FactBool(row.Drafted) == domain.Known(true) {
			orderable = append(orderable, pawn)
		}
	}
	slices.Sort(orderable)
	return orderable
}

// combatInputs is what the fight reads from one frame beside the view:
// the hostiles and hunting predators it answers, the standing hostile
// buildings, and every census colonist's and those threats' detail rows.
type combatInputs struct {
	hostileIDs []string
	hunting    map[string]bool
	buildings  []policy.EmergencyThreat
	rows       map[string]*n.PawnState
	// prey are a hunt origin's live squad prey (#1617), read only while no
	// hostile stands: wild animals the threat census does not call hostile.
	prey []string
	// needed is plannedDrafts, set by the planner (#939).
	needed map[domain.PawnID]bool
	// weapons are the def rows' facts (#1723) of every weapon the frame's
	// combat pawns hold.
	weapons map[string]policy.WeaponDef
	// ranged is each detail row's ranged-weapon fact and reach the range of
	// its primary ranged weapon, read from the weapon's def rows (#1723);
	// a row whose primary is not yet resolved has neither.
	ranged map[string]domain.Fact[bool]
	reach  map[string]float64
	// profiles are the census colonists' profiles, traits resolved against
	// the catalog (resolveProfiles).
	profiles map[domain.PawnID]policy.PawnProfile
}

// resolveProfiles builds every census colonist's profile from its detail row.
func (in *combatInputs) resolveProfiles(colonists []policy.EmergencyPawn, arms armament) error {
	in.profiles = map[domain.PawnID]policy.PawnProfile{}
	for _, pawn := range colonists {
		work, err := observation.WorkPawnRow(in.rows[string(pawn.ID)], arms.catalog, arms.things)
		if err != nil {
			return err
		}
		in.profiles[domain.PawnID(pawn.ID)] = policy.BuildProfile(work)
	}
	return nil
}

// combatFrameInputs is the fight's inputs from the frame; a reason means
// there is no fight to decide (an incomplete census, no threat).
func combatFrameInputs(combat bridge.Combat, huntPrey []domain.PawnID) (combatInputs, Verdict, error) {
	facts := combat.Emergency.Facts
	colonistsComplete, ck := facts.ColonistsComplete.Value()
	if !ck || !colonistsComplete {
		return combatInputs{}, waitFor(WaitMethodUsed, "colonist_census_incomplete"), nil
	}
	var in combatInputs
	in.hostileIDs, in.hunting, in.buildings = defenseTargets(facts.Threats)
	// A hunt origin (#1617) is a fight with no hostile: its prey are the
	// targets while the frame shows none.
	if len(in.hostileIDs)+len(in.buildings) == 0 && !hasAggressiveBreak(facts) && facts.PodsOpen == 0 {
		for _, id := range huntPrey {
			if row, _ := combat.Detail.Get(string(id)); row != nil && boundary.FactBool(row.Dead) != domain.Known(true) {
				in.prey = append(in.prey, string(id))
			}
		}
	}
	// Raiders still in their pods (#908) are a fight with no hostile yet:
	// the pods tactic drafts the nearest armed before the open (#891).
	if len(facts.Colonists) == 0 || len(in.hostileIDs)+len(in.buildings)+len(in.prey) == 0 && !hasAggressiveBreak(facts) && facts.PodsOpen == 0 {
		return combatInputs{}, waitFor(WaitMethodUsed, "no_fight"), nil
	}
	in.rows = map[string]*n.PawnState{}
	for _, pawn := range facts.Colonists {
		in.rows[string(pawn.ID)], _ = combat.Detail.Get(string(pawn.ID))
	}
	for _, id := range append(slices.Clone(in.hostileIDs), in.prey...) {
		in.rows[id], _ = combat.Detail.Get(id)
	}
	for _, row := range in.rows {
		if row == nil {
			// The frame's detail misses a pawn its census lists.
			return combatInputs{}, Verdict{}, fmt.Errorf("%w: combatFrameInputs: row == nil", ErrControl)
		}
	}
	arms := armament{catalog: combat.Catalog, things: combat.Things}
	if err := in.resolveProfiles(facts.Colonists, arms); err != nil {
		return combatInputs{}, Verdict{}, err
	}
	in.ranged, in.reach = map[string]domain.Fact[bool]{}, map[string]float64{}
	for id, row := range in.rows {
		weapon, known, err := arms.primary(row.GetEquipment())
		if err != nil {
			return combatInputs{}, Verdict{}, err
		}
		if !known {
			continue
		}
		in.ranged[id] = domain.Known(weapon.Ranged)
		if weapon.Ranged {
			in.reach[id] = weapon.Range
		}
	}
	in.weapons = map[string]policy.WeaponDef{}
	for _, pawn := range combat.Pawns {
		def := pawn.GetWeapon()
		if _, done := in.weapons[def]; def == "" || done {
			continue
		}
		facts, err := combat.Catalog.WeaponOf(def)
		if err != nil {
			return combatInputs{}, Verdict{}, err
		}
		in.weapons[def] = facts
	}
	return in, Verdict{}, nil
}

// combatView is DecideCombat's view of the frame: the census colonists as
// defenders (the fight's own drafts not work elsewhere) split into the
// front line and shooters, the threats with their positions and the
// building lines of fire, the live pawn state and the stored layout.
func combatView(combat bridge.Combat, in combatInputs, orderable []domain.PawnID, layout domain.Fact[policy.CombatLayout]) policy.CombatView {
	owned := map[domain.PawnID]bool{}
	for _, id := range orderable {
		owned[id] = true
	}
	// The frame's colonist rows carry worn armor (#881), which ranks
	// blockers and tanks.
	armor := map[domain.PawnID]float64{}
	// Melee power (#969) is the MeleeDPS stat scaled by health.
	melee := map[domain.PawnID]domain.Fact[float64]{}
	for _, row := range combat.Pawns {
		if row.Armor != nil {
			armor[domain.PawnID(row.GetId())] = row.GetArmor()
		}
		if row.MeleePower != nil && row.Health != nil {
			melee[domain.PawnID(row.GetId())] = domain.Known(row.GetMeleePower() * row.GetHealth())
		}
	}
	var defenders []policy.SquadDefenderFacts
	var profiles []policy.PawnProfile
	for _, pawn := range combat.Emergency.Facts.Colonists {
		row := in.rows[string(pawn.ID)]
		d := squadDefenderFacts(row, in.needed, in.ranged[string(pawn.ID)])
		if a, ok := armor[d.ID]; ok {
			d.Armor = domain.Known(a)
		}
		d.MeleePower = melee[d.ID]
		if owned[d.ID] {
			d.DraftOwned = domain.Known(false)
		}
		defenders = append(defenders, d)
		profile := in.profiles[domain.PawnID(pawn.ID)]
		d.Warden = profile.Capable(policy.WorkWarden, 0)
		profiles = append(profiles, profile)
	}
	// The combat detail carries the biography, so the line split comes
	// from the same rows: holders take melee opponents, shooters ranged
	// ones and the firing cells.
	front, _ := policy.FrontLine(profiles)
	holds := map[domain.PawnID]bool{}
	for _, id := range front {
		holds[domain.PawnID(id)] = true
	}
	for i := range defenders {
		defenders[i].FrontLine = holds[defenders[i].ID]
	}
	var threats []policy.SquadThreatFacts
	// ReadCombat built the table, so it carries no error here.
	races, _ := combat.Catalog.AnimalRaces()
	positional := make([]policy.DefensiveThreatFacts, 0, len(in.hostileIDs))
	for _, id := range in.hostileIDs {
		row := in.rows[id]
		facts := squadThreatFacts(row, races, in.ranged[id])
		facts.Hunting = domain.Known(in.hunting[id])
		facts.MeleePower = melee[domain.PawnID(id)]
		threats = append(threats, facts)
		positional = append(positional, defensiveThreatFacts(row))
	}
	// A hunt origin's prey are threats the squad answers (#1617).
	for _, id := range in.prey {
		threats = append(threats, squadThreatFacts(in.rows[id], races, in.ranged[id]))
	}
	lines := buildingLinesOfFire(combat.Lines, in.buildings, defenders, in.rows, in.reach)
	for _, building := range in.buildings {
		threats = append(threats, policy.SquadThreatFacts{ID: building.ID, Dead: building.Dead, Building: true, LinesOfFire: lines[building.ID]})
	}
	var structures []policy.HostileStructure
	for _, b := range in.buildings {
		if dead, _ := b.Dead.Value(); !dead && len(b.Cells) > 0 {
			structures = append(structures, policy.HostileStructure{ID: domain.PawnID(b.ID), Def: b.Definition, Cell: b.Cells[0], Mortar: b.Mortar})
		}
	}
	var damaged []domain.Cell
	for _, door := range combat.Doors {
		damaged = append(damaged, domain.Cell{X: door.GetCell().GetX(), Z: door.GetCell().GetZ()})
	}
	return policy.CombatView{SiteExit: observation.CombatSiteExit(combat.World, domain.MapID(combat.Context.GetIdentity().GetMapId())), Hunt: len(in.prey) > 0, Tick: domain.Tick(combat.Context.GetTick()), Pawns: preyStates(combatPawnStates(combat, in.rows, in.weapons), in, combat.Catalog), Defenders: defenders, Threats: threats, Positional: positional, Orderable: orderable, Layout: layout, Pods: podArrival(combat), Rooms: combat.Rooms, DoorStates: combat.DoorStates, DamagedDoors: damaged, Mortars: combat.Mortars, Shells: combat.Shells, Structures: structures, OutdoorTemperatureC: combat.OutdoorTemperatureC, HiveTemperatureC: combat.HiveTemperatureC,
		Population: domain.Known(len(combat.Emergency.Facts.Colonists))}
}

// preyStates adds the hunt origin's prey the combat mirror does not list
// (it carries wild animals only beside a hostile) from their detail rows.
func preyStates(states []policy.CombatPawnState, in combatInputs, catalog *bridge.DefinitionCatalog) []policy.CombatPawnState {
	listed := map[domain.PawnID]bool{}
	for _, s := range states {
		listed[s.ID] = true
	}
	for _, id := range in.prey {
		row := in.rows[id]
		if listed[domain.PawnID(id)] {
			continue
		}
		s := policy.CombatPawnState{ID: domain.PawnID(id), Downed: row.GetDowned(), Dead: row.GetDead(), Wild: true}
		if position := row.GetPawn().GetPosition(); position != nil && position.X != nil && position.Z != nil {
			s.Cell = domain.Known(domain.Cell{X: position.GetX(), Z: position.GetZ()})
		}
		states = append(states, threatFacts(s, row, catalog))
	}
	return states
}

// podArrival is the frame's newest drop-pod arrival row (#870), for the
// pods tactic (#891).
func podArrival(combat bridge.Combat) domain.Fact[policy.PodArrival] {
	var newest *mirrorpb.CombatEventRow
	for _, row := range combat.Events {
		if bridge.DropPodArrival(row) && (newest == nil || bridge.CombatBefore(newest.GetAt(), row.GetAt())) {
			newest = row
		}
	}
	if newest == nil {
		return domain.Unknown[policy.PodArrival]()
	}
	pods := policy.PodArrival{Open: domain.Tick(newest.GetOpenTick())}
	for _, c := range newest.GetLandingCells() {
		pods.Landing = append(pods.Landing, domain.Cell{X: c.GetX(), Z: c.GetZ()})
	}
	return domain.Known(pods)
}

// buildingLinesOfFire is, for every standing hostile building, which
// eligible ranged-equipped defenders can fire on it from where they stand:
// the frame's native line of sight from the defender's cell to one of the
// building's occupied cells no further than the defender's weapon range.
// The native attack preview decides the shot itself; this only keeps a
// defender who could not shoot from being planned as a shooter (#327).
// The frame carries at most 64 cells a side; a defender or building cell
// it leaves out has no line, and that defender walks in.
func buildingLinesOfFire(read []bridge.LineOfFire, buildings []policy.EmergencyThreat, defenders []policy.SquadDefenderFacts, rows map[string]*n.PawnState, reaches map[string]float64) map[policy.PawnID]map[domain.PawnID]bool {
	lines := map[policy.PawnID]map[domain.PawnID]bool{}
	shooters := map[domain.Cell][]domain.PawnID{}
	weaponRange := map[domain.PawnID]float64{}
	for _, d := range defenders {
		row := rows[string(d.ID)]
		ranged, known := d.RangedEquipped.Value()
		if !known || !ranged || row == nil || row.Pawn.Position == nil {
			continue
		}
		reach := reaches[string(d.ID)]
		if reach <= 0 {
			continue
		}
		at := domain.Cell{X: row.Pawn.Position.GetX(), Z: row.Pawn.Position.GetZ()}
		shooters[at] = append(shooters[at], d.ID)
		weaponRange[d.ID] = reach
	}
	for _, line := range read {
		if !line.Known || !line.LineOfSight {
			continue
		}
		for _, b := range buildings {
			for _, cell := range b.Cells {
				if cell != line.To {
					continue
				}
				for _, id := range shooters[line.From] {
					if line.Distance <= weaponRange[id] {
						if lines[b.ID] == nil {
							lines[b.ID] = map[domain.PawnID]bool{}
						}
						lines[b.ID][id] = true
					}
				}
			}
		}
	}
	return lines
}

// primaryRange is the range of the pawn's primary ranged weapon in cells;
// zero when unarmed, melee-armed or unknown.
func (a armament) primaryRange(equipment *n.PawnEquipment) (float64, error) {
	weapon, known, err := a.primary(equipment)
	if err != nil || !known || !weapon.Ranged {
		return 0, err
	}
	return weapon.Range, nil
}

// defensiveThreatFacts reads the lord, distance and position evidence a hostile row
// carries; a missing field stays unknown so positions are never taken on a
// guess about how the raid arrives.
func defensiveThreatFacts(row *n.PawnState) policy.DefensiveThreatFacts {
	facts := policy.DefensiveThreatFacts{ID: policy.PawnID(row.Pawn.GetId()), Dead: boundary.FactBool(row.Dead), Downed: boundary.FactBool(row.Downed)}
	if row.Humanlike != nil {
		facts.Humanlike = domain.Known(row.GetHumanlike())
	}
	if row.LordJobClass != nil && !boundary.IssueField(row.Issues, "lord_job_class") {
		facts.LordJobClass = domain.Known(row.GetLordJobClass())
	}
	if row.LordToilClass != nil && !boundary.IssueField(row.Issues, "lord_toil_class") {
		facts.LordToilClass = domain.Known(row.GetLordToilClass())
	}
	if row.NearestColonistDistance != nil && !boundary.IssueField(row.Issues, "nearest_colonist_distance") {
		facts.NearestColonistDistance = domain.Known(row.GetNearestColonistDistance())
	}
	if position := row.Pawn.GetPosition(); position != nil && position.X != nil && position.Z != nil {
		facts.Position = domain.Known(domain.Cell{X: position.GetX(), Z: position.GetZ()})
	}
	return facts
}

// defenseMethodID names one admission of a defense method. The count of
// every method the goal ever admitted salts the hash so that re-planning
// the same assignments after an earlier method's actions were cancelled
// admits a new method instead of colliding with the retired one's key.
// The active method count is not that salt: it falls when a plan retires,
// and the same assignments then rehash to a plan id the journal still
// holds (#214).
func defenseMethodID(prefix string, admitted int, hash hash.Hash) domain.MethodID {
	fmt.Fprintf(hash, "#%d\n", admitted)
	method := domain.MethodID(fmt.Sprintf("%s-%x", prefix, hash.Sum(nil)[:16]))
	return method
}

// settleUnissuedWork cancels every pending or prepared action across the
// recovered goal's methods. Dispatched actions are left to their own
// reconciliation; once they close the goal has no open work and satisfies.
func (r *RoundsDefensePlanner) settleUnissuedWork(call context.Context, incident store.IncidentState) error {
	p := r.reviewer.player
	for _, method := range incident.Methods {
		if strings.HasPrefix(string(method.Method), stripMethodPrefix) {
			continue // the post-fight strip waits for its own dispatch (#1079)
		}
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return err
		}
		for _, progress := range plan.Progress {
			v := progress.View()
			if v.Stage != domain.Pending && v.Stage != domain.Prepared {
				continue
			}
			if _, err = p.journal.Cancel(call, method.Plan, v.Action); err != nil {
				return err
			}
		}
	}
	return nil
}

// orphanedDraftDependents lists the unissued movement and attack actions
// whose owned-draft prerequisite can no longer carry them: the draft (or,
// for an attack, the move it waits on) ended without completing. Only pending and prepared
// actions are named; anything native may still be executing is left to
// its own reconciliation.
func orphanedDraftDependents(spec domain.PlanSpec, progress []domain.Progress) []domain.ActionID {
	views := make(map[domain.ActionID]domain.ProgressView, len(progress))
	for _, p := range progress {
		views[p.View().Action] = p.View()
	}
	dead := func(id domain.ActionID) bool {
		v, ok := views[id]
		if !ok {
			return false
		}
		return v.Stage == domain.Unsuccessful || v.Stage == domain.Cancelled
	}
	requires := map[domain.ActionID][]domain.ActionID{}
	for _, d := range spec.Dependencies() {
		requires[d.Action] = append(requires[d.Action], d.Requires)
	}
	var out []domain.ActionID
	for _, action := range spec.Actions() {
		v, ok := views[action.ID()]
		if !ok || v.Stage != domain.Pending && v.Stage != domain.Prepared {
			continue
		}
		var draft domain.ActionID
		if m, ok := action.Movement(); ok {
			draft = m.DraftAction()
		} else if a, ok := action.Subdue(); ok {
			draft = a.DraftAction()
		} else {
			continue
		}
		orphan := dead(draft)
		for _, req := range requires[action.ID()] {
			orphan = orphan || dead(req)
		}
		if orphan {
			out = append(out, action.ID())
		}
	}
	return out
}

// defenseTargets are the threats squad defense answers: every hostile pawn,
// a predator hunting a colonist that the emergency holds the clock for (a
// distant one is watched by the native supervisor instead), reported in the
// hunting set, and, returned apart since they are not pawns to read, the
// standing hostile buildings (#246). Without the predator here the hold has
// no planner and autonomous play parks until the hunt ends on its own.
func defenseTargets(threats []policy.EmergencyThreat) ([]string, map[string]bool, []policy.EmergencyThreat) {
	var ids []string
	var buildings []policy.EmergencyThreat
	hunting := map[string]bool{}
	for _, threat := range threats {
		switch {
		case !threat.Engaging():
			// A dormant hive or idle insect is left alone (#948).
		case threat.Building():
			if dead, known := threat.Dead.Value(); known && !dead {
				buildings = append(buildings, threat)
			}
		case threat.Kind == policy.Hostile:
			ids = append(ids, string(threat.ID))
		case threat.Kind == policy.HuntingPredator && !threat.DistantThreat():
			ids = append(ids, string(threat.ID))
			hunting[string(threat.ID)] = true
		}
	}
	return ids, hunting, buildings
}
