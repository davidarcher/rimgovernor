package policy

import (
	"fmt"
	"math"
	"slices"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The gear and armory planners declare the production orders they want
// standing to the work ledger (OrderDeclarer); the ledger places a missing
// order, keeps a matching bill and removes an undeclared one. A declaration is
// the whole wanted set of one Round, derived from the census and the bench
// readback alone: the one-at-a-time method history the bill planners used is
// gone, so an order is judged again every Round and a bill that already makes
// a need is declared as it stands, which keeps it.

// DeclareGearOrders is MaintainEquipment's wanted apparel orders: one
// demand-sized batch per definition and stuff the loadout model still lacks
// after netting stored stock. Armor and helmets are the armory's
// (DeclareArmoryOrders). A wearable item already observed is worn before
// anything is crafted, so a pending candidate declares no new order, only the
// bills that already stand. Abstain while the census or the bench readback is
// unread.
func DeclareGearOrders(r GearPlanningRequest) (Declared, error) {
	review, v, unread, err := gearOrderCensus(r)
	if err != nil || unread != "" {
		return abstainUnless(unread), err
	}
	needs := []gearNeed{}
	for _, p := range v.Pawns {
		replacements, ok := p.Replacements.Value()
		if !ok {
			return Abstaining(UnreadGearNeeds), nil
		}
		for _, n := range replacements {
			if !ArmoryArmor(n.Definition) {
				needs = append(needs, gearNeed{p, n})
			}
		}
	}
	orders, unknown, err := gearOrders(needs, v, review, r)
	return abstainUnless(unknown, orders...), err
}

// DeclareArmoryOrders is the armory's wanted orders at tier: the weapon batches
// (weapons is ArmoryWeaponDemand), the armor ladder the tier allows (each armor
// definition falls back down its family's rungs to the best one a bench
// recipe makes) and the stock-target mortar shells (shells is
// MortarShellTargets). Abstain while the tier, the census or the bench
// readback is unread.
func DeclareArmoryOrders(r GearPlanningRequest, tier ArmoryTier, weapons, shells []Amount) (Declared, error) {
	review, v, unread, err := gearOrderCensus(r)
	if err != nil || unread != "" {
		return abstainUnless(unread), err
	}
	benches, _ := r.Benches.Value()
	var out Declared
	collect := func(orders []OrderSpec, unread UnreadFact) {
		out.Orders = append(out.Orders, orders...)
		if unread != "" {
			out.Unread(unread)
		}
	}
	needs := []gearNeed{}
	for _, d := range weapons {
		if !validResource(d.Resource) || d.Count <= 0 || d.Count > 256 {
			return Declared{}, fmt.Errorf("invalid weapon demand %q x%d", d.Resource, d.Count)
		}
		for i := int64(0); i < d.Count; i++ {
			needs = append(needs, gearNeed{GearPawn{Pawn: "weapon-batch", Loadout: "colony"}, GearReplacement{Definition: d.Resource, Reason: "unarmed"}})
		}
	}
	orders, unknown, err := gearOrders(needs, v, review, r)
	if err != nil {
		return Declared{}, err
	}
	collect(orders, unknown)
	// Armor and shells follow the tier; weapons stand without it.
	if tier == ArmoryTierUnknown {
		out.Unread(UnreadArmoryTier)
		return out, nil
	}
	armor := map[Resource]int{}
	for _, p := range v.Pawns {
		replacements, ok := p.Replacements.Value()
		if !ok {
			return Abstaining(UnreadGearNeeds), nil
		}
		for _, n := range replacements {
			if ArmoryArmor(n.Definition) {
				armor[n.Definition]++
			}
		}
	}
	definitions := make([]Resource, 0, len(armor))
	for d := range armor {
		definitions = append(definitions, d)
	}
	sort.Slice(definitions, func(i, j int) bool { return definitions[i] < definitions[j] })
	for _, d := range definitions {
		rungs, _ := armoryArmorRungs(d, tier)
		for _, rung := range rungs {
			rungNeeds := make([]gearNeed, armor[d])
			for i := range rungNeeds {
				rungNeeds[i] = gearNeed{GearPawn{Pawn: "armor-batch", Loadout: "colony"}, GearReplacement{Definition: rung, Reason: "armory"}}
			}
			orders, unknown, err := gearOrders(rungNeeds, v, review, r)
			if err != nil {
				return Declared{}, err
			}
			collect(orders, unknown)
			if len(orders) > 0 || unknown != "" {
				break
			}
		}
	}
	stocked, unknown, err := shellOrders(benches, shells)
	if err != nil {
		return Declared{}, err
	}
	collect(stocked, unknown)
	return out, nil
}

// gearOrderCensus is the reviewed, loadout-modelled census a declaration reads;
// unread names the fact the review cannot establish (recovery or the bench
// readback), empty when both are read.
func gearOrderCensus(r GearPlanningRequest) (review GearReview, v GearObservation, unread UnreadFact, err error) {
	if review, err = ReviewGear(r.Observation); err != nil {
		return GearReview{}, GearObservation{}, "", err
	}
	if _, ok := review.Recovered.Value(); !ok {
		return GearReview{}, GearObservation{}, UnreadGearRecover, nil
	}
	if _, ok := r.Benches.Value(); !ok {
		return GearReview{}, GearObservation{}, UnreadBenches, nil
	}
	observed, _ := r.Observation.Value()
	return review, modeledGearObservation(observed, review.Loadouts), "", nil
}

// sortGearNeeds orders needs by pawn, definition, stuff and reason.
func sortGearNeeds(needs []gearNeed) {
	sort.SliceStable(needs, func(i, j int) bool {
		a, b := needs[i], needs[j]
		if a.pawn.Pawn != b.pawn.Pawn {
			return a.pawn.Pawn < b.pawn.Pawn
		}
		if a.need.Definition != b.need.Definition {
			return a.need.Definition < b.need.Definition
		}
		if a.need.Stuff != b.need.Stuff {
			return a.need.Stuff < b.need.Stuff
		}
		return a.need.Reason < b.need.Reason
	})
}

// gearOrders turns needs into the orders that make them: per definition and
// stuff, the bill that already makes it (declared as it stands) or the first
// funded recipe's batch sized to the demand left after stored stock. unknown
// names the fact left unread (a bench census, bills or a recipe's facts).
func gearOrders(needs []gearNeed, v GearObservation, review GearReview, r GearPlanningRequest) (orders []OrderSpec, unknown UnreadFact, err error) {
	if len(needs) == 0 {
		return nil, "", nil
	}
	sortGearNeeds(needs)
	benches, demand, known, err := gearProductionDemand(needs, v, review, r)
	if err != nil || !known {
		if !known {
			return nil, UnreadBenches, err
		}
		return nil, "", err
	}
	pending := false
	for _, p := range v.Pawns {
		candidates, _ := p.Candidates.Value()
		pending = pending || len(candidates) > 0
	}
	done := map[gearStockKey]bool{}
	for _, n := range needs {
		key := gearStockKey{n.need.Definition, n.need.Stuff}
		count := demand[key]
		if count == 0 || done[key] {
			continue
		}
		done[key] = true
		method, resolved, err := produceNeed(n, count, benches, r)
		if err != nil {
			return nil, "", err
		}
		if !resolved {
			continue
		}
		switch method.Kind {
		case GearUnknown:
			unknown = UnreadBills
		case GearWait:
			spec, ok := method.Bill.Spec.Value()
			if !ok {
				unknown = UnreadBills
				continue
			}
			orders = append(orders, spec)
		case GearProduce:
			if pending {
				continue
			}
			ingredients := make([]string, len(method.Filter))
			for i, resource := range method.Filter {
				ingredients[i] = string(resource)
			}
			orders = append(orders, OrderSpec{Recipe: method.Recipe, Ingredients: ingredients, Mode: domain.GearBatch, Target: method.Count, BenchKind: gearBenchDef(benches, method.Bench)})
		}
	}
	return orders, unknown, nil
}

func gearBenchDef(benches []GearBench, id string) string {
	for _, b := range benches {
		if b.ID == id {
			return b.Def
		}
	}
	return ""
}

// shellOrders are the stock-target orders of the mortar shell targets, in
// priority order: a shell some bench bill already makes is declared as that
// bill stands, else the first bench (by id) with an available recipe makes it.
// A target no recipe makes is skipped.
func shellOrders(benches []GearBench, targets []Amount) (orders []OrderSpec, unknown UnreadFact, err error) {
	sorted := slices.Clone(benches)
	sort.Slice(sorted, func(i, j int) bool { return sorted[i].ID < sorted[j].ID })
	standing := map[Resource]GearBill{}
	for _, b := range sorted {
		bills, _ := b.Bills.Value()
		for _, bill := range bills {
			if bill.Spent {
				continue
			}
			for _, product := range bill.Products {
				if _, ok := standing[product]; !ok {
					standing[product] = bill
				}
			}
		}
	}
	for _, t := range targets {
		if t.Count <= 0 {
			continue
		}
		if bill, ok := standing[t.Resource]; ok {
			spec, known := bill.Spec.Value()
			if !known {
				unknown = UnreadBills
				continue
			}
			orders = append(orders, spec)
			continue
		}
		if t.Count > math.MaxInt32 {
			return nil, "", fmt.Errorf("shell bill target %d exceeds the bill count", t.Count)
		}
	order:
		for _, b := range sorted {
			recipes, known := b.Recipes.Value()
			if !known {
				continue
			}
			for _, recipe := range recipes {
				available, ak := recipe.Available.Value()
				on, ok := recipe.AvailableOn.Value()
				if ak && available && ok && on && slices.Contains(recipe.Products, t.Resource) {
					orders = append(orders, OrderSpec{Recipe: recipe.Definition, Mode: domain.StockTarget, Target: int32(t.Count), BenchKind: b.Def})
					break order
				}
			}
		}
	}
	return orders, unknown, nil
}
