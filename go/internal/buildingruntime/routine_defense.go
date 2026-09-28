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
	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	mirrorpb "github.com/davidarcher/RimGovernor/go/internal/wire/mirrorpb"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
)

type RoutineDefenseSource interface {
	// CombatOrders sends a stop's changed orders (#850, #852).
	CombatOrders(context.Context, *a.WritePrecondition, *op.CombatOrders) ([]bridge.CombatOrderResult, *op.ExecuteReply, bridge.Result, error)
	CombatGeometry(context.Context, *mirrorpb.CombatGeometryRequest) (*mirrorpb.CombatGeometry, bridge.Result, error)
	// ReadCombat is the newest snapshot frame's combat state and the
	// fight's other inputs (#851, #853, #858).
	ReadCombat(context.Context, *c.Identity) (bridge.Combat, error)
}
type RoutineDefensePlanner struct {
	reviewer *RoutineReviewer
	native   RoutineDefenseSource
}
type RoutineDefenseResult struct {
	Reason RoutineBuildingReason
	Plan   domain.PlanID
}

func NewRoutineDefensePlanner(reviewer *RoutineReviewer, native RoutineDefenseSource) (*RoutineDefensePlanner, error) {
	if reviewer == nil || native == nil {
		return nil, fmt.Errorf("%w: NewRoutineDefensePlanner: reviewer == nil || native == nil", ErrControl)
	}
	return &RoutineDefensePlanner{reviewer, native}, nil
}
func (r *RoutineDefensePlanner) decide(call, epoch context.Context, arbiter *stepArbiter) (RoutineDefenseResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutineDefenseResult{Reason: BuildingMethodDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoutineDefenseResult{}, fmt.Errorf("%w: decide: !state.ObservationKnown || state.Snapshot.Validate() != nil", ErrControl)
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutineDefenseResult{}, err
	}
	if !review.Enabled || review.Snapshot != state.Snapshot {
		return RoutineDefenseResult{Reason: BuildingMethodNoReview}, nil
	}
	incident, need, found, err := routineIncident(call, p.journal, review, policy.ActiveCombat)
	if err != nil {
		return RoutineDefenseResult{}, err
	}
	if found && need == domain.NeedRecovered {
		// The raid ended before every action was issued: a squad draft the
		// worker prepared but never dispatched, and the moves and attacks
		// waiting on it or on a target that is now dead, would hold the
		// epoch open forever, so the goal could never satisfy and the next
		// raid could never open a fresh epoch (#226). Nothing native was
		// ordered for an unissued action, so cancelling it settles it.
		if err = r.settleUnissuedWork(call, incident); err != nil {
			return RoutineDefenseResult{}, err
		}
		// The fight is over: closing it lets draft cleanup release its
		// defenders, and the goal satisfies once they are released.
		for _, method := range incident.Methods {
			if strings.HasPrefix(string(method.Method), combatMethodPrefix) {
				if err = p.journal.CloseCombatFight(call, method.Plan); err != nil {
					return RoutineDefenseResult{}, err
				}
			}
		}
		return RoutineDefenseResult{Reason: BuildingMethodNoDeficit}, nil
	}
	if !found || need != domain.NeedDeficit || review.VetoIncident(incident.Incident) != "" {
		return RoutineDefenseResult{Reason: BuildingMethodNoDeficit}, nil
	}
	// One ActiveCombat plan owns the fight (#852): its combat method whose
	// fight is open or still holds claims (#910) is the fight the stop
	// decides for. Any other open work waits.
	var fight *store.CombatFight
	var fightPlan domain.PlanID
	for _, method := range incident.Methods {
		if strings.HasPrefix(string(method.Method), combatMethodPrefix) {
			record, ok, err := p.journal.LoadCombatFight(call, method.Plan)
			if err != nil {
				return RoutineDefenseResult{}, err
			}
			if !ok || !record.Holds() {
				continue
			}
			if fight != nil {
				return RoutineDefenseResult{Reason: BuildingMethodExistingWork}, nil
			}
			fight, fightPlan = &record, method.Plan
			continue
		}
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoutineDefenseResult{}, err
		}
		if store.PlanOpen(plan) {
			return RoutineDefenseResult{Reason: BuildingMethodExistingWork}, nil
		}
	}
	started := r.reviewer.clock.Now()
	identity := boundary.Identity(state.Snapshot)
	// The fight decides from one frame (#853): the census, the combat
	// detail rows, the building lines of fire and the combat pawns and
	// events are all as of its tick.
	combat, err := r.native.ReadCombat(call, identity)
	if err != nil {
		return RoutineDefenseResult{}, err
	}
	if _, err = boundary.Context(combat.Context, state.Snapshot); err != nil || combat.Emergency.Context == nil || combat.Context.GetTick() < int64(review.Tick) {
		return RoutineDefenseResult{}, fmt.Errorf("%w: decide: err != nil || combat.Emergency.Context == nil || combat.Context.GetTick() < int64(review.Tick)", ErrControl)
	}
	in, reason, err := combatFrameInputs(combat)
	if err != nil || reason != "" {
		return RoutineDefenseResult{Reason: reason}, err
	}
	emergency, rows := combat.Emergency, in.rows
	if result, err := r.planBreak(call, epoch, incident, state, started, arbiter, emergency.Facts, rows); err != nil || result.Reason != "" {
		return result, err
	}
	var orderable []domain.PawnID
	var memory policy.CombatMemory
	if fight != nil {
		if !fight.Open {
			return RoutineDefenseResult{Reason: BuildingMethodExistingWork}, nil
		}
		memory = fight.Memory
		if orderable, err = learnFightClaims(call, p.journal, fightPlan, *fight, rows); err != nil {
			return RoutineDefenseResult{}, err
		}
	}
	// A complete record from an earlier load of this colony still holds:
	// its geometry is on the map and its completion was census-verified
	// when written. Combat cannot wait for the layout review to adopt it
	// (that review does not run under a raid), and a wall lost since is the
	// same degradation a raider causes mid-session.
	layout, ok, err := p.journal.LoadDefenseLayout(call, store.World{Colony: state.Snapshot.Colony, Load: state.Snapshot.Load, Map: state.Snapshot.Map})
	if err != nil {
		return RoutineDefenseResult{}, err
	}
	var held domain.Fact[policy.CombatLayout]
	if ok && layout.Complete {
		combatLayout := policy.CombatLayout{Firing: layout.Firing, Retreat: layout.Retreat, Toward: layout.Toward}
		if n := len(layout.SafeLane); n > 0 {
			// The civilian lane runs entry to exit; its last cell is the
			// corridor's mouth on our side, where blockers hold (#864).
			combatLayout.Choke = domain.Known(layout.SafeLane[n-1])
		}
		held = domain.Known(combatLayout)
	}
	view := combatView(combat, in, orderable, held)
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
		slog.Default().InfoContext(call, "hold refused: "+next.Refusal, telemetry.ComponentKey, "routine-defense", telemetry.KindKey, "hold_refused", "incident", string(incident.Incident.ID))
	}
	if fight == nil {
		result, err := r.admitFight(call, epoch, incident, state, started, arbiter, fightAdmission{combat: combat, in: in, held: held, reply: recorded.Reply, stop: stop, memory: next})
		if err == nil && result.Reason == BuildingMethodAdmitted {
			recorded.Plan = result.Plan
			recordCombatStop(call, combat, recorded)
		}
		return result, err
	}
	id := fightPlan
	recorded.Plan = id
	// A role pawn the fight holds no claim on (a later formation's evacuee
	// or responder, #911) is drafted in this stop's batch, and the stop
	// decides as it will be once drafted, as the admission does.
	var drafts []domain.PawnID
	unclaimed := unclaimedRoles(next, fight.Claims, view)
	if len(unclaimed) > 0 {
		// A pawn still taking its loadout (#1115) drafts once it settles.
		plan, err := p.journal.LoadPlan(call, id)
		if err != nil {
			return RoutineDefenseResult{}, err
		}
		pending := loadoutPending(plan)
		unclaimed = slices.DeleteFunc(unclaimed, func(pawn domain.PawnID) bool { return pending[pawn] })
	}
	if len(unclaimed) > 0 && arbiter.tryClaim(unclaimed) {
		drafts = unclaimed
		recorded.Orderable = append(slices.Clone(orderable), drafts...)
		slices.Sort(recorded.Orderable)
		view = combatView(combat, in, recorded.Orderable, held)
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
	if len(orders)+len(drafts) == 0 {
		// A stop that changes nothing writes nothing; a re-formation that
		// could order no one yet keeps its roles for the next stop.
		if next.Formed != memory.Formed || next.Tactic != memory.Tactic || !reflect.DeepEqual(next.Roles, memory.Roles) {
			if err = p.journal.SaveCombatMemory(call, id, next); err != nil {
				return RoutineDefenseResult{}, err
			}
			recordCombatStop(call, combat, recorded)
			if memory.Tactic == policy.TacticHold && next.Tactic != policy.TacticHold {
				return RoutineDefenseResult{Reason: BuildingMethodHoldFallback, Plan: id}, nil
			}
		}
		return RoutineDefenseResult{Reason: BuildingMethodExistingWork, Plan: id}, nil
	}
	if err = p.current(call, epoch); err != nil {
		return RoutineDefenseResult{}, err
	}
	record, next, err := r.issueCombatOrders(call, state, id, view, drafts, orders, next)
	if err != nil {
		return RoutineDefenseResult{}, err
	}
	record.Stop = stop
	if err = p.journal.RecordCombatStop(call, id, record, next); err != nil {
		return RoutineDefenseResult{}, err
	}
	// MemoryOut stays DecideCombat's: the refused orders' Forget is the
	// caller's, read from the evidence.
	recorded.Evidence = true
	recordCombatStop(call, combat, recorded)
	return RoutineDefenseResult{Reason: BuildingMethodCombatOrders, Plan: id}, nil
}

// learnFightClaims is the fight's orderable defenders: the pawns whose
// draft claim it knows (#910). A claim the admission batch left unknown
// (an uncertain receipt) is settled from the pawn's frame row: a claim
// owning the draft is the fight's, an unowned pawn was never drafted.
func learnFightClaims(ctx context.Context, journal *store.Store, plan domain.PlanID, fight store.CombatFight, rows map[string]*n.PawnState) ([]domain.PawnID, error) {
	set := map[domain.PawnID]string{}
	var drop, orderable []domain.PawnID
	for pawn, claim := range fight.Claims {
		if claim == "" {
			switch state := rows[string(pawn)].GetDraftClaim().GetState().(type) {
			case *n.DraftClaimObservation_Owned:
				if claim = state.Owned.GetClaimId(); claim != "" {
					set[pawn] = claim
				}
			case *n.DraftClaimObservation_Unowned:
				drop = append(drop, pawn)
			}
		}
		if claim != "" {
			orderable = append(orderable, pawn)
		}
	}
	slices.Sort(orderable)
	if len(set)+len(drop) == 0 {
		return orderable, nil
	}
	return orderable, journal.RecordCombatClaims(ctx, plan, set, drop)
}

// combatInputs is what the fight reads from one frame beside the view:
// the hostiles and hunting predators it answers, the standing hostile
// buildings, and every census colonist's and those threats' detail rows.
type combatInputs struct {
	hostileIDs []string
	hunting    map[string]bool
	buildings  []policy.EmergencyThreat
	rows       map[string]*n.PawnState
}

// combatFrameInputs is the fight's inputs from the frame; a reason means
// there is no fight to decide (an incomplete census, no threat).
func combatFrameInputs(combat bridge.Combat) (combatInputs, RoutineBuildingReason, error) {
	facts := combat.Emergency.Facts
	colonistsComplete, ck := facts.ColonistsComplete.Value()
	if !ck || !colonistsComplete {
		return combatInputs{}, BuildingMethodUsed, nil
	}
	var in combatInputs
	in.hostileIDs, in.hunting, in.buildings = defenseTargets(facts.Threats)
	// Raiders still in their pods (#908) are a fight with no hostile yet:
	// the pods tactic drafts the nearest armed before the open (#891).
	if len(facts.Colonists) == 0 || len(in.hostileIDs)+len(in.buildings) == 0 && !hasAggressiveBreak(facts) && facts.PodsOpen == 0 {
		return combatInputs{}, BuildingMethodUsed, nil
	}
	in.rows = map[string]*n.PawnState{}
	for _, pawn := range facts.Colonists {
		in.rows[string(pawn.ID)] = combat.Detail[string(pawn.ID)]
	}
	for _, id := range in.hostileIDs {
		in.rows[id] = combat.Detail[id]
	}
	for _, row := range in.rows {
		if row == nil {
			// The frame's detail misses a pawn its census lists.
			return combatInputs{}, "", fmt.Errorf("%w: combatFrameInputs: row == nil", ErrControl)
		}
	}
	return in, "", nil
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
		d := squadDefenderFacts(row)
		if a, ok := armor[d.ID]; ok {
			d.Armor = domain.Known(a)
		}
		d.MeleePower = melee[d.ID]
		if owned[d.ID] {
			d.DraftOwned = domain.Known(false)
		}
		defenders = append(defenders, d)
		profile := policy.BuildProfile(observation.WorkPawnRow(row))
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
	positional := make([]policy.DefensiveThreatFacts, 0, len(in.hostileIDs))
	for _, id := range in.hostileIDs {
		row := in.rows[id]
		facts := squadThreatFacts(row)
		facts.Hunting = domain.Known(in.hunting[id])
		facts.MeleePower = melee[domain.PawnID(id)]
		threats = append(threats, facts)
		positional = append(positional, defensiveThreatFacts(row))
	}
	lines := buildingLinesOfFire(combat.Lines, in.buildings, defenders, in.rows)
	for _, building := range in.buildings {
		threats = append(threats, policy.SquadThreatFacts{ID: building.ID, Dead: building.Dead, Building: true, LinesOfFire: lines[building.ID]})
	}
	var structures []policy.HostileStructure
	for _, b := range in.buildings {
		if dead, _ := b.Dead.Value(); !dead && len(b.Cells) > 0 {
			structures = append(structures, policy.HostileStructure{ID: domain.PawnID(b.ID), Def: b.Definition, Cell: b.Cells[0]})
		}
	}
	var damaged []domain.Cell
	for _, door := range combat.Doors {
		damaged = append(damaged, domain.Cell{X: door.GetCell().GetX(), Z: door.GetCell().GetZ()})
	}
	return policy.CombatView{Tick: domain.Tick(combat.Context.GetTick()), Pawns: combatPawnStates(combat, in.rows), Defenders: defenders, Threats: threats, Positional: positional, Orderable: orderable, Layout: layout, Pods: podArrival(combat), Rooms: combat.Rooms, DamagedDoors: damaged, Mortars: combat.Mortars, Structures: structures,
		Population: domain.Known(len(combat.Emergency.Facts.Colonists))}
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
func buildingLinesOfFire(read []bridge.LineOfFire, buildings []policy.EmergencyThreat, defenders []policy.SquadDefenderFacts, rows map[string]*n.PawnState) map[policy.PawnID]map[domain.PawnID]bool {
	lines := map[policy.PawnID]map[domain.PawnID]bool{}
	shooters := map[domain.Cell][]domain.PawnID{}
	weaponRange := map[domain.PawnID]float64{}
	for _, d := range defenders {
		row := rows[string(d.ID)]
		ranged, known := d.RangedEquipped.Value()
		if !known || !ranged || row == nil || row.Pawn.Position == nil {
			continue
		}
		reach := primaryRange(row.Equipment)
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
func primaryRange(equipment *n.PawnEquipment) float64 {
	if equipment == nil || equipment.PrimaryId == nil {
		return 0
	}
	for _, item := range equipment.Equipped {
		if item.GetThing().GetId() == equipment.GetPrimaryId() && item.GetRanged() && item.Range != nil && item.GetRange() > 0 {
			return item.GetRange()
		}
	}
	return 0
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
func (r *RoutineDefensePlanner) settleUnissuedWork(call context.Context, incident store.IncidentState) error {
	p := r.reviewer.player
	for _, method := range incident.Methods {
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
// whose owned-draft prerequisite can no longer carry them: the draft's
// claim was released or superseded, or the draft (or, for an attack, the
// move it waits on) ended without completing. Only pending and prepared
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
		if v.Stage == domain.Unsuccessful || v.Stage == domain.Cancelled {
			return true
		}
		cleanup, known := v.DraftCleanup.Value()
		return known && (cleanup.Stage == domain.DraftReleased || cleanup.Stage == domain.DraftSuperseded)
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
		} else if a, ok := action.RangedAttack(); ok {
			draft = a.DraftAction()
		} else if a, ok := action.MeleeAttack(); ok {
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
