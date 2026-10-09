package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// DispatchMemory is the dispatcher's calibration state: the rolling windows,
// the benches added on top of the rate model and the standing shortfalls. It is
// memory only; a world change empties it.
type DispatchMemory struct {
	Windows    map[string]ThroughputWindow
	Widen      map[string]int
	Shortfalls map[string]UnmetThroughput
}

// DispatchInputs is one Round's reading for the dispatcher.
type DispatchInputs struct {
	Declared []Declared
	Benches  []GearBench
	Pawns    []WorkPawn
	Stock    domain.Fact[map[Resource]int64]
	Tick     int64
}

// Dispatch sizes every declared order (the copies ReconcileLedger wants), then
// feeds the Round's measurement of each stock target into its calibration
// window, widening or recording the shortfall for the next Round. Memory of an
// order no planner declares is dropped, unless a planner abstained.
func (m *DispatchMemory) Dispatch(in DispatchInputs) (copies map[string]int, dispatch map[string]OrderDispatch) {
	wanted, abstain := WantedOrders(in.Declared)
	if m.Windows == nil {
		m.Windows, m.Widen, m.Shortfalls = map[string]ThroughputWindow{}, map[string]int{}, map[string]UnmetThroughput{}
	}
	copies, dispatch = map[string]int{}, map[string]OrderDispatch{}
	for k, order := range wanted {
		d := DispatchOrder(order, in.Stock, in.Benches, in.Pawns, m.Widen[k])
		dispatch[k], copies[k] = d, d.Copies
		if !order.Stocked() {
			continue
		}
		window, cal := CalibrateThroughput(m.Windows[k], ObserveOrder(order, d, in))
		m.Windows[k] = window
		switch {
		case cal.Closed && cal.Widen:
			m.Widen[k]++
			delete(m.Shortfalls, k)
		case cal.Closed && cal.Short:
			m.Shortfalls[k] = UnmetThroughput{BenchKind: order.BenchKind, ShortPerDay: cal.ShortPerDay, Reason: cal.Reason}
		case cal.Closed:
			delete(m.Shortfalls, k)
		}
	}
	if !abstain {
		for k := range m.Windows {
			if _, ok := wanted[k]; !ok {
				delete(m.Windows, k)
				delete(m.Widen, k)
				delete(m.Shortfalls, k)
			}
		}
	}
	return copies, dispatch
}

// Unmet is the Round's unmet throughput per bench kind: the standing
// calibration shortfalls, the orders the rate model wants more benches for
// than exist and the orders no bench could take (unplaced).
func (m *DispatchMemory) Unmet(dispatch map[string]OrderDispatch, wanted map[string]OrderSpec, unplaced []OrderSpec, benches []GearBench) []UnmetThroughput {
	var items []UnmetThroughput
	for _, u := range m.Shortfalls {
		items = append(items, u)
	}
	for k, d := range dispatch {
		if d.NoBenchShort > 0 {
			items = append(items, UnmetThroughput{BenchKind: wanted[k].BenchKind, ShortPerDay: d.NoBenchShort, Reason: UnmetNoBench})
		}
	}
	items = append(items, UnplacedUnmet(unplaced, benches, dispatch)...)
	return MergeUnmet(items)
}

// ObserveOrder reads one stock-target order off the Round: the benches
// carrying it, whether its bills are active and a pawn works them, and whether
// the recipe's ingredients are in stock.
func ObserveOrder(order OrderSpec, d OrderDispatch, in DispatchInputs) ThroughputObservation {
	key := order.Key()
	o := ThroughputObservation{Tick: in.Tick, Target: int64(order.Target), Eligible: d.Eligible, Capacity: d.Capacity, Stock: domain.Unknown[int64](), Active: true, Ingredients: domain.Unknown[bool]()}
	if have, ok := in.Stock.Value(); ok {
		o.Stock = domain.Known(have[order.Product])
	}
	var carrying []string
	for _, b := range in.Benches {
		bills, _ := b.Bills.Value()
		for _, bill := range bills {
			if spec, ok := bill.Spec.Value(); ok && !bill.Spent && spec.Key() == key {
				carrying = append(carrying, b.ID)
				if active, ok := bill.Active.Value(); !ok || !active {
					o.Active = false
				}
			}
		}
	}
	o.Carrying = len(carrying)
	o.Busy = BenchesBusy(in.Pawns, carrying)
	if eligible := EligibleBenches(order, in.Benches); len(eligible) > 0 {
		o.Ingredients = ingredientsStocked(recipeOf(eligible[0], order.Recipe), in.Stock)
	}
	return o
}

// ingredientsStocked is known false when some ingredient slot has every
// alternative at or below IngredientBoundStock, known true when every slot has
// one above it, unknown when the recipe's ingredients or the stock are unread.
func ingredientsStocked(r GearRecipe, stock domain.Fact[map[Resource]int64]) domain.Fact[bool] {
	slots, ok := r.Ingredients.Value()
	have, haveOK := stock.Value()
	if !ok || !haveOK || len(slots) == 0 {
		return domain.Unknown[bool]()
	}
	for _, alternatives := range slots {
		present := false
		for _, a := range alternatives {
			if have[a.Resource] > IngredientBoundStock {
				present = true
			}
		}
		if !present {
			return domain.Known(false)
		}
	}
	return domain.Known(true)
}
