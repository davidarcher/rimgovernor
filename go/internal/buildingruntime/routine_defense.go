package buildingruntime

import (
	"context"
	"crypto/sha256"
	"fmt"
	"hash"
	"sort"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

type RoutineDefenseSource interface {
	ReadEmergency(context.Context, *c.Identity) (bridge.EmergencyObservation, bridge.Result, error)
	ReadCombatPawns(context.Context, *c.Identity, []string) (*n.ListPawnsReply, bridge.Result, error)
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
		return nil, ErrControl
	}
	return &RoutineDefensePlanner{reviewer, native}, nil
}
func (r *RoutineDefensePlanner) Step(ctx context.Context) (RoutineDefenseResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, false)
	if err != nil {
		return RoutineDefenseResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}
func (r *RoutineDefensePlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoutineDefenseResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutineDefenseResult{Reason: BuildingMethodDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoutineDefenseResult{}, ErrControl
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutineDefenseResult{}, err
	}
	if !review.Enabled || review.Snapshot != state.Snapshot {
		return RoutineDefenseResult{Reason: BuildingMethodNoReview}, nil
	}
	var goal store.GoalState
	found := false
	for _, binding := range review.Goals {
		if binding.Need == policy.ActiveCombat {
			goal, err = p.journal.LoadGoal(call, binding.Goal)
			found = true
			break
		}
	}
	if err != nil {
		return RoutineDefenseResult{}, err
	}
	if found && goal.Goal.Status == domain.GoalActive && goal.Goal.Need == domain.NeedRecovered {
		// The raid ended before every action was issued: a squad draft the
		// worker prepared but never dispatched, and the moves and attacks
		// waiting on it or on a target that is now dead, would hold the
		// epoch open forever, so the goal could never satisfy and the next
		// raid could never open a fresh epoch (#226). Nothing native was
		// ordered for an unissued action, so cancelling it settles it.
		if err = r.settleUnissuedWork(call, goal); err != nil {
			return RoutineDefenseResult{}, err
		}
		return RoutineDefenseResult{Reason: BuildingMethodNoDeficit}, nil
	}
	if !found || goal.Goal.Status != domain.GoalActive || goal.Goal.Need != domain.NeedDeficit {
		return RoutineDefenseResult{Reason: BuildingMethodNoDeficit}, nil
	}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoutineDefenseResult{}, err
		}
		// A Manual cycle mid-raid (an interruption hold, then a resume)
		// releases every owned draft; the moves and attacks layered on
		// those drafts can then never dispatch, and while they stay open
		// the goal would never re-plan (#5 scenario 2). Cancel them so a
		// fresh hold or squad method is admitted against the live raid.
		for _, orphan := range orphanedDraftDependents(plan.Spec, plan.Progress) {
			if _, err = p.journal.Cancel(call, method.Plan, orphan); err != nil {
				return RoutineDefenseResult{}, err
			}
		}
		if plan, err = p.journal.LoadPlan(call, method.Plan); err != nil {
			return RoutineDefenseResult{}, err
		}
		if domain.GoalWorkOpen(plan.Progress) {
			return RoutineDefenseResult{Reason: BuildingMethodExistingWork}, nil
		}
	}
	started := r.reviewer.clock.Now()
	identity := boundary.Identity(state.Snapshot)
	emergency, _, err := r.native.ReadEmergency(call, identity)
	if err != nil {
		return RoutineDefenseResult{}, err
	}
	if _, err = boundary.Context(emergency.Context, state.Snapshot); err != nil || emergency.Context.GetTick() < int64(review.Tick) {
		return RoutineDefenseResult{}, ErrControl
	}
	colonistsComplete, ck := emergency.Facts.ColonistsComplete.Value()
	threatsComplete, tk := emergency.Facts.ThreatsComplete.Value()
	if !ck || !colonistsComplete || !tk || !threatsComplete {
		return RoutineDefenseResult{Reason: BuildingMethodUsed}, nil
	}
	hostileIDs, hunting, buildings := defenseTargets(emergency.Facts.Threats)
	if len(emergency.Facts.Colonists) == 0 || len(hostileIDs)+len(buildings) == 0 {
		return RoutineDefenseResult{Reason: BuildingMethodUsed}, nil
	}
	ids := make([]string, 0, len(emergency.Facts.Colonists)+len(hostileIDs))
	seenID := map[string]bool{}
	for _, pawn := range emergency.Facts.Colonists {
		if !seenID[string(pawn.ID)] {
			seenID[string(pawn.ID)] = true
			ids = append(ids, string(pawn.ID))
		}
	}
	for _, id := range hostileIDs {
		if !seenID[id] {
			seenID[id] = true
			ids = append(ids, id)
		}
	}
	reply, _, err := r.native.ReadCombatPawns(call, identity, ids)
	if err != nil {
		return RoutineDefenseResult{}, err
	}
	observed := reply.GetObserved()
	if observed == nil {
		return RoutineDefenseResult{}, ErrControl
	}
	if _, err = boundary.Context(observed.Context, state.Snapshot); err != nil {
		return RoutineDefenseResult{}, ErrControl
	}
	counts := observed.Completeness
	if counts == nil || counts.Page == nil || !counts.Page.GetComplete() || counts.Page.GetNextCursor() != "" || counts.Matched == nil || counts.Returned == nil || counts.Unreadable == nil || counts.GetUnreadable() != 0 || counts.GetMatched() != uint64(len(ids)) || counts.GetReturned() != uint64(len(ids)) || len(observed.Pawns) != len(ids) {
		return RoutineDefenseResult{}, ErrControl
	}
	rows := map[string]*n.PawnState{}
	for _, row := range observed.Pawns {
		if row == nil || row.Pawn == nil || rows[row.Pawn.GetId()] != nil {
			return RoutineDefenseResult{}, ErrControl
		}
		rows[row.Pawn.GetId()] = row
	}
	var defenders []policy.SquadDefenderFacts
	var profiles []policy.PawnProfile
	for _, pawn := range emergency.Facts.Colonists {
		row := rows[string(pawn.ID)]
		if row == nil {
			return RoutineDefenseResult{}, ErrControl
		}
		defenders = append(defenders, squadDefenderFacts(row))
		profiles = append(profiles, policy.BuildProfile(observation.WorkPawnRow(row)))
	}
	// The combat read carries the biography, so the line split comes from
	// the same rows: holders take melee opponents, shooters ranged ones
	// and the firing cells.
	front, _ := policy.FrontLine(profiles)
	holds := map[domain.PawnID]bool{}
	for _, id := range front {
		holds[domain.PawnID(id)] = true
	}
	for i := range defenders {
		defenders[i].FrontLine = holds[defenders[i].ID]
	}
	var threats []policy.SquadThreatFacts
	for _, id := range hostileIDs {
		row := rows[id]
		if row == nil {
			return RoutineDefenseResult{}, ErrControl
		}
		facts := squadThreatFacts(row)
		facts.Hunting = domain.Known(hunting[id])
		threats = append(threats, facts)
	}
	for _, building := range buildings {
		threats = append(threats, policy.SquadThreatFacts{ID: building.ID, Dead: building.Dead, Building: true})
	}
	// A complete defensive layout against an ordinary edge assault sends the
	// ranged line to its firing cells first; anything else is squad defense.
	if held, err := r.holdTheLine(call, epoch, goal, state, started, arbiter, hostileIDs, rows, defenders); err != nil || held.Reason != "" {
		return held, err
	}
	var assignments []policy.SquadAssignment
	var ok bool
	if len(threats) == 1 {
		assignments, ok = policy.SelectTribalRaiderDefense(threats[0], defenders)
	}
	if !ok {
		assignments, ok = policy.SelectSquadDefense(threats, defenders)
	}
	if !ok {
		return RoutineDefenseResult{Reason: BuildingMethodNoSquad}, nil
	}
	defenderIDs := make([]domain.PawnID, 0, len(assignments))
	for _, a := range assignments {
		defenderIDs = append(defenderIDs, a.Defender)
	}
	if !arbiter.tryClaim(defenderIDs) {
		return RoutineDefenseResult{Reason: BuildingMethodUsed}, nil
	}
	sort.Slice(assignments, func(i, j int) bool {
		if assignments[i].Defender != assignments[j].Defender {
			return assignments[i].Defender < assignments[j].Defender
		}
		return assignments[i].Target < assignments[j].Target
	})
	hash := sha256.New()
	for _, a := range assignments {
		fmt.Fprintf(hash, "%s/%s/%d\n", a.Defender, a.Target, a.Mode)
	}
	method, id := defenseMethodIDs("squad", goal, hash)
	var actions []domain.Action
	for _, a := range assignments {
		draftID := domain.ActionID(fmt.Sprintf("%s-draft-%s", id, a.Defender))
		draft, err := domain.NewOwnedDraft(a.Defender)
		if err != nil {
			return RoutineDefenseResult{}, err
		}
		draftAction, err := domain.NewOwnedDraftAction(draftID, draft)
		if err != nil {
			return RoutineDefenseResult{}, err
		}
		actions = append(actions, draftAction)
		attackID := domain.ActionID(fmt.Sprintf("%s-attack-%s-%s", id, a.Defender, a.Target))
		var attackAction domain.Action
		switch a.Mode {
		case policy.SquadRanged:
			intent, err := domain.NewRangedAttack(a.Defender, domain.PawnID(a.Target), draftID)
			if err != nil {
				return RoutineDefenseResult{}, err
			}
			attackAction, err = domain.NewRangedAttackAction(attackID, intent)
			if err != nil {
				return RoutineDefenseResult{}, err
			}
		default:
			intent, err := domain.NewMeleeAttack(a.Defender, domain.PawnID(a.Target), draftID)
			if err != nil {
				return RoutineDefenseResult{}, err
			}
			attackAction, err = domain.NewMeleeAttackAction(attackID, intent)
			if err != nil {
				return RoutineDefenseResult{}, err
			}
		}
		actions = append(actions, attackAction)
	}
	plan, err := domain.NewPlan(id, 1, actions)
	if err != nil {
		return RoutineDefenseResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutineDefenseResult{}, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoutineDefenseResult{}, ErrControl
	}
	if _, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, method, plan); err != nil {
		return RoutineDefenseResult{}, err
	}
	return RoutineDefenseResult{Reason: BuildingMethodAdmitted, Plan: id}, nil
}

// holdTheLine returns a zero Reason when the stored layout is absent or
// incomplete or the threat is not an edge assault, so the caller continues
// with squad defense. Otherwise it commits draft, movement to the firing cell
// and ranged attack for every positioned defender.
func (r *RoutineDefensePlanner) holdTheLine(call, epoch context.Context, goal store.GoalState, state ControlState, started time.Time, arbiter *stepArbiter, hostileIDs []string, rows map[string]*n.PawnState, defenders []policy.SquadDefenderFacts) (RoutineDefenseResult, error) {
	p := r.reviewer.player
	layout, ok, err := p.journal.LoadDefenseLayout(call, store.World{Colony: state.Snapshot.Colony, Load: state.Snapshot.Load, Map: state.Snapshot.Map})
	if err != nil {
		return RoutineDefenseResult{}, err
	}
	// A complete record from an earlier load of this colony still holds:
	// its geometry is on the map and its completion was census-verified
	// when written. Combat cannot wait for the layout review to adopt it
	// (that review does not run under a raid), and a wall lost since is the
	// same degradation a raider causes mid-session.
	if !ok || !layout.Complete {
		return RoutineDefenseResult{}, nil
	}
	threats := make([]policy.DefensiveThreatFacts, 0, len(hostileIDs))
	for _, id := range hostileIDs {
		threats = append(threats, defensiveThreatFacts(rows[id]))
	}
	positions, ok := policy.SelectDefensivePositions(layout.Firing, threats, defenders)
	if !ok {
		return RoutineDefenseResult{}, nil
	}
	defenderIDs := make([]domain.PawnID, 0, len(positions))
	for _, a := range positions {
		defenderIDs = append(defenderIDs, a.Defender)
	}
	if !arbiter.tryClaim(defenderIDs) {
		return RoutineDefenseResult{Reason: BuildingMethodUsed}, nil
	}
	hash := sha256.New()
	for _, a := range positions {
		fmt.Fprintf(hash, "%s/%d/%d/%s\n", a.Defender, a.Cell.X, a.Cell.Z, a.Target)
	}
	method, id := defenseMethodIDs("hold", goal, hash)
	var actions []domain.Action
	var dependencies []domain.ActionDependency
	for _, a := range positions {
		draftID := domain.ActionID(fmt.Sprintf("%s-draft-%s", id, a.Defender))
		draft, err := domain.NewOwnedDraft(a.Defender)
		if err != nil {
			return RoutineDefenseResult{}, err
		}
		draftAction, err := domain.NewOwnedDraftAction(draftID, draft)
		if err != nil {
			return RoutineDefenseResult{}, err
		}
		moveID := domain.ActionID(fmt.Sprintf("%s-move-%s", id, a.Defender))
		movement, err := domain.NewMovement(a.Defender, a.Cell, draftID)
		if err != nil {
			return RoutineDefenseResult{}, err
		}
		moveAction, err := domain.NewMovementAction(moveID, movement)
		if err != nil {
			return RoutineDefenseResult{}, err
		}
		attackID := domain.ActionID(fmt.Sprintf("%s-attack-%s-%s", id, a.Defender, a.Target))
		intent, err := domain.NewRangedAttack(a.Defender, domain.PawnID(a.Target), draftID)
		if err != nil {
			return RoutineDefenseResult{}, err
		}
		attackAction, err := domain.NewRangedAttackAction(attackID, intent)
		if err != nil {
			return RoutineDefenseResult{}, err
		}
		// The move already depends on the draft through its prerequisite;
		// the attack waits for the defender to reach the firing cell.
		actions = append(actions, draftAction, moveAction, attackAction)
		dependencies = append(dependencies, domain.ActionDependency{Action: attackID, Requires: moveID})
	}
	plan, err := domain.NewPlan(id, 1, actions, dependencies...)
	if err != nil {
		return RoutineDefenseResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutineDefenseResult{}, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoutineDefenseResult{}, ErrControl
	}
	if _, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, method, plan); err != nil {
		return RoutineDefenseResult{}, err
	}
	return RoutineDefenseResult{Reason: BuildingMethodAdmitted, Plan: id}, nil
}

// defensiveThreatFacts reads the lord and distance evidence a hostile row
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
	return facts
}

// defenseMethodIDs names one admission of a defense method. The count of
// every method the goal ever admitted salts the hash so that re-planning
// the same assignments after an earlier method's actions were cancelled
// admits a new plan instead of colliding with the retired one's identity.
// The active method count is not that salt: it falls when a plan retires,
// and the same assignments then rehash to a plan id the journal still
// holds (#214).
func defenseMethodIDs(prefix string, goal store.GoalState, hash hash.Hash) (domain.MethodID, domain.PlanID) {
	fmt.Fprintf(hash, "#%d\n", goal.Admitted)
	method := domain.MethodID(fmt.Sprintf("%s-%x", prefix, hash.Sum(nil)[:16]))
	digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%d/%s", goal.Goal.ID, goal.Goal.Epoch, method)))
	return method, domain.PlanID(fmt.Sprintf("routine-defense-%x", digest[:16]))
}

// settleUnissuedWork cancels every pending or prepared action across the
// recovered goal's methods. Dispatched actions are left to their own
// reconciliation; once they close the goal has no open work and satisfies.
func (r *RoutineDefensePlanner) settleUnissuedWork(call context.Context, goal store.GoalState) error {
	p := r.reviewer.player
	for _, method := range goal.Methods {
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
