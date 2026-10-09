package buildingruntime

import (
	"context"
	"fmt"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// craftWeapons admits one demand-sized weapon bill under MaintainEquipment
// for the colonists no loose weapon arms. It moved here from the
// gear planner unchanged: the gear planner still wears and replaces, and a
// pending wear candidate or any open bill holds the armory back.
//
// A hunter lacking a hunting weapon is food's need: while EnsureFoodSupply is
// workable (open, unmet, admitted by the Safeguards) its weapon bill is that
// Standard's Method, so a starving colony still crafts the bow its hunt waits
// on. Without that open need the same weapons are ordinary demand.
func (r *RoundsArmoryPlanner) craftWeapons(call, epoch context.Context, arbiter *stepArbiter, state ControlState, review store.Rounds, tier policy.ArmoryTier, stuffCategories map[policy.Resource][]string) (RoundsArmoryResult, error) {
	p := r.reviewer.player
	goal, workable, err := p.journal.Workable(call, review, policy.MaintainEquipment)
	if err != nil {
		return RoundsArmoryResult{}, err
	}

	food, foodWorkable, err := p.journal.Workable(call, review, policy.EnsureFoodSupply)
	if err != nil {
		return RoundsArmoryResult{}, err
	}
	if !workable && !foodWorkable {
		return RoundsArmoryResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	claimed := map[string]bool{}
	for _, method := range goal.Methods {
		if !workable {
			break
		}
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoundsArmoryResult{}, err
		}
		if !store.PlanOpen(plan) {
			continue
		}
		for _, action := range plan.Spec.Actions() {
			if wear, ok := action.GearReplace(); ok {
				claimed[wear.Thing()] = true
			} else if _, ok := action.ApparelPolicy(); !ok {
				return RoundsArmoryResult{Verdict: BuildingReasonExistingWork}, nil
			}
		}
	}
	if workable {
		// A bill whose need is gone (the owner stayed Met) is removed first.
		if plan, err := r.reviewer.removeStaleBill(call, epoch, arbiter, state, goal, policy.MaintainEquipment); err != nil || plan != "" {
			return RoundsArmoryResult{Verdict: BuildingReasonAdmitted, Plan: plan}, err
		}
	}
	started := r.reviewer.clock.Now()
	identity := boundary.Identity(state.Snapshot)
	reply, _, err := r.reviewer.colonyFacts(call, r.native, identity, true)
	if err != nil {
		return RoundsArmoryResult{}, err
	}
	observed := reply.GetObserved()
	if observed == nil {
		return RoundsArmoryResult{}, fmt.Errorf("%w: craftWeapons: observed == nil", ErrControl)
	}
	if _, err = boundary.Context(observed.Context, state.Snapshot); err != nil || observed.Context.GetTick() < int64(review.Tick) {
		return RoundsArmoryResult{}, fmt.Errorf("%w: craftWeapons: err != nil || observed.Context.GetTick() < int64(review.Tick)", ErrControl)
	}
	gear := observed.GetPlanning().GetObserved().GetGear()
	if gear == nil || observed.ColonistCount == nil || uint32(len(gear.GetPawns())) != observed.GetColonistCount() {
		return RoundsArmoryResult{Verdict: waitFor(WaitMethodUsed, "gear_census_incomplete")}, nil
	}
	things, err := frameThings(call, r.native, identity)
	if err != nil {
		return RoundsArmoryResult{}, err
	}
	defs, err := gearDefinitions(call, r.native, identity)
	if err != nil {
		return RoundsArmoryResult{}, err
	}
	observation, known := gearObservationFacts(observed, bridge.Tables{Things: things}, defs)
	if !known {
		return RoundsArmoryResult{Verdict: fieldUnavailable("gear_census")}, nil
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
			return RoundsArmoryResult{Verdict: waitFor(WaitMethodUsed, "existing_gear_item")}, nil
		}
		observation.Pawns[i].Candidates = domain.Known(available)
	}
	census, _, err := r.native.ReadGearBenches(call, identity)
	if err != nil {
		return RoundsArmoryResult{}, err
	}
	benches := make([]policy.GearBench, 0, len(census))
	tokens := map[string]string{}
	for _, row := range census {
		benches = append(benches, row.Bench)
		tokens[row.Bench.ID] = row.Token
	}
	weapons, hunters, unarmed, err := r.weaponDemand(call, state, gear, benches, tier)
	if err != nil {
		return RoundsArmoryResult{}, err
	}
	// A weapon bill the demand no longer names goes whatever the owner's
	// finding: equipment's own, and the hunter weapons filed under
	// food, removed under the food Standard so that no food step starts.
	wantedWeapons := policy.WeaponsWanted(weapons, hunters)
	judge := func(b policy.StaleBill) (bool, bool) {
		return policy.WeaponBill(b.Products), policy.BillWanted(b.Products, wantedWeapons)
	}
	for _, owner := range []struct {
		goal    store.WorkOwner
		concern policy.ConcernID
		open    bool
	}{{goal, policy.MaintainEquipment, workable}, {food, policy.EnsureFoodSupply, foodWorkable}} {
		if !owner.open {
			continue
		}
		if plan, err := r.reviewer.removeUnwantedBill(call, epoch, arbiter, state, review, owner.goal, owner.concern, judge); err != nil || plan != "" {
			return RoundsArmoryResult{Verdict: BuildingReasonAdmitted, Plan: plan}, err
		}
	}
	if unarmed > 0 && !weaponBenchHosted(benches) {
		return r.placeCraftingSpot(call, epoch, arbiter)
	}
	// Food owns the hunters' weapons while its need stands.
	hunting := foodWorkable && len(hunters) > 0
	if hunting {
		goal, weapons = food, hunters
		for _, method := range goal.Methods {
			plan, err := p.journal.LoadPlan(call, method.Plan)
			if err != nil {
				return RoundsArmoryResult{}, err
			}
			if !store.PlanOpen(plan) {
				continue
			}
			for _, action := range plan.Spec.Actions() {
				if _, ok := action.ProductionBill(); ok {
					return RoundsArmoryResult{Verdict: BuildingReasonExistingWork}, nil
				}
			}
		}
	} else if weapons = mergeAmounts(weapons, hunters); !workable {
		return RoundsArmoryResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	seen := make([]domain.MethodID, 0, len(goal.Methods))
	for _, method := range goal.Methods {
		seen = append(seen, method.Method)
	}
	request := policy.GearPlanningRequest{Observation: domain.Known(observation), Seen: seen, Benches: domain.Known(benches), StuffCategories: stuffCategories}
	choice, err := policy.SelectArmoryMethod(request, weapons)
	if err != nil {
		return RoundsArmoryResult{}, err
	}
	// Armed colonists next get the armor ladder the tier allows.
	if choice.Kind != policy.GearProduce && !hunting {
		if choice, err = policy.SelectArmoryArmorMethod(request, tier); err != nil {
			return RoundsArmoryResult{}, err
		}
	}
	if choice.Kind != policy.GearProduce {
		return RoundsArmoryResult{Verdict: waitFor(WaitMethodUsed, "armory_craft_not_needed")}, nil
	}
	if _, ok := tokens[choice.Bench]; !ok {
		return RoundsArmoryResult{}, fmt.Errorf("%w: craftWeapons: unknown bench %q", ErrControl, choice.Bench)
	}
	ingredients := make([]string, len(choice.Filter))
	for i, resource := range choice.Filter {
		ingredients[i] = string(resource)
	}
	id := domain.MintPlanID()
	bill, err := domain.NewProductionBill(choice.Bench, choice.Recipe, domain.GearBatch, choice.Count, ingredients...)
	if err != nil {
		return RoundsArmoryResult{}, err
	}
	action, err := domain.NewProductionBillAction(domain.ActionID(fmt.Sprintf("%s-0", id)), bill)
	if err != nil {
		return RoundsArmoryResult{}, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoundsArmoryResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoundsArmoryResult{}, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoundsArmoryResult{}, fmt.Errorf("%w: craftWeapons: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge", ErrControl)
	}
	if _, err = p.journal.CommitMethod(call, goal.Standard.ID, goal.Revision, choice.ID, plan); err != nil {
		return RoundsArmoryResult{}, err
	}
	return RoundsArmoryResult{Verdict: BuildingReasonAdmitted, Plan: id}, nil
}

// weaponDemand is the armory's weapon bill target: the fighters' demand, the
// hunters' (a hunter lacking a hunting weapon) and the unarmed fighter count.
func (r *RoundsArmoryPlanner) weaponDemand(ctx context.Context, state ControlState, gear *o.GearSnapshot, benches []policy.GearBench, tier policy.ArmoryTier) (fighters, hunters []policy.Amount, unarmed int, err error) {
	source, ok := r.native.(RoundsEquipSource)
	if !ok {
		return nil, nil, 0, nil
	}
	identity := boundary.Identity(state.Snapshot)
	ids := []string{}
	things, err := frameThings(ctx, r.native, identity)
	if err != nil {
		return nil, nil, 0, err
	}
	for _, p := range gear.Pawns {
		ids = append(ids, p.GetPawn().GetId())
	}
	reply, _, err := source.ReadCombatPawns(ctx, identity, ids)
	if err != nil {
		return nil, nil, 0, err
	}
	observed := reply.GetObserved()
	if observed == nil {
		return nil, nil, 0, fmt.Errorf("%w: weaponDemand: observed == nil", ErrControl)
	}
	if _, err = boundary.Context(observed.Context, state.Snapshot); err != nil || observed.Context.GetTick() < gear.Context.GetTick() {
		return nil, nil, 0, fmt.Errorf("%w: weaponDemand: err != nil || observed.Context.GetTick() < gear.Context.GetTick()", ErrControl)
	}
	// An exact-ID census counts every other pawn on the map as filtered, so
	// only the requested rows establish completeness: matched, returned and
	// the decoded rows must each cover the whole request, the same contract
	// RoundsEquipPlanner reads the identical census under. Demanding
	// filtered == 0 here refused every real colony (a single animal or
	// visitor is enough) and failed the whole gear step with ErrControl, so
	// MaintainEquipment never planned a wear or bill method past its apparel
	// policies and never recovered.
	if len(observed.Pawns) != len(ids) {
		return nil, nil, 0, fmt.Errorf("%w: weaponDemand: len(observed.Pawns) != len(ids)", ErrControl)
	}
	pawns := []policy.EquipCandidatePawn{}
	primaries := map[domain.PawnID]policy.ArmoryPrimary{}
	catalog, err := source.DefinitionCatalog(ctx, identity)
	if err != nil {
		return nil, nil, 0, err
	}
	for _, p := range observed.Pawns {
		facts, err := equipCandidatePawnFacts(p, catalog, things)
		if err != nil {
			return nil, nil, 0, err
		}
		pawns = append(pawns, facts)
		primary, ok, err := armoryPrimary(p, things, catalog)
		if err != nil {
			return nil, nil, 0, err
		}
		if ok {
			primaries[domain.PawnID(p.Pawn.GetId())] = primary
		}
	}
	bounds, _, err := source.ReadMapBounds(ctx, identity, domain.Cell{})
	if err != nil {
		return nil, nil, 0, err
	}
	if _, err = boundary.Context(bounds.Context, state.Snapshot); err != nil || bounds.Bounds.Width <= 0 || bounds.Bounds.Height <= 0 {
		return nil, nil, 0, fmt.Errorf("%w: weaponDemand: err != nil || bounds.Bounds.Width <= 0 || bounds.Bounds.Height <= 0", ErrControl)
	}
	weapons, _, err := source.ReadEquipWeapons(ctx, identity, domain.Cell{}, domain.Cell{X: bounds.Bounds.Width - 1, Z: bounds.Bounds.Height - 1})
	if err != nil {
		return nil, nil, 0, err
	}
	if _, err = boundary.Context(weapons.Context, state.Snapshot); err != nil {
		return nil, nil, 0, fmt.Errorf("%w: weaponDemand: err != nil", ErrControl)
	}
	candidates := []policy.EquipCandidateWeapon{}
	for _, w := range weapons.Targets {
		candidate, err := equipCandidateWeapon(catalog, w)
		if err != nil {
			return nil, nil, 0, err
		}
		candidates = append(candidates, candidate)
	}
	recipes := []policy.GearRecipe{}
	// The def rows of each ladder weapon a recipe makes.
	products := map[policy.Resource]policy.WeaponDef{}
	for _, b := range benches {
		rows, known := b.Recipes.Value()
		if !known {
			return nil, nil, 0, fmt.Errorf("%w: weaponDemand: !known", ErrControl)
		}
		recipes = append(recipes, rows...)
		for _, recipe := range rows {
			for _, def := range recipe.Products {
				if _, tiered := policy.ArmoryWeaponTier(def); !tiered {
					continue
				}
				if products[def], err = catalog.WeaponOf(string(def)); err != nil {
					return nil, nil, 0, err
				}
			}
		}
	}
	fighters, hunters = policy.ArmoryWeaponDemand(tier, pawns, primaries, candidates, recipes, products)
	return fighters, hunters, policy.UnarmedFighters(pawns, candidates), nil
}

// mergeAmounts is the sum of two demands, sorted by resource.
func mergeAmounts(a, b []policy.Amount) []policy.Amount {
	if len(b) == 0 {
		return a
	}
	sum := map[policy.Resource]int64{}
	for _, d := range append(append([]policy.Amount(nil), a...), b...) {
		sum[d.Resource] += d.Count
	}
	out := make([]policy.Amount, 0, len(sum))
	for def, n := range sum {
		out = append(out, policy.Amount{Resource: def, Count: n})
	}
	sort.Slice(out, func(i, j int) bool { return out[i].Resource < out[j].Resource })
	return out
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
func (r *RoundsArmoryPlanner) placeCraftingSpot(call, epoch context.Context, arbiter *stepArbiter) (RoundsArmoryResult, error) {
	if r.spot == nil {
		return RoundsArmoryResult{Verdict: BuildingNoWeaponBench}, nil
	}
	result, err := r.spot.step(call, epoch, arbiter)
	if err != nil {
		return RoundsArmoryResult{}, err
	}
	if result.Decision.Admitted {
		return RoundsArmoryResult{Verdict: BuildingReasonAdmitted}, nil
	}
	return RoundsArmoryResult{Verdict: result.Verdict}, nil
}
