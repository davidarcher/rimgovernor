package policy

import (
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// OpenBillExpiry is how long a journaled bill may wait undispatched before
// the review cancels it: the need that placed it may be gone (its colonist
// died), native has no stock check to retire it, and a bill still wanted is
// placed again by its planner.
const OpenBillExpiry = domain.Tick(domain.TicksPerDay)

// OpenBillExpired reports whether a bill admitted at admitted has waited
// longer than OpenBillExpiry at now.
func OpenBillExpired(admitted, now domain.Tick) bool {
	return now > admitted && now-admitted > OpenBillExpiry
}

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

// StaleBillReviews is how many consecutive reviews a finite bill's owner
// must stay Met before the bill is stale (#2411); a restart restarts the count.
const StaleBillReviews = 4

// StaleBill is a finite (gear_batch) bill still on its bench whose journaled
// owner placed it: the id RemoveProductionBill names. Products and Worker are
// the bench census's read of the bill, what an owner's wanted set is matched
// against; Worker is "" unless the census knew the pinned colonist.
type StaleBill struct {
	Owner     ConcernID
	Bench, ID string
	Products  []Resource
	Worker    string
}

// UnwantedBillOwner reports whether id's planners remove a bill that matches
// nothing the owner currently wants, whatever its finding (#2433): the owners
// of StaleBillOwner plus EnsureFoodSupply, whose hunter-weapon bills the armory
// planner removes under the food Standard without filing it Unmet.
//
// MaintainMechs is excluded from both rules. A gestation is a Bill_Mech: once
// a gestator starts it the bill is the mech being formed, which neither a pawn
// job nor an unfinished item shows the native handler, so removing it destroys
// the mech; and MechGestationOwed reads not-owed for exactly that stretch, so
// the owner stays Met while the bill is legitimately working and a Met-streak
// rule would name it stale mid-gestation.
func UnwantedBillOwner(id ConcernID) bool {
	return StaleBillOwner(id) || id == EnsureFoodSupply
}

// StaleBillOwner reports whether id is filed Unmet for a bill it placed once it
// stayed Met for StaleBillReviews (#2411). EnsureFoodSupply is excluded:
// filing it Unmet would start the food machinery.
func StaleBillOwner(id ConcernID) bool {
	return id == MaintainEquipment || id == MaintainArt || id == MaintainSurgery
}

// HasStaleBill reports whether any of bills belongs to owner.
func HasStaleBill(bills []StaleBill, owner ConcernID) bool {
	for _, b := range bills {
		if b.Owner == owner {
			return true
		}
	}
	return false
}
