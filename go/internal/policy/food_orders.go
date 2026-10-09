package policy

import (
	"slices"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The food planners declare the production orders they want standing to the
// work ledger (OrderDeclarer), as the gear planners do (gear_orders.go). A
// declaration is derived from the reviewed facts and the bench census alone:
// the census is read without its bills, so the same selection that once chose
// the next bill to place now chooses the order that should stand, every Round.
// A bill that already does the work is declared as it stands (adequateOrder),
// which keeps it; any other food bill of a migrated owner is an orphan.

// FoodOrderGap says why a food declaration chose no order.
type FoodOrderGap string

const (
	// FoodOrderChosen: at least one order was chosen.
	FoodOrderChosen FoodOrderGap = ""
	// FoodOrderField: Subject names a fact the declaration needs and lacks.
	FoodOrderField FoodOrderGap = "field"
	// FoodOrderPlan: the food plan does not yet support the capacity in Subject.
	FoodOrderPlan FoodOrderGap = "food_plan"
	// FoodOrderNoDeficit: the purpose is not owed.
	FoodOrderNoDeficit FoodOrderGap = "no_deficit"
	// FoodOrderNoBill: the purpose is owed but no bench and recipe can serve it.
	FoodOrderNoBill FoodOrderGap = "no_bill"
)

// FoodOrderRequest is one food purpose's reviewed inputs. Benches is the
// census with its bills; Supply is the combined food supply and Humans the
// human consumers' supply.
type FoodOrderRequest struct {
	Purpose BillPurpose
	Benches domain.Fact[[]ProductionBench]
	Facts   RoundsFacts
	Supply  domain.Fact[FoodSupply]
	Humans  domain.Fact[FoodSupply]
	// Meals is the cooking purpose's meal request; TargetDays the seasonal
	// runway target both it and the other selections size against.
	Meals      MealTierRequest
	TargetDays float64
	// Warm is the cook-ahead purpose's at-risk nutrition (the refrigeration
	// review's warm stock).
	Warm domain.Fact[float64]
}

// FoodOrder is the selection of one food purpose. Selections are the bills that
// should stand, with the bench each was chosen on.
type FoodOrder struct {
	Selections []BillSelection
	Gap        FoodOrderGap
	Subject    string
	// ReserveRunning is whether a standing reserve bill is cooking while the
	// reserve is short: the product needs game time, not another method.
	ReserveRunning bool
}

// FoodPlanSupport is whether the food plan opened or holds the channel.
func FoodPlanSupport(p domain.Fact[FoodPlan], kind CandidateKind, id string) bool {
	plan, known := p.Value()
	if !known {
		return false
	}
	for _, entry := range plan.Portfolio {
		if entry.Channel.Kind == kind && entry.Channel.ID == id {
			return entry.Decision == FoodPlanOpen || entry.Decision == FoodPlanHold
		}
	}
	return false
}

// withoutBills copies the census with every bench's bills dropped: the
// selections then ignore what already stands, which the ledger reconciles.
func withoutBills(benches domain.Fact[[]ProductionBench]) domain.Fact[[]ProductionBench] {
	rows, known := benches.Value()
	if !known {
		return benches
	}
	out := make([]ProductionBench, len(rows))
	for i, b := range rows {
		b.Bills = nil
		out[i] = b
	}
	return domain.Known(out)
}

// SelectFoodOrder chooses the food orders a purpose wants standing. A selector
// that cannot say (an unread fact, a food-plan channel not yet open) names the
// gap and chooses nothing.
func SelectFoodOrder(r FoodOrderRequest) FoodOrder {
	f := r.Facts
	census := withoutBills(r.Benches)
	var out FoodOrder
	gap := func(g FoodOrderGap, subject string) FoodOrder {
		out.Selections, out.Gap, out.Subject = nil, g, subject
		return out
	}
	switch r.Purpose {
	case CookFood:
		if !FoodPlanSupport(f.FoodPlan, CandidateCook, "cooking-capacity") {
			return gap(FoodOrderPlan, "cooking-capacity")
		}
		meals := r.Meals
		context := ProductionBillContext{Meals: &meals}
		if supply, ok := r.Supply.Value(); ok {
			if humans, ok := r.Humans.Value(); ok {
				context.Ingredients = HumanCookingIngredients(supply, humans.Consumers, f.FoodPlan, HumanMeatMeals)
			}
		}
		if selected, known := SelectProductionBill(CookFood, census, f.Colonists, f.FoodDays, domain.Fact[float64]{}, r.TargetDays, context); known {
			out.Selections = append(out.Selections, selected)
		}
	case PreserveFood:
		if !FoodPlanSupport(f.FoodPlan, CandidateReserve, "stock-protection") {
			return gap(FoodOrderPlan, "stock-protection")
		}
		reserve, known := f.FoodReserve.Value()
		if !known {
			return gap(FoodOrderField, "food_reserve")
		}
		out.ReserveRunning = ReserveBillRunning(r.Benches, reserve)
		if selected, known := SelectProductionBill(PreserveFood, census, f.Colonists, f.FoodDays, domain.Fact[float64]{}, r.TargetDays, ProductionBillContext{Reserve: &reserve}); known {
			out.Selections = append(out.Selections, selected)
		}
		if supply, ok := r.Supply.Value(); ok {
			if sale, ok := SelectHumanSurvivalBill(census, supply, f.FoodPlan); ok {
				out.Selections = append(out.Selections, sale)
			}
		}
	case ButcherFood:
		// Owed on the food runway alone: native offers no hunt row until a
		// usable bench carries this bill.
		days, known := f.FoodDays.Value()
		if (!known || days >= r.TargetDays) && !HumanFoodPending(f.FoodPlan) {
			if !known {
				return gap(FoodOrderField, "food_days")
			}
			return gap(FoodOrderNoDeficit, "")
		}
		if selected, known := SelectProductionBill(ButcherFood, census, f.Colonists, f.FoodDays, domain.Fact[float64]{}, r.TargetDays); known {
			out.Selections = append(out.Selections, selected)
		}
		if human, ok := SelectHumanButcher(census, f.IdeologyRead()); ok {
			if !FoodPlanSupport(f.FoodPlan, CandidateCorpse, "human-butchery") {
				return gap(FoodOrderPlan, "human-butchery")
			}
			out.Selections = append(out.Selections, human)
		}
	case CookAheadFood:
		// Only under a solar flare with a known remaining duration: the
		// coolers are dark for the outage, so the warm at-risk stock is
		// cooked instead.
		if !PowerOutageHold(f.DisasterConditions) {
			return gap(FoodOrderNoDeficit, "")
		}
		if selected, known := SelectProductionBill(CookAheadFood, census, f.Colonists, f.FoodDays, r.Warm, r.TargetDays); known {
			out.Selections = append(out.Selections, selected)
		}
	default:
		return gap(FoodOrderNoBill, "")
	}
	if len(out.Selections) == 0 {
		out.Gap = FoodOrderNoBill
	}
	return out
}

// Order is the selection as the ledger identifies it on a bench of the kind.
func (s BillSelection) Order(benchKind string) OrderSpec {
	return OrderSpec{Recipe: s.Recipe, Ingredients: slices.Clone(s.Ingredients), Worker: s.Worker, Mode: s.Mode, Target: s.Target, BenchKind: benchKind}
}

// DeclareFoodOrders is a food purpose's wanted orders. Abstain while a fact the
// selection needs is unread, the food plan has not opened the capacity, or a
// standing bill's readback is unread. A purpose that is not owed, or has no
// bench to serve it, keeps the bills that already stand for it, as the owners
// did before the ledger: the food runway is the last thing to starve on a
// transient fact. A solar-flare cook-ahead is the exception, which ends with
// the flare.
func DeclareFoodOrders(r FoodOrderRequest, benches []GearBench) Declared {
	order := SelectFoodOrder(r)
	if order.Gap == FoodOrderField || order.Gap == FoodOrderPlan {
		return Declared{Abstain: true}
	}
	census, _ := r.Benches.Value()
	kinds := map[string]string{}
	for _, b := range census {
		kinds[b.ID] = b.Definition
	}
	var out Declared
	for _, selected := range order.Selections {
		kind, ok := kinds[selected.Bench]
		if !ok || kind == "" {
			return Declared{Abstain: true}
		}
		spec, known := adequateOrder(selected.Order(kind), benches)
		if !known {
			return Declared{Abstain: true}
		}
		out.Orders = append(out.Orders, spec)
	}
	if len(out.Orders) > 0 || r.Purpose == CookAheadFood {
		return out
	}
	standing, known := standingOrders(benches, foodFamily(r.Purpose))
	return Declared{Orders: standing, Abstain: !known}
}

// foodFamily is whether a bill belongs to a purpose's family.
func foodFamily(purpose BillPurpose) func(GearBill) bool {
	switch purpose {
	case CookFood:
		return func(b GearBill) bool { return b.Role == domain.RoleOrdinaryMeal }
	case ButcherFood:
		return func(b GearBill) bool { return b.Role == domain.RoleButcherFlesh }
	case PreserveFood:
		return func(b GearBill) bool { return slices.ContainsFunc(b.Products, ReserveFoodDefinition) }
	}
	return func(GearBill) bool { return false }
}

// standingOrders are the specs of the unspent production bills of a family, as
// they stand; known is false while one's readback is unread.
func standingOrders(benches []GearBench, family func(GearBill) bool) (orders []OrderSpec, known bool) {
	for _, b := range benches {
		bills, ok := b.Bills.Value()
		if !ok {
			return nil, false
		}
		for _, bill := range bills {
			if bill.Spent || bill.Kind != LedgerProduction || !family(bill) {
				continue
			}
			spec, ok := bill.Spec.Value()
			if !ok {
				return nil, false
			}
			orders = append(orders, spec)
		}
	}
	return orders, true
}

// adequateOrder is a stock-target order's declaration: a bill that already
// stands on the order's bench kind and recipe, with its pin and ingredients and
// a target that covers the wanted one, is declared as it stands, so a target
// that moves with the colony does not replace the bill every Round. Any other
// order is declared as wanted. Known is false while a bill's readback is unread.
func adequateOrder(want OrderSpec, benches []GearBench) (OrderSpec, bool) {
	if wireClass(want.Mode) != domain.StockTarget {
		return want, true
	}
	var best OrderSpec
	bestID, found := "", false
	for _, b := range benches {
		bills, ok := b.Bills.Value()
		if !ok {
			return OrderSpec{}, false
		}
		for _, bill := range bills {
			spec, ok := bill.Spec.Value()
			if !ok {
				return OrderSpec{}, false
			}
			if bill.Spent || bill.Kind != LedgerProduction || spec.BenchKind != want.BenchKind || spec.Recipe != want.Recipe || wireClass(spec.Mode) != domain.StockTarget ||
				spec.Worker != want.Worker || spec.Target < want.Target || !sameSet(spec.Ingredients, want.Ingredients) {
				continue
			}
			if !found || spec.Target < best.Target || spec.Target == best.Target && bill.ID < bestID {
				best, bestID, found = spec, bill.ID, true
			}
		}
	}
	if found {
		return best, true
	}
	return want, true
}

func sameSet(a, b []string) bool {
	a, b = slices.Clone(a), slices.Clone(b)
	sort.Strings(a)
	sort.Strings(b)
	return slices.Equal(a, b)
}

// FoodStorageMealOrder is MaintainFoodStorage's produce fallback as an order:
// the stock-target bill of the choice's resource on the first bench (by id)
// whose recipe makes it, chosen as if no bill stood. Unknown while the bench
// recipes are unread; ok is false when no bench can make the resource.
func FoodStorageMealOrder(choice FoodStorageMethod, benches []GearBench) (spec OrderSpec, kind MedicineMethodKind, ok bool, err error) {
	bare := make([]GearBench, len(benches))
	for i, b := range benches {
		b.Bills = domain.Known([]GearBill(nil))
		bare[i] = b
	}
	picked, err := SelectMedicineMethod(MedicinePlanningRequest{Review: MedicalReserveReview{Active: true, Target: domain.Known(choice.Target)}, Resource: choice.Resource, Benches: domain.Known(bare)})
	if err != nil || picked.Kind != MedicineProduce {
		return OrderSpec{}, picked.Kind, false, err
	}
	def := ""
	for _, b := range benches {
		if b.ID == picked.Bench {
			def = b.Def
		}
	}
	spec = OrderSpec{Recipe: picked.Recipe, Mode: domain.StockTarget, Target: int32(picked.Target), BenchKind: def}
	spec, _ = adequateOrder(spec, benches)
	return spec, picked.Kind, def != "", nil
}

// StandingStockOrders are the unspent bills that make the resource, declared as
// they stand; Abstain while a bill readback is unread.
func StandingStockOrders(benches []GearBench, resource Resource) Declared {
	orders, known := standingOrders(benches, func(b GearBill) bool { return slices.Contains(b.Products, resource) })
	return Declared{Orders: orders, Abstain: !known}
}
