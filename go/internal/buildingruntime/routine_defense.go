package buildingruntime

import (
	"context"
	"crypto/sha256"
	"fmt"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
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
	return r.step(call, epoch)
}
func (r *RoutineDefensePlanner) step(call, epoch context.Context) (RoutineDefenseResult, error) {
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
	if !found || goal.Goal.Status != domain.GoalActive || goal.Goal.Need != domain.NeedDeficit {
		return RoutineDefenseResult{Reason: BuildingMethodNoDeficit}, nil
	}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
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
	var hostileIDs []string
	for _, threat := range emergency.Facts.Threats {
		if threat.Kind == policy.Hostile {
			hostileIDs = append(hostileIDs, string(threat.ID))
		}
	}
	if len(emergency.Facts.Colonists) == 0 || len(hostileIDs) == 0 {
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
	for _, pawn := range emergency.Facts.Colonists {
		row := rows[string(pawn.ID)]
		if row == nil {
			return RoutineDefenseResult{}, ErrControl
		}
		defenders = append(defenders, squadDefenderFacts(row))
	}
	var threats []policy.SquadThreatFacts
	for _, id := range hostileIDs {
		row := rows[id]
		if row == nil {
			return RoutineDefenseResult{}, ErrControl
		}
		threats = append(threats, squadThreatFacts(row))
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
	method := domain.MethodID(fmt.Sprintf("squad-%x", hash.Sum(nil)[:16]))
	digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%d/%s", goal.Goal.ID, goal.Goal.Epoch, method)))
	id := domain.PlanID(fmt.Sprintf("routine-defense-%x", digest[:16]))
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
