package buildingruntime

import (
	"context"
	"fmt"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// weaponDemand is the armory's weapon bill target: the fighters' demand, the
// hunters' (a hunter lacking a hunting weapon) and the unarmed fighter count.
func (r *RoundsArmoryPlanner) weaponDemand(ctx context.Context, snapshot domain.GenerationSnapshot, ids []string, minTick int64, benches []policy.GearBench, tier policy.ArmoryTier) (fighters, hunters []policy.Amount, unarmed int, err error) {
	source, ok := r.native.(RoundsEquipSource)
	if !ok {
		return nil, nil, 0, nil
	}
	identity := boundary.Identity(snapshot)
	things, err := frameThings(ctx, r.native, identity)
	if err != nil {
		return nil, nil, 0, err
	}
	reply, _, err := source.ReadCombatPawns(ctx, identity, ids)
	if err != nil {
		return nil, nil, 0, err
	}
	observed := reply.GetObserved()
	if observed == nil {
		return nil, nil, 0, fmt.Errorf("%w: weaponDemand: observed == nil", ErrControl)
	}
	if _, err = boundary.Context(observed.Context, snapshot); err != nil || observed.Context.GetTick() < minTick {
		return nil, nil, 0, fmt.Errorf("%w: weaponDemand: err != nil || observed.Context.GetTick() < minTick", ErrControl)
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
	if _, err = boundary.Context(bounds.Context, snapshot); err != nil || bounds.Bounds.Width <= 0 || bounds.Bounds.Height <= 0 {
		return nil, nil, 0, fmt.Errorf("%w: weaponDemand: err != nil || bounds.Bounds.Width <= 0 || bounds.Bounds.Height <= 0", ErrControl)
	}
	weapons, _, err := source.ReadEquipWeapons(ctx, identity, domain.Cell{}, domain.Cell{X: bounds.Bounds.Width - 1, Z: bounds.Bounds.Height - 1})
	if err != nil {
		return nil, nil, 0, err
	}
	if _, err = boundary.Context(weapons.Context, snapshot); err != nil {
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
			if !policy.WeaponRecipe(recipe) {
				continue
			}
			for _, def := range recipe.Products {
				if products[def], err = catalog.WeaponOf(string(def)); err != nil {
					return nil, nil, 0, err
				}
			}
		}
	}
	fighters, hunters = policy.ArmoryWeaponDemand(tier, pawns, primaries, candidates, recipes, products)
	r.reviewer.exports.setWeapons(snapshot, policy.SpareWeaponCounts(tier, pawns, primaries, candidates, recipes, products))
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
		if item.Quality != nil {
			quality = int(item.GetQuality())
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
