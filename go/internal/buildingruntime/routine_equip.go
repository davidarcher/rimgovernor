package buildingruntime

import (
	"context"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
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
	Verdict
	Plan domain.PlanID
}

func NewRoutineEquipPlanner(reviewer *RoutineReviewer, native RoutineEquipSource) (*RoutineEquipPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, fmt.Errorf("%w: NewRoutineEquipPlanner: reviewer == nil || native == nil", ErrControl)
	}
	return &RoutineEquipPlanner{reviewer: reviewer, native: native}, nil
}

// nextEquipWaveMethod names the next wave after every wave this epoch bound,
// settled and retired ones included: goal.Methods lists only the unretired
// plans, so counting it reused "equip-wave-0" once the first wave retired and
// the goal_methods key refused every later wave (#1674).
func nextEquipWaveMethod(goal store.GoalState) domain.MethodID {
	bound := map[domain.MethodID]bool{}
	for _, m := range goal.History {
		bound[m.Method] = true
	}
	for _, m := range goal.Methods {
		bound[m.Method] = true
	}
	for i := 0; ; i++ {
		if method := domain.MethodID(fmt.Sprintf("equip-wave-%d", i)); !bound[method] {
			return method
		}
	}
}

func (r *RoutineEquipPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoutineEquipResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutineEquipResult{Verdict: BuildingReasonDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoutineEquipResult{}, fmt.Errorf("%w: step: !state.ObservationKnown || state.Snapshot.Validate() != nil", ErrControl)
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutineEquipResult{}, err
	}
	if !review.Enabled || review.Snapshot != state.Snapshot {
		return RoutineEquipResult{Verdict: BuildingReasonNoReview}, nil
	}
	goal, workable, err := p.journal.Workable(call, review, policy.EnsureBasicDefense)
	if err != nil {
		return RoutineEquipResult{}, err
	}
	if !workable {
		return RoutineEquipResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	attemptsByPawn := map[domain.PawnID]int{}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoutineEquipResult{}, err
		}
		if store.PlanOpen(plan) {
			return RoutineEquipResult{Verdict: BuildingReasonExistingWork}, nil
		}
		for _, progress := range plan.Progress {
			if equip, ok := progress.Action().Equip(); ok {
				if method.Epoch == goal.Goal.Epoch {
					attemptsByPawn[equip.Pawn()]++
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
		return RoutineEquipResult{}, fmt.Errorf("%w: step: err != nil || emergency.Context.GetTick() < int64(review.Tick)", ErrControl)
	}
	complete, known := emergency.Facts.ColonistsComplete.Value()
	if !known || !complete || len(emergency.Facts.Colonists) == 0 {
		return RoutineEquipResult{Verdict: BuildingReasonUsed}, nil
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
		return RoutineEquipResult{}, fmt.Errorf("%w: step: observed == nil", ErrControl)
	}
	if _, err = boundary.Context(observed.Context, state.Snapshot); err != nil {
		return RoutineEquipResult{}, fmt.Errorf("%w: step: err != nil", ErrControl)
	}
	if len(observed.Pawns) != len(ids) {
		return RoutineEquipResult{}, fmt.Errorf("%w: step: len(observed.Pawns) != len(ids)", ErrControl)
	}
	things, err := frameThings(call, r.native, identity)
	if err != nil {
		return RoutineEquipResult{}, err
	}
	var pawns []policy.EquipCandidatePawn
	seen := map[string]bool{}
	for _, row := range observed.Pawns {
		if row == nil || row.Pawn == nil || seen[row.Pawn.GetId()] {
			return RoutineEquipResult{}, fmt.Errorf("%w: step: row == nil || row.Pawn == nil || seen[row.Pawn.GetId()]", ErrControl)
		}
		seen[row.Pawn.GetId()] = true
		facts := equipCandidatePawnFacts(row)
		if current := row.GetEquipment().GetPrimaryId(); current != "" {
			for _, item := range row.GetEquipment().GetEquipped() {
				if item.GetThing().GetId() == current {
					facts.Current = &policy.EquipCandidateWeapon{Thing: current, Definition: gearDef(things, item.GetThing()), Class: policy.ClassifyWeapon(true, item.GetRanged(), item.GetMelee()), BiocodedTo: domain.PawnID(item.GetBiocodedTo()), Biocoded: item.GetBiocoded()}
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
		return RoutineEquipResult{}, fmt.Errorf("%w: step: err != nil || bounds.Bounds.Width <= 0 || bounds.Bounds.Height <= 0", ErrControl)
	}
	weapons, _, err := r.native.ReadEquipWeapons(call, identity, domain.Cell{X: 0, Z: 0}, domain.Cell{X: bounds.Bounds.Width - 1, Z: bounds.Bounds.Height - 1})
	if err != nil {
		return RoutineEquipResult{}, err
	}
	if _, err = boundary.Context(weapons.Context, state.Snapshot); err != nil {
		return RoutineEquipResult{}, fmt.Errorf("%w: step: err != nil", ErrControl)
	}
	var candidates []policy.EquipCandidateWeapon
	for _, w := range weapons.Targets {
		candidates = append(candidates, policy.EquipCandidateWeapon{Thing: w.Thing, Definition: w.Definition, Cell: w.Cell, Class: policy.ClassifyWeapon(w.ByTrade, w.Ranged, w.Melee), BiocodedTo: w.BiocodedTo, Biocoded: w.Biocoded})
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
			return RoutineEquipResult{Verdict: BuildingReasonExhausted}, nil
		}
		// No loose weapon fits an unarmed fighter: the armory crafts one
		// (#1204).
		return RoutineEquipResult{Verdict: BuildingReasonUsed}, nil
	}
	// A single plan contains independent equip actions: no pawn waits for a
	// preceding pawn's native postcondition before its order can dispatch.
	method := nextEquipWaveMethod(goal)
	id := domain.MintPlanID()
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
		return RoutineEquipResult{}, fmt.Errorf("%w: step: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge", ErrControl)
	}
	if _, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, method, plan); err != nil {
		return RoutineEquipResult{}, err
	}
	return RoutineEquipResult{Verdict: BuildingReasonAdmitted, Plan: id}, nil
}
