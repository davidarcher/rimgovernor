package buildingruntime

import (
	"context"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
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
		return RoutineArmoryResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	if !equipmentRanked(review) {
		return RoutineArmoryResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	if _, refused, err := equipmentSlots(call, p, review); err != nil || !refused.IsZero() {
		return RoutineArmoryResult{Verdict: refused}, err
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
				return RoutineArmoryResult{Verdict: BuildingReasonExistingWork}, nil
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
		return RoutineArmoryResult{Verdict: waitFor(WaitMethodUsed, "gear_census_incomplete")}, nil
	}
	things, err := frameThings(call, r.native, identity)
	if err != nil {
		return RoutineArmoryResult{}, err
	}
	defs, err := gearDefinitions(call, r.native, identity)
	if err != nil {
		return RoutineArmoryResult{}, err
	}
	observation, known := gearObservationFacts(observed, bridge.Tables{Things: things}, defs)
	if !known {
		return RoutineArmoryResult{Verdict: fieldUnavailable("gear_census")}, nil
	}
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
			return RoutineArmoryResult{Verdict: waitFor(WaitMethodUsed, "existing_gear_item")}, nil
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
		return RoutineArmoryResult{Verdict: waitFor(WaitMethodUsed, "armory_craft_not_needed")}, nil
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
	return RoutineArmoryResult{Verdict: BuildingReasonAdmitted, Plan: id}, nil
}

func (r *RoutineArmoryPlanner) weaponDemand(ctx context.Context, state ControlState, gear *o.GearSnapshot, benches []policy.GearBench, tier policy.ArmoryTier) ([]policy.Amount, int, error) {
	source, ok := r.native.(RoutineEquipSource)
	if !ok {
		return nil, 0, nil
	}
	identity := boundary.Identity(state.Snapshot)
	ids := []string{}
	things, err := frameThings(ctx, r.native, identity)
	if err != nil {
		return nil, 0, err
	}
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
	catalog, err := source.DefinitionCatalog(ctx, identity)
	if err != nil {
		return nil, 0, err
	}
	for _, p := range observed.Pawns {
		facts, err := equipCandidatePawnFacts(p, catalog, things)
		if err != nil {
			return nil, 0, err
		}
		pawns = append(pawns, facts)
		primary, ok, err := armoryPrimary(p, things, catalog)
		if err != nil {
			return nil, 0, err
		}
		if ok {
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
		candidate, err := equipCandidateWeapon(catalog, w)
		if err != nil {
			return nil, 0, err
		}
		candidates = append(candidates, candidate)
	}
	recipes := []policy.GearRecipe{}
	// The def rows of each ladder weapon a recipe makes (#1723).
	products := map[policy.Resource]policy.WeaponDef{}
	for _, b := range benches {
		rows, known := b.Recipes.Value()
		if !known {
			return nil, 0, fmt.Errorf("%w: weaponDemand: !known", ErrControl)
		}
		recipes = append(recipes, rows...)
		for _, recipe := range rows {
			for _, def := range recipe.Products {
				if _, tiered := policy.ArmoryWeaponTier(def); !tiered {
					continue
				}
				if products[def], err = catalog.WeaponOf(string(def)); err != nil {
					return nil, 0, err
				}
			}
		}
	}
	return policy.ArmoryWeaponDemand(tier, pawns, primaries, candidates, recipes, products), policy.UnarmedFighters(pawns, candidates), nil
}

// armoryPrimary is the pawn's equipped primary weapon; an unobserved
// quality reads as normal.
func armoryPrimary(row *o.PawnState, things bridge.Things, catalog *bridge.DefinitionCatalog) (policy.ArmoryPrimary, bool, error) {
	equipment := row.GetEquipment()
	id := equipment.GetPrimaryId()
	if id == "" {
		return policy.ArmoryPrimary{}, false, nil
	}
	for _, item := range equipment.GetEquipped() {
		def := gearDef(things, item.GetThing())
		if item.GetThing().GetId() != id || def == "" {
			continue
		}
		facts, err := catalog.WeaponOf(def)
		if err != nil {
			return policy.ArmoryPrimary{}, false, err
		}
		quality := 2 // QualityCategory.Normal
		if q := item.GetQuality(); q != o.Quality_QUALITY_UNSPECIFIED {
			quality = int(q) - 1
		}
		return policy.ArmoryPrimary{Definition: def, Ranged: facts.Ranged, Quality: quality, Facts: facts}, true, nil
	}
	return policy.ArmoryPrimary{}, false, nil
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
		return RoutineArmoryResult{Verdict: BuildingNoWeaponBench}, nil
	}
	result, err := r.spot.step(call, epoch, arbiter)
	if err != nil {
		return RoutineArmoryResult{}, err
	}
	if result.Decision.Admitted {
		return RoutineArmoryResult{Verdict: BuildingReasonAdmitted}, nil
	}
	return RoutineArmoryResult{Verdict: result.Verdict}, nil
}
