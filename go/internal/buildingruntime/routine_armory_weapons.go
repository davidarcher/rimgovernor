package buildingruntime

import (
	"context"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// craftWeapons admits one demand-sized weapon bill under MaintainEquipment
// for the colonists no loose weapon arms (#1203). It moved here from the
// gear planner unchanged: the gear planner still wears and replaces, and a
// pending wear candidate or any open bill holds the armory back.
func (r *RoutineArmoryPlanner) craftWeapons(call, epoch context.Context, arbiter *stepArbiter, state ControlState, review store.RoutineReview, tier policy.ArmoryTier, holds []policy.Amount) (RoutineArmoryResult, error) {
	p := r.reviewer.player
	goal, workable, err := p.journal.Workable(call, review, policy.MaintainEquipment)
	if err != nil {
		return RoutineArmoryResult{}, err
	}
	if !workable {
		return RoutineArmoryResult{Reason: BuildingMethodNoDeficit}, nil
	}
	if _, refused, err := equipmentSlots(call, p, review); err != nil || refused != "" {
		return RoutineArmoryResult{Reason: refused}, err
	}
	claimed := map[string]bool{}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoutineArmoryResult{}, err
		}
		if !store.PlanOpen(plan) {
			continue
		}
		for _, action := range plan.Spec.Actions() {
			if wear, ok := action.GearReplace(); ok {
				claimed[wear.Thing()] = true
			} else if _, ok := action.ApparelPolicy(); !ok {
				return RoutineArmoryResult{Reason: BuildingMethodExistingWork}, nil
			}
		}
	}
	started := r.reviewer.clock.Now()
	identity := boundary.Identity(state.Snapshot)
	reply, _, err := r.reviewer.colonyFacts(call, r.native, identity, true)
	if err != nil {
		return RoutineArmoryResult{}, err
	}
	observed := reply.GetObserved()
	if observed == nil {
		return RoutineArmoryResult{}, fmt.Errorf("%w: craftWeapons: observed == nil", ErrControl)
	}
	if _, err = boundary.Context(observed.Context, state.Snapshot); err != nil || observed.Context.GetTick() < int64(review.Tick) {
		return RoutineArmoryResult{}, fmt.Errorf("%w: craftWeapons: err != nil || observed.Context.GetTick() < int64(review.Tick)", ErrControl)
	}
	gear := observed.GetPlanning().GetObserved().GetGear()
	if gear == nil || observed.ColonistCount == nil || uint32(len(gear.GetPawns())) != observed.GetColonistCount() {
		return RoutineArmoryResult{Reason: BuildingMethodUsed}, nil
	}
	observation := gearObservationFacts(gear)
	for i := range observation.Pawns {
		candidates, _ := observation.Pawns[i].Candidates.Value()
		available := []policy.GearCandidate{}
		for _, c := range candidates {
			if !claimed[c.Target] {
				available = append(available, c)
			}
		}
		if len(available) > 0 {
			// Gear wears an existing item before anything is crafted.
			return RoutineArmoryResult{Reason: BuildingMethodUsed}, nil
		}
		observation.Pawns[i].Candidates = domain.Known(available)
	}
	census, _, err := r.native.ReadGearBenches(call, identity)
	if err != nil {
		return RoutineArmoryResult{}, err
	}
	benches := make([]policy.GearBench, 0, len(census))
	tokens := map[string]string{}
	for _, row := range census {
		benches = append(benches, row.Bench)
		tokens[row.Bench.ID] = row.Token
	}
	var stock []policy.Stock
	if names := recipeIngredientNames(census, ""); len(names) > 0 {
		if stock, _, err = r.native.ReadSupplyStock(call, identity, names); err != nil {
			return RoutineArmoryResult{}, err
		}
	}
	weapons, unarmed, err := r.weaponDemand(call, state, gear, benches, tier)
	if err != nil {
		return RoutineArmoryResult{}, err
	}
	if unarmed > 0 && !weaponBenchHosted(benches) {
		return r.placeCraftingSpot(call, epoch, arbiter)
	}
	seen := make([]domain.MethodID, 0, len(goal.Methods))
	for _, method := range goal.Methods {
		seen = append(seen, method.Method)
	}
	// MaintainResource holds (#1230) bind upgrades and armor, never arming
	// the unarmed: an early wood floor would otherwise leave a tribal start
	// without clubs.
	request := policy.GearPlanningRequest{Observation: domain.Known(observation), Seen: seen, Benches: domain.Known(benches), Stock: stock}
	if unarmed == 0 {
		request.Holds = holds
	}
	choice, err := policy.SelectArmoryMethod(request, weapons)
	request.Holds = holds
	if err != nil {
		return RoutineArmoryResult{}, err
	}
	// Armed colonists next get the armor ladder the tier allows (#1205).
	if choice.Kind != policy.GearProduce {
		if choice, err = policy.SelectArmoryArmorMethod(request, tier); err != nil {
			return RoutineArmoryResult{}, err
		}
	}
	if choice.Kind != policy.GearProduce {
		return RoutineArmoryResult{Reason: BuildingMethodUsed}, nil
	}
	if _, ok := tokens[choice.Bench]; !ok {
		return RoutineArmoryResult{}, fmt.Errorf("%w: craftWeapons: unknown bench %q", ErrControl, choice.Bench)
	}
	ingredients := make([]string, len(choice.Filter))
	for i, resource := range choice.Filter {
		ingredients[i] = string(resource)
	}
	id := domain.MintPlanID()
	bill, err := domain.NewProductionBill(choice.Bench, choice.Recipe, domain.GearBatch, choice.Count, ingredients...)
	if err != nil {
		return RoutineArmoryResult{}, err
	}
	action, err := domain.NewProductionBillAction(domain.ActionID(fmt.Sprintf("%s-0", id)), bill)
	if err != nil {
		return RoutineArmoryResult{}, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoutineArmoryResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutineArmoryResult{}, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoutineArmoryResult{}, fmt.Errorf("%w: craftWeapons: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge", ErrControl)
	}
	if _, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, choice.ID, plan); err != nil {
		return RoutineArmoryResult{}, err
	}
	return RoutineArmoryResult{Reason: BuildingMethodAdmitted, Plan: id}, nil
}

func (r *RoutineArmoryPlanner) weaponDemand(ctx context.Context, state ControlState, gear *o.GearSnapshot, benches []policy.GearBench, tier policy.ArmoryTier) ([]policy.Amount, int, error) {
	source, ok := r.native.(RoutineEquipSource)
	if !ok {
		return nil, 0, nil
	}
	identity := boundary.Identity(state.Snapshot)
	ids := []string{}
	for _, p := range gear.Pawns {
		ids = append(ids, p.GetPawn().GetId())
	}
	reply, _, err := source.ReadCombatPawns(ctx, identity, ids)
	if err != nil {
		return nil, 0, err
	}
	observed := reply.GetObserved()
	if observed == nil {
		return nil, 0, fmt.Errorf("%w: weaponDemand: observed == nil", ErrControl)
	}
	if _, err = boundary.Context(observed.Context, state.Snapshot); err != nil || observed.Context.GetTick() < gear.Context.GetTick() {
		return nil, 0, fmt.Errorf("%w: weaponDemand: err != nil || observed.Context.GetTick() < gear.Context.GetTick()", ErrControl)
	}
	// An exact-ID census counts every other pawn on the map as filtered, so
	// only the requested rows establish completeness: matched, returned and
	// the decoded rows must each cover the whole request, the same contract
	// RoutineEquipPlanner reads the identical census under. Demanding
	// filtered == 0 here refused every real colony (a single animal or
	// visitor is enough) and failed the whole gear step with ErrControl, so
	// MaintainEquipment never planned a wear or bill method past its apparel
	// policies and never recovered (#660).
	if len(observed.Pawns) != len(ids) {
		return nil, 0, fmt.Errorf("%w: weaponDemand: len(observed.Pawns) != len(ids)", ErrControl)
	}
	pawns := []policy.EquipCandidatePawn{}
	primaries := map[domain.PawnID]policy.ArmoryPrimary{}
	for _, p := range observed.Pawns {
		pawns = append(pawns, equipCandidatePawnFacts(p))
		if primary, ok := armoryPrimary(p); ok {
			primaries[domain.PawnID(p.Pawn.GetId())] = primary
		}
	}
	bounds, _, err := source.ReadMapBounds(ctx, identity, domain.Cell{})
	if err != nil {
		return nil, 0, err
	}
	if _, err = boundary.Context(bounds.Context, state.Snapshot); err != nil || bounds.Bounds.Width <= 0 || bounds.Bounds.Height <= 0 {
		return nil, 0, fmt.Errorf("%w: weaponDemand: err != nil || bounds.Bounds.Width <= 0 || bounds.Bounds.Height <= 0", ErrControl)
	}
	weapons, _, err := source.ReadEquipWeapons(ctx, identity, domain.Cell{}, domain.Cell{X: bounds.Bounds.Width - 1, Z: bounds.Bounds.Height - 1})
	if err != nil {
		return nil, 0, err
	}
	if _, err = boundary.Context(weapons.Context, state.Snapshot); err != nil {
		return nil, 0, fmt.Errorf("%w: weaponDemand: err != nil", ErrControl)
	}
	candidates := []policy.EquipCandidateWeapon{}
	for _, w := range weapons.Targets {
		candidates = append(candidates, policy.EquipCandidateWeapon{Thing: w.Thing, Definition: w.Definition, Cell: w.Cell, Class: policy.ClassifyWeapon(w.ByTrade, w.Ranged, w.Melee), BiocodedTo: w.BiocodedTo, Biocoded: w.Biocoded})
	}
	recipes := []policy.GearRecipe{}
	for _, b := range benches {
		rows, known := b.Recipes.Value()
		if !known {
			return nil, 0, fmt.Errorf("%w: weaponDemand: !known", ErrControl)
		}
		recipes = append(recipes, rows...)
	}
	return policy.ArmoryWeaponDemand(tier, pawns, primaries, candidates, recipes), policy.UnarmedFighters(pawns, candidates), nil
}

var armoryQualityRank = map[string]int{"Awful": 0, "Poor": 1, "Normal": 2, "Good": 3, "Excellent": 4, "Masterwork": 5, "Legendary": 6}

// armoryPrimary is the pawn's equipped primary weapon; an unobserved
// quality reads as normal.
func armoryPrimary(row *o.PawnState) (policy.ArmoryPrimary, bool) {
	equipment := row.GetEquipment()
	id := equipment.GetPrimaryId()
	if id == "" {
		return policy.ArmoryPrimary{}, false
	}
	for _, item := range equipment.GetEquipped() {
		if item.GetThing().GetId() != id || item.GetThing().GetDefName() == "" {
			continue
		}
		quality, ok := armoryQualityRank[item.GetQuality()]
		if !ok {
			quality = 2
		}
		return policy.ArmoryPrimary{Definition: item.GetThing().GetDefName(), Ranged: item.GetRanged(), Quality: quality}, true
	}
	return policy.ArmoryPrimary{}, false
}

// weaponBenchHosted reports whether any bench hosts a modelled weapon recipe.
func weaponBenchHosted(benches []policy.GearBench) bool {
	for _, b := range benches {
		rows, _ := b.Recipes.Value()
		for _, recipe := range rows {
			if policy.WeaponRecipe(recipe) {
				return true
			}
		}
	}
	return false
}

// placeCraftingSpot places a crafting spot for colonists nothing can arm;
// the weapon bill follows on a later step once it stands.
func (r *RoutineArmoryPlanner) placeCraftingSpot(call, epoch context.Context, arbiter *stepArbiter) (RoutineArmoryResult, error) {
	if r.spot == nil {
		return RoutineArmoryResult{Reason: BuildingNoWeaponBench}, nil
	}
	result, err := r.spot.step(call, epoch, arbiter)
	if err != nil {
		return RoutineArmoryResult{}, err
	}
	if result.Decision.Admitted {
		return RoutineArmoryResult{Reason: BuildingMethodAdmitted}, nil
	}
	return RoutineArmoryResult{Reason: result.Reason}, nil
}
