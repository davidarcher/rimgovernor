package buildingruntime

import (
	"context"
	"crypto/sha256"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// RoutineEquipSource reuses the generic combat pawn read (armed/violence
// facts) plus the new bridge.ReadEquipWeapons for loose-weapon discovery.
// Skills, traits and positions come from the same combat pawn observation.
type RoutineEquipSource interface {
	ReadEmergency(context.Context, *c.Identity) (bridge.EmergencyObservation, bridge.Result, error)
	ReadCombatPawns(context.Context, *c.Identity, []string) (*n.ListPawnsReply, bridge.Result, error)
	ReadMapBounds(context.Context, *c.Identity, domain.Cell) (bridge.MapBounds, bridge.Result, error)
	ReadEquipWeapons(context.Context, *c.Identity, domain.Cell, domain.Cell) (bridge.EquipRead, bridge.Result, error)
}
type RoutineEquipPlanner struct {
	reviewer *RoutineReviewer
	native   RoutineEquipSource
}
type RoutineEquipResult struct {
	Reason RoutineBuildingReason
	Plan   domain.PlanID
}

func NewRoutineEquipPlanner(reviewer *RoutineReviewer, native RoutineEquipSource) (*RoutineEquipPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, ErrControl
	}
	return &RoutineEquipPlanner{reviewer, native}, nil
}
func (r *RoutineEquipPlanner) Step(ctx context.Context) (RoutineEquipResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, false)
	if err != nil {
		return RoutineEquipResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}
func (r *RoutineEquipPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoutineEquipResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutineEquipResult{Reason: BuildingMethodDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoutineEquipResult{}, ErrControl
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutineEquipResult{}, err
	}
	if !review.Enabled || review.Snapshot != state.Snapshot {
		return RoutineEquipResult{Reason: BuildingMethodNoReview}, nil
	}
	var goal store.GoalState
	found := false
	for _, binding := range review.Goals {
		if binding.Need == policy.EnsureBasicDefense {
			goal, err = p.journal.LoadGoal(call, binding.Goal)
			found = true
			break
		}
	}
	if err != nil {
		return RoutineEquipResult{}, err
	}
	if !found || goal.Goal.Status != domain.GoalActive || goal.Goal.Need != domain.NeedDeficit {
		return RoutineEquipResult{Reason: BuildingMethodNoDeficit}, nil
	}
	attemptsByPawn := map[domain.PawnID]int{}
	ownedWeapons := map[domain.PawnID]string{}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoutineEquipResult{}, err
		}
		if domain.GoalWorkOpen(plan.Progress) {
			return RoutineEquipResult{Reason: BuildingMethodExistingWork}, nil
		}
		for _, progress := range plan.Progress {
			if equip, ok := progress.Action().Equip(); ok {
				if method.Epoch == goal.Goal.Epoch {
					attemptsByPawn[equip.Pawn()]++
				}
				if progress.View().Stage == domain.Completed {
					ownedWeapons[equip.Pawn()] = equip.Thing()
				}
			}
		}
	}
	started := r.reviewer.clock.Now()
	identity := boundary.Identity(state.Snapshot)
	emergency, _, err := r.native.ReadEmergency(call, identity)
	if err != nil {
		return RoutineEquipResult{}, err
	}
	if _, err = boundary.Context(emergency.Context, state.Snapshot); err != nil || emergency.Context.GetTick() < int64(review.Tick) {
		return RoutineEquipResult{}, ErrControl
	}
	complete, known := emergency.Facts.ColonistsComplete.Value()
	if !known || !complete || len(emergency.Facts.Colonists) == 0 {
		return RoutineEquipResult{Reason: BuildingMethodUsed}, nil
	}
	ids := make([]string, 0, len(emergency.Facts.Colonists))
	for _, pawn := range emergency.Facts.Colonists {
		ids = append(ids, string(pawn.ID))
	}
	reply, _, err := r.native.ReadCombatPawns(call, identity, ids)
	if err != nil {
		return RoutineEquipResult{}, err
	}
	observed := reply.GetObserved()
	if observed == nil {
		return RoutineEquipResult{}, ErrControl
	}
	if _, err = boundary.Context(observed.Context, state.Snapshot); err != nil {
		return RoutineEquipResult{}, ErrControl
	}
	counts := observed.Completeness
	if counts == nil || counts.Page == nil || !counts.Page.GetComplete() || counts.Page.GetNextCursor() != "" || counts.Matched == nil || counts.Returned == nil || counts.Unreadable == nil || counts.GetUnreadable() != 0 || counts.GetMatched() != uint64(len(ids)) || counts.GetReturned() != uint64(len(ids)) || len(observed.Pawns) != len(ids) {
		return RoutineEquipResult{}, ErrControl
	}
	var pawns []policy.EquipCandidatePawn
	seen := map[string]bool{}
	for _, row := range observed.Pawns {
		if row == nil || row.Pawn == nil || seen[row.Pawn.GetId()] {
			return RoutineEquipResult{}, ErrControl
		}
		seen[row.Pawn.GetId()] = true
		facts := equipCandidatePawnFacts(row)
		if current := row.GetEquipment().GetPrimaryId(); current != "" && ownedWeapons[facts.Pawn] == current {
			for _, item := range row.GetEquipment().GetEquipped() {
				if item.GetThing().GetId() == current {
					facts.Current = &policy.EquipCandidateWeapon{Thing: current, Definition: item.GetThing().GetDefName(), Class: policy.ClassifyWeapon(true, item.GetRanged(), item.GetMelee())}
					facts.AutomationOwned = true
				}
			}
		}
		pawns = append(pawns, facts)
	}
	bounds, _, err := r.native.ReadMapBounds(call, identity, domain.Cell{X: 0, Z: 0})
	if err != nil {
		return RoutineEquipResult{}, err
	}
	if _, err = boundary.Context(bounds.Context, state.Snapshot); err != nil || bounds.Bounds.Width <= 0 || bounds.Bounds.Height <= 0 {
		return RoutineEquipResult{}, ErrControl
	}
	weapons, _, err := r.native.ReadEquipWeapons(call, identity, domain.Cell{X: 0, Z: 0}, domain.Cell{X: bounds.Bounds.Width - 1, Z: bounds.Bounds.Height - 1})
	if err != nil {
		return RoutineEquipResult{}, err
	}
	if _, err = boundary.Context(weapons.Context, state.Snapshot); err != nil {
		return RoutineEquipResult{}, ErrControl
	}
	var candidates []policy.EquipCandidateWeapon
	for _, w := range weapons.Targets {
		candidates = append(candidates, policy.EquipCandidateWeapon{Thing: w.Thing, Definition: w.Definition, Cell: w.Cell, Class: policy.ClassifyWeapon(w.ByTrade, w.Ranged, w.Melee)})
	}
	// Exclude unavailable pawns before matching so they cannot consume a weapon
	// another colonist could use. The arbiter lock spans selection and claims.
	arbiter.mu.Lock()
	pool := make([]policy.EquipCandidatePawn, 0, len(pawns))
	exhausted := false
	for _, pawn := range pawns {
		attempts := attemptsByPawn[pawn.Pawn]
		if attempts >= maxMedicalAttemptsPerPatient {
			exhausted = true
			continue
		}
		if !arbiter.pawns[pawn.Pawn] {
			pool = append(pool, pawn)
		}
	}
	available := make([]policy.EquipCandidateWeapon, 0, len(candidates))
	for _, weapon := range candidates {
		if !arbiter.resources["equip-weapon:"+weapon.Thing] {
			available = append(available, weapon)
		}
	}
	assignments := policy.AssignEquip(pool, available)
	for _, pair := range assignments {
		arbiter.pawns[pair.Pawn] = true
		arbiter.resources["equip-weapon:"+pair.Weapon.Thing] = true
	}
	arbiter.mu.Unlock()
	if len(assignments) == 0 {
		if exhausted {
			return RoutineEquipResult{Reason: BuildingMethodExhausted}, nil
		}
		return RoutineEquipResult{Reason: BuildingMethodUsed}, nil
	}
	// A single plan contains independent equip actions: no pawn waits for a
	// preceding pawn's native postcondition before its order can dispatch.
	method := domain.MethodID(fmt.Sprintf("equip-wave-%d", len(goal.Methods)))
	digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%d/%s", goal.Goal.ID, goal.Goal.Epoch, method)))
	id := domain.PlanID(fmt.Sprintf("routine-equip-%x", digest[:16]))
	actions := make([]domain.Action, 0, len(assignments))
	for i, pair := range assignments {
		equip, err := domain.NewEquip(pair.Pawn, pair.Weapon.Thing, pair.Weapon.Definition, pair.Weapon.Cell)
		if err != nil {
			return RoutineEquipResult{}, err
		}
		action, err := domain.NewEquipAction(domain.ActionID(fmt.Sprintf("%s-%d", id, i)), equip)
		if err != nil {
			return RoutineEquipResult{}, err
		}
		actions = append(actions, action)
	}
	plan, err := domain.NewPlan(id, 1, actions)
	if err != nil {
		return RoutineEquipResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutineEquipResult{}, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoutineEquipResult{}, ErrControl
	}
	if _, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, method, plan); err != nil {
		return RoutineEquipResult{}, err
	}
	return RoutineEquipResult{Reason: BuildingMethodAdmitted, Plan: id}, nil
}

func equipCandidatePawnFacts(row *n.PawnState) policy.EquipCandidatePawn {
	facts := policy.EquipCandidatePawn{Pawn: domain.PawnID(row.Pawn.GetId()), Dead: boundary.FactBool(row.Dead), Downed: boundary.FactBool(row.Downed), Drafted: boundary.FactBool(row.Drafted), MentalState: boundary.FactPresence(row.MentalState, row.Issues, "mental_state")}
	facts.Profile = policy.BuildProfile(observation.WorkPawnRow(row))
	facts.Position = domain.Cell{X: row.Pawn.GetPosition().GetX(), Z: row.Pawn.GetPosition().GetZ()}
	if biography := row.Biography; biography != nil && !boundary.IssueField(biography.Issues, "disabled_work_tags") {
		capable := true
		for _, tag := range biography.DisabledWorkTags {
			if tag == "Shooting" {
				facts.ShootingDisabled = true
			}
			if tag == "Violent" {
				capable = false
			}
		}
		facts.IncapableOfViolence = domain.Known(!capable)
	}
	if equipment := row.Equipment; equipment != nil && equipment.Armed != nil && !boundary.IssueField(equipment.Issues, "armed") {
		facts.Armed = domain.Known(equipment.GetArmed())
	}
	return facts
}
