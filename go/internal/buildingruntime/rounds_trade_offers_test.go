package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func bookedOffers(trader string, tick domain.Tick) policy.TradeOffers {
	return policy.TradeOffers{Trader: trader, Tick: tick, Silver: 1000, GoodsStacks: 20, Rows: []policy.TradeOffer{{Def: "Steel", Count: 100, Price: 8}}}
}

func presentTraders(ids ...string) []policy.TraderFacts {
	var out []policy.TraderFacts
	for _, id := range ids {
		out = append(out, policy.TraderFacts{ID: id, CanTrade: true, GoodsStacks: 20})
	}
	return out
}

// A stale offer cannot hold a resource: the book hands the plan nothing once
// the record ages out, the trader leaves, the caravan is settled, or the
// colony's silver moved, so the plan falls back to its other candidates.
func TestStaleTradeOfferCannotHoldAResource(t *testing.T) {
	t.Parallel()
	snap := domain.GenerationSnapshot{Colony: "c", Load: "l", Plan: "p"}
	var book tradeOfferBook
	book.record(snap, bookedOffers("a", 1000))
	if got := book.fresh(snap, 1100, 1000, presentTraders("a")); len(got) != 1 {
		t.Fatal("fresh record not offered", got)
	}
	if got := book.fresh(snap, 1000+policy.TradeOffersMaxAgeTicks+1, 1000, presentTraders("a")); len(got) != 0 {
		t.Fatal("an aged record held", got)
	}
	if got := book.fresh(snap, 1100, 400, presentTraders("a")); len(got) != 0 {
		t.Fatal("a record held after silver moved", got)
	}
	if got := book.fresh(snap, 1100, 1000, presentTraders("b")); len(got) != 0 {
		t.Fatal("a departed trader's record held", got)
	}
	if got := book.fresh(snap, 1100, 1000, presentTraders("a")); len(got) != 0 {
		t.Fatal("a departed trader's record came back", got)
	}
	book.record(snap, bookedOffers("a", 1000))
	book.drop("a")
	if got := book.fresh(snap, 1100, 1000, presentTraders("a")); len(got) != 0 {
		t.Fatal("a settled caravan's record held", got)
	}
	book.record(snap, bookedOffers("a", 1000))
	if got := book.fresh(domain.GenerationSnapshot{Colony: "c", Load: "l2", Plan: "p"}, 1100, 1000, presentTraders("a")); len(got) != 0 {
		t.Fatal("a record crossed a load")
	}
}

// Recording or dropping a record changes the version the plan cache keys on.
func TestTradeOfferBookVersionKeysThePlan(t *testing.T) {
	t.Parallel()
	snap := domain.GenerationSnapshot{Colony: "c", Load: "l", Plan: "p"}
	var book tradeOfferBook
	v := book.version()
	book.record(snap, bookedOffers("a", 1))
	if book.version() == v {
		t.Fatal("record did not change the version")
	}
	v = book.version()
	book.drop("a")
	if book.version() == v {
		t.Fatal("drop did not change the version")
	}
}

// The trade need buys a resource only as far as the plan opened the offer.
func TestRestrictToPlan(t *testing.T) {
	t.Parallel()
	need := policy.TradeNeed{
		MedicineReplenish:  5,
		ComponentShortfall: 10,
		Shortfall:          []policy.Amount{{Resource: "Steel", Count: 100}, {Resource: "WoodLog", Count: 50}},
		Surplus:            []policy.Amount{{Resource: "Gold", Count: 7}},
	}
	got := restrictToPlan(need, map[policy.Resource]int64{"Steel": 40, policy.ComponentResource: 3}, 0)
	if len(got.Shortfall) != 1 || got.Shortfall[0] != (policy.Amount{Resource: "Steel", Count: 40}) {
		t.Errorf("shortfall %v", got.Shortfall)
	}
	if got.ComponentShortfall != 3 || got.MedicineReplenish != 5 || len(got.Surplus) != 1 {
		t.Errorf("restricted more than resource purchases: %+v", got)
	}
	if none := restrictToPlan(need, nil, 0); len(none.Shortfall) != 0 || none.ComponentShortfall != 0 {
		t.Errorf("unplanned purchases stayed: %+v", none)
	}
}
