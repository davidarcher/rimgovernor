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

// RoundsEquipSource reuses the generic combat pawn read (armed/violence
// facts) plus the new bridge.ReadEquipWeapons for loose-weapon discovery.
// Skills, traits and positions come from the same combat pawn observation.
type RoundsEquipSource interface {
	ReadEmergency(context.Context, *c.Identity) (bridge.EmergencyObservation, bridge.Result, error)
	ReadCombatPawns(context.Context, *c.Identity, []string) (*n.ListPawnsReply, bridge.Result, error)
	ReadMapBounds(context.Context, *c.Identity, domain.Cell) (bridge.MapBounds, bridge.Result, error)
	ReadEquipWeapons(context.Context, *c.Identity, domain.Cell, domain.Cell) (bridge.EquipRead, bridge.Result, error)
	// DefinitionCatalog is the def rows every weapon is scored from.
	DefinitionCatalog(context.Context, *c.Identity) (*bridge.DefinitionCatalog, error)
}

// equipCandidateWeapon is a loose weapon with its def rows: a def
// the catalog cannot state is an error, never a default weapon.
func equipCandidateWeapon(catalog *bridge.DefinitionCatalog, w bridge.EquipCandidate) (policy.EquipCandidateWeapon, error) {
	facts, err := catalog.WeaponOf(w.Definition)
	if err != nil {
		return policy.EquipCandidateWeapon{}, err
	}
	return policy.EquipCandidateWeapon{Thing: w.Thing, Definition: w.Definition, Cell: w.Cell, Class: policy.ClassifyWeapon(facts.ByTrade, facts.Ranged, facts.Melee), BiocodedTo: w.BiocodedTo, Biocoded: w.Biocoded, Facts: facts}, nil
}

type RoundsEquipPlanner struct {
	reviewer *Rounder
	native   RoundsEquipSource
}
type RoundsEquipResult struct {
	Verdict
	Plan domain.PlanID
}

func NewRoundsEquipPlanner(reviewer *Rounder, native RoundsEquipSource) (*RoundsEquipPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, fmt.Errorf("%w: NewRoundsEquipPlanner: reviewer == nil || native == nil", ErrControl)
	}
	return &RoundsEquipPlanner{reviewer: reviewer, native: native}, nil
}

// nextEquipWaveMethod names the next wave after every wave this epoch bound,
// settled and retired ones included: goal.Methods lists only the unretired
// plans, so counting it reused "equip-wave-0" once the first wave retired and
// the goal_methods key refused every later wave.
func nextEquipWaveMethod(goal store.StandardState) domain.MethodID {
	return nextWaveMethod(goal, "equip-wave-")
}

func (r *RoundsEquipPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoundsEquipResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoundsEquipResult{Verdict: BuildingReasonDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoundsEquipResult{}, fmt.Errorf("%w: step: !state.ObservationKnown || state.Snapshot.Validate() != nil", ErrControl)
	}
	review, err := p.journal.LoadRounds(call)
	if err != nil {
		return RoundsEquipResult{}, err
	}
	if !review.Enabled || review.Snapshot != state.Snapshot {
		return RoundsEquipResult{Verdict: BuildingReasonNoReview}, nil
	}
	goal, workable, err := p.journal.Workable(call, review, policy.EnsureBasicDefense)
	if err != nil {
		return RoundsEquipResult{}, err
	}
	if !workable {
		return RoundsEquipResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoundsEquipResult{}, err
		}
		if store.PlanOpen(plan) {
			return RoundsEquipResult{Verdict: BuildingReasonExistingWork}, nil
		}
	}
	// Native's refusals of equip orders, by pawn, over every wave this
	// episode bound (retired ones included).
	var wavePlans []domain.PlanID
	for _, method := range goal.History {
		if method.Episode == goal.Standard.Episode {
			wavePlans = append(wavePlans, method.Plan)
		}
	}
	refusals, err := refusalLedger(call, p.journal, wavePlans, func(progress domain.Progress) string {
		if equip, ok := progress.Action().Equip(); ok {
			return "equip-" + string(equip.Pawn())
		}
		return ""
	})
	if err != nil {
		return RoundsEquipResult{}, err
	}
	started := r.reviewer.clock.Now()
	identity := boundary.Identity(state.Snapshot)
	emergency, _, err := r.native.ReadEmergency(call, identity)
	if err != nil {
		return RoundsEquipResult{}, err
	}
	if _, err = boundary.Context(emergency.Context, state.Snapshot); err != nil || emergency.Context.GetTick() < int64(review.Tick) {
		return RoundsEquipResult{}, fmt.Errorf("%w: step: err != nil || emergency.Context.GetTick() < int64(review.Tick)", ErrControl)
	}
	complete, known := emergency.Facts.ColonistsComplete.Value()
	if !known || !complete || len(emergency.Facts.Colonists) == 0 {
		return RoundsEquipResult{Verdict: waitFor(policy.CauseMethodUsed, "colonist_census_incomplete")}, nil
	}
	ids := make([]string, 0, len(emergency.Facts.Colonists))
	for _, pawn := range emergency.Facts.Colonists {
		ids = append(ids, string(pawn.ID))
	}
	reply, _, err := r.native.ReadCombatPawns(call, identity, ids)
	if err != nil {
		return RoundsEquipResult{}, err
	}
	observed := reply.GetObserved()
	if observed == nil {
		return RoundsEquipResult{}, fmt.Errorf("%w: step: observed == nil", ErrControl)
	}
	if _, err = boundary.Context(observed.Context, state.Snapshot); err != nil {
		return RoundsEquipResult{}, fmt.Errorf("%w: step: err != nil", ErrControl)
	}
	if len(observed.Pawns) != len(ids) {
		return RoundsEquipResult{}, fmt.Errorf("%w: step: len(observed.Pawns) != len(ids)", ErrControl)
	}
	things, err := frameThings(call, r.native, identity)
	if err != nil {
		return RoundsEquipResult{}, err
	}
	catalog, err := r.native.DefinitionCatalog(call, identity)
	if err != nil {
		return RoundsEquipResult{}, err
	}
	var pawns []policy.EquipCandidatePawn
	seen := map[string]bool{}
	for _, row := range observed.Pawns {
		if row == nil || row.Pawn == nil || seen[row.Pawn.GetId()] {
			return RoundsEquipResult{}, fmt.Errorf("%w: step: row == nil || row.Pawn == nil || seen[row.Pawn.GetId()]", ErrControl)
		}
		seen[row.Pawn.GetId()] = true
		facts, err := equipCandidatePawnFacts(row, catalog, things)
		if err != nil {
			return RoundsEquipResult{}, err
		}
		if current := row.GetEquipment().GetPrimaryId(); current != "" {
			for _, item := range row.GetEquipment().GetEquipped() {
				if item.GetThing().GetId() == current {
					def := gearDef(things, item.GetThing())
					rows, err := catalog.WeaponOf(def)
					if err != nil {
						return RoundsEquipResult{}, err
					}
					facts.Current = &policy.EquipCandidateWeapon{Thing: current, Definition: def, Class: policy.ClassifyWeapon(true, rows.Ranged, rows.Melee), BiocodedTo: domain.PawnID(item.GetBiocodedTo()), Biocoded: item.GetBiocoded(), Facts: rows}
				}
			}
		}
		pawns = append(pawns, facts)
	}
	bounds, _, err := r.native.ReadMapBounds(call, identity, domain.Cell{X: 0, Z: 0})
	if err != nil {
		return RoundsEquipResult{}, err
	}
	if _, err = boundary.Context(bounds.Context, state.Snapshot); err != nil || bounds.Bounds.Width <= 0 || bounds.Bounds.Height <= 0 {
		return RoundsEquipResult{}, fmt.Errorf("%w: step: err != nil || bounds.Bounds.Width <= 0 || bounds.Bounds.Height <= 0", ErrControl)
	}
	weapons, _, err := r.native.ReadEquipWeapons(call, identity, domain.Cell{X: 0, Z: 0}, domain.Cell{X: bounds.Bounds.Width - 1, Z: bounds.Bounds.Height - 1})
	if err != nil {
		return RoundsEquipResult{}, err
	}
	if _, err = boundary.Context(weapons.Context, state.Snapshot); err != nil {
		return RoundsEquipResult{}, fmt.Errorf("%w: step: err != nil", ErrControl)
	}
	var candidates []policy.EquipCandidateWeapon
	for _, w := range weapons.Targets {
		candidate, err := equipCandidateWeapon(catalog, w)
		if err != nil {
			return RoundsEquipResult{}, err
		}
		candidates = append(candidates, candidate)
	}
	// Exclude unavailable pawns before matching so they cannot consume a weapon
	// another colonist could use. The arbiter lock spans selection and claims.
	arbiter.mu.Lock()
	pool := make([]policy.EquipCandidatePawn, 0, len(pawns))
	var refused Verdict
	var held []domain.PawnID
	for _, pawn := range pawns {
		if pawn.NoArms != "" {
			held = append(held, pawn.Pawn)
			continue
		}
		if verdict, ok := budgetVerdict(refusals, "equip-"+string(pawn.Pawn), state.Snapshot); !ok {
			if refused.IsZero() {
				refused = verdict
			}
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
		if !refused.IsZero() {
			return RoundsEquipResult{Verdict: refused}, nil
		}
		// Every colonist left is held back from arms (a creepjoiner whose
		// downside has not shown): no weapon is owed until it does.
		if len(held) > 0 && len(pool) == 0 {
			return RoundsEquipResult{Verdict: refuse(policy.CauseNoWorker, "creepjoiner_downside_unrevealed", string(held[0]))}, nil
		}
		// No loose weapon fits an unarmed fighter: the armory crafts one.
		return RoundsEquipResult{Verdict: waitFor(policy.CauseMethodUsed, "armory_crafts_weapon")}, nil
	}
	// A single plan contains independent equip actions: no pawn waits for a
	// preceding pawn's native postcondition before its order can dispatch.
	method := nextEquipWaveMethod(goal)
	id := domain.MintPlanID()
	actions := make([]domain.Action, 0, len(assignments))
	for i, pair := range assignments {
		equip, err := domain.NewEquip(pair.Pawn, pair.Weapon.Thing, pair.Weapon.Definition, pair.Weapon.Cell)
		if err != nil {
			return RoundsEquipResult{}, err
		}
		action, err := domain.NewEquipAction(domain.ActionID(fmt.Sprintf("%s-%d", id, i)), equip)
		if err != nil {
			return RoundsEquipResult{}, err
		}
		actions = append(actions, action)
	}
	plan, err := domain.NewPlan(id, 1, actions)
	if err != nil {
		return RoundsEquipResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoundsEquipResult{}, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoundsEquipResult{}, fmt.Errorf("%w: step: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge", ErrControl)
	}
	if _, err = p.journal.CommitMethod(call, goal.Standard.ID, goal.Revision, method, plan); err != nil {
		return RoundsEquipResult{}, err
	}
	return RoundsEquipResult{Verdict: BuildingReasonAdmitted, Plan: id}, nil
}
