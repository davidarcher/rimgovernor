package policy

import (
	"fmt"
	"math"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// ResourceFloor is one stock floor MaintainResource wants a bench to hold.
type ResourceFloor struct {
	// Resource is the product a recipe must make: the floor's own resource, or
	// the wort of the beer reserve.
	Resource Resource
	Target   int64
	// Reserve is the beer reserve: a BeerReserve bill, wanted whatever the
	// stock, rather than a stock target wanted only below its floor.
	Reserve bool
}

// ResourceOrderRequest is the declaration request of MaintainResource's bench
// production: the floors, the stock census and the ledger's bench readback.
type ResourceOrderRequest struct {
	Floors  []ResourceFloor
	Stock   domain.Fact[map[Resource]int64]
	Benches domain.Fact[[]GearBench]
}

// DeclareResourceOrders is MaintainResource's wanted bench orders. A floor
// below its target declares the first producing recipe's stock target (the
// dispatcher sizes how many benches carry it); a bill that already makes the
// resource is declared as it stands, which keeps it, so a floor whose stock
// recovered does not churn its bill. Bench choice is the ledger's: the order
// names the bench kind only. A floor with no producing recipe declares nothing
// (the planner's mining and sourcing methods take it). Abstain while the stock
// census, the bench readback or a bill's identity is unread.
func DeclareResourceOrders(r ResourceOrderRequest) (Declared, error) {
	stock, known := r.Stock.Value()
	benches, benchesKnown := r.Benches.Value()
	if !known || !benchesKnown {
		return Declared{Abstain: true}, nil
	}
	floors := append([]ResourceFloor(nil), r.Floors...)
	sort.SliceStable(floors, func(i, j int) bool { return floors[i].Resource < floors[j].Resource })
	var out Declared
	for _, f := range floors {
		if !validResource(f.Resource) || f.Target <= 0 || f.Target > math.MaxInt32 {
			return Declared{}, fmt.Errorf("invalid resource floor %q x%d", f.Resource, f.Target)
		}
		standing, active, unknown := standingResourceBills(benches, f.Resource)
		if unknown {
			out.Abstain = true
			continue
		}
		if active || !f.Reserve && stock[f.Resource] >= f.Target {
			out.Orders = append(out.Orders, standing...)
			continue
		}
		method, err := SelectResourceMethod(ResourceMethodRequest{Resource: f.Resource, Target: f.Target, Benches: r.Benches})
		if err != nil {
			return Declared{}, err
		}
		if method.Kind != ResourceMethodProduce {
			// Nothing to place: an idle bill stands as it is.
			out.Orders = append(out.Orders, standing...)
		}
		switch method.Kind {
		case ResourceMethodUnknown:
			out.Abstain = true
		case ResourceMethodProduce:
			// An idle bill of another size is replaced, not kept beside it.
			mode, class, product := domain.StockTarget, ResourceMaterial, f.Resource
			if f.Reserve {
				// The reserve's floor counts beer, not its wort: the rate
				// model has no product to size it by.
				mode, class, product = domain.BeerReserve, "", ""
			}
			out.Orders = append(out.Orders, OrderSpec{Recipe: method.Recipe, Mode: mode, Target: int32(f.Target), BenchKind: gearBenchDef(benches, method.Bench), Product: product, Class: class})
		}
	}
	return out, nil
}

// standingResourceBills are the specs of the unspent bills that make resource,
// and whether one of them is active (still counting toward its target).
// unknown is set while a bench's bills or a bill's identity is unread.
func standingResourceBills(benches []GearBench, resource Resource) (specs []OrderSpec, active, unknown bool) {
	for _, b := range benches {
		bills, known := b.Bills.Value()
		if !known {
			return nil, false, true
		}
		for _, bill := range bills {
			if bill.Spent || bill.Kind != LedgerProduction || !containsResource(bill.Products, resource) {
				continue
			}
			spec, specKnown := bill.Spec.Value()
			on, onKnown := bill.Active.Value()
			if !specKnown || !onKnown {
				return nil, false, true
			}
			active = active || on
			specs = append(specs, spec)
		}
	}
	return specs, active, false
}
