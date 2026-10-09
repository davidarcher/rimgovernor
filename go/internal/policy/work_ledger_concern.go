package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// MaintainWorkLedger keeps the benches' production bills equal to the orders
// the planners declared (ReconcileLedger). It is a Standard in the Industry
// department whose target is no outstanding reconcile work, like
// ManagePollution: Unmet exactly while the diff between declared and actual
// bills is non-empty (LedgerDiffOwed). Its one method is the Round's batched
// plan of production_bill and remove_production_bill actions.
const MaintainWorkLedger ConcernID = "MaintainWorkLedger"

const workLedgerPriority = 3

// LedgerBenchSlots is the vanilla bill stack cap of one bench.
const LedgerBenchSlots = 15

// LedgerActuals reads the benches' bills in the ledger's terms. It is unknown
// when any bench's bills, or any bill's spec, was unread: a diff against a
// partial readback would place duplicates and remove wanted bills.
func LedgerActuals(benches []GearBench) ([]ActualBill, bool) {
	var out []ActualBill
	for _, bench := range benches {
		bills, known := bench.Bills.Value()
		if !known || bench.Def == "" {
			return nil, false
		}
		for _, bill := range bills {
			spec, known := bill.Spec.Value()
			if !known || bill.ID == "" {
				return nil, false
			}
			out = append(out, ActualBill{ID: bill.ID, Bench: bench.ID, Kind: bill.Kind, Spec: spec, Spent: bill.Spent})
		}
	}
	return out, true
}

// LedgerDiffOwed is MaintainWorkLedger's finding: owed while the plan places or
// removes anything.
func LedgerDiffOwed(plan LedgerPlan) bool { return len(plan.Place) > 0 || len(plan.Remove) > 0 }

// LedgerPlacement is a wanted order assigned to the bench that will carry it.
type LedgerPlacement struct {
	Bench string
	Spec  OrderSpec
}

// PlaceLedgerOrders assigns each order (one entry per wanted copy) to an
// eligible bench (EligibleBenches) that is not full and does not already carry
// the order: the fastest bench by work speed, an unknown speed last, then the
// one carrying the fewest bills, then by id. A bench is full when its readback
// bill count reaches LedgerBenchSlots. Placements earlier in the same plan
// count. Orders no bench can take are returned unplaced.
func PlaceLedgerOrders(orders []OrderSpec, benches []GearBench) (placed []LedgerPlacement, unplaced []OrderSpec) {
	load := map[string]int{}
	carries := map[[2]string]bool{}
	speed := map[string]float64{}
	for _, b := range benches {
		bills, _ := b.Bills.Value()
		load[b.ID] = len(bills)
		for _, bill := range bills {
			if spec, ok := bill.Spec.Value(); ok && !bill.Spent {
				carries[[2]string{b.ID, spec.Key()}] = true
			}
		}
		if s, ok := b.WorkSpeed.Value(); ok {
			speed[b.ID] = s
		}
	}
	sorted := append([]GearBench(nil), benches...)
	sort.Slice(sorted, func(i, j int) bool { return sorted[i].ID < sorted[j].ID })
	// A plan carries one bill per bench and claim recipe (the store's rule), so
	// a second order of the same recipe takes another bench or waits a Round.
	taken := map[[2]string]bool{}
	for _, order := range orders {
		best := ""
		claim := order.Recipe
		if order.Mode == domain.HumanButcherForever {
			claim += "/humanlike"
		} else if order.Mode == domain.GearBatch && order.Worker != "" {
			claim += "/" + order.Worker
		}
		for _, b := range sorted {
			if b.Def != order.BenchKind || load[b.ID] >= LedgerBenchSlots || taken[[2]string{b.ID, claim}] || carries[[2]string{b.ID, order.Key()}] || !benchOffers(b, order.Recipe) {
				continue
			}
			if best == "" || fasterBench(speed, load, b.ID, best) {
				best = b.ID
			}
		}
		if best == "" {
			unplaced = append(unplaced, order)
			continue
		}
		load[best]++
		taken[[2]string{best, claim}] = true
		carries[[2]string{best, order.Key()}] = true
		placed = append(placed, LedgerPlacement{Bench: best, Spec: order})
	}
	return placed, unplaced
}

// fasterBench is whether bench a beats b: higher known work speed, then fewer
// bills. Ids were visited in order, so a tie keeps the earlier bench.
func fasterBench(speed map[string]float64, load map[string]int, a, b string) bool {
	sa, aKnown := speed[a]
	sb, bKnown := speed[b]
	switch {
	case aKnown != bKnown:
		return aKnown
	case sa != sb:
		return sa > sb
	}
	return load[a] < load[b]
}

func benchOffers(b GearBench, recipe string) bool {
	if usable, known := b.Usable.Value(); !known || !usable {
		return false
	}
	recipes, known := b.Recipes.Value()
	if !known {
		return false
	}
	for _, r := range recipes {
		if r.Definition == recipe {
			on, known := r.AvailableOn.Value()
			return known && on
		}
	}
	return false
}

// inspectWorkLedger: unknown while the declarations or the bench readback are
// unread, and then it raises no Concern.
func inspectWorkLedger(c *roundsRun) error {
	c.assess(MaintainWorkLedger, workLedgerPriority, notFact(c.f.LedgerOwed))
	if owed, known := c.f.LedgerOwed.Value(); known && owed {
		c.raise(MaintainWorkLedger, workLedgerPriority)
	}
	return nil
}
