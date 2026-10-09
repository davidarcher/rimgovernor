package policy

import (
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// OpenBill is one open production bill of the journal: its recipe, the batch
// count, the bill's ingredient filter (the loadout's stuff and the
// substitutes the planner treats as equivalent; empty allows every member)
// and the recipe's catalog slots, one alternative list per ingredient slot
// with the count one unit of product takes. Slots is unknown while the
// catalog has no row for the recipe.
type OpenBill struct {
	Recipe string
	Count  int32
	Filter []string
	Slots  domain.Fact[[][]Amount]
}

// OpenBillDemand is the stock MaintainResource must reach for the open
// bills' ingredients, per resource, counted where the stock falls short of
// the bills' total. Each slot is demanded at its cheapest filtered member
// (lowest count, then name), whether or not native would run the bill now.
// A bill with unknown slots, or a slot the filter leaves no member of,
// contributes nothing; the demand is an absolute stock level like
// ConstructionDemand and merges into it by maximum.
func OpenBillDemand(bills []OpenBill, stock StockReader) map[Resource]int64 {
	need := map[Resource]int64{}
	for _, bill := range bills {
		for _, pick := range bill.picks() {
			need[pick.Resource] += pick.Count
		}
	}
	var out map[Resource]int64
	for resource, n := range need {
		if have, known := stock.Count(resource).Value(); known && n > have {
			if out == nil {
				out = map[Resource]int64{}
			}
			out[resource] = n
		}
	}
	return out
}

// picks are the bill's ingredient demand: each slot's cheapest filtered
// member (lowest count, then name) at the count the whole batch takes. A bill
// with unknown slots, or a slot the filter leaves no member of, gives none
// for that slot.
func (b OpenBill) picks() []Amount {
	slots, known := b.Slots.Value()
	if !known || b.Count <= 0 {
		return nil
	}
	var out []Amount
	for _, slot := range slots {
		var pick Amount
		for _, a := range slot {
			if len(b.Filter) > 0 && !slices.Contains(b.Filter, string(a.Resource)) {
				continue
			}
			if pick.Resource == "" || a.Count < pick.Count || a.Count == pick.Count && a.Resource < pick.Resource {
				pick = a
			}
		}
		if pick.Resource != "" && pick.Count > 0 {
			out = append(out, Amount{Resource: pick.Resource, Count: pick.Count * int64(b.Count)})
		}
	}
	return out
}
