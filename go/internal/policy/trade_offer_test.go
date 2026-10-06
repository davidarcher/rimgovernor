package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func steelOffers(tick domain.Tick, price float64, count int64) TradeOffers {
	return TradeOffers{Trader: "caravan", Tick: tick, Silver: 1000, GoodsStacks: 20, Rows: []TradeOffer{{Def: "Steel", Count: count, Price: price}, {Def: "Silver", Count: 9, Price: 1}}}
}

// A record is read again past its age or once silver or stacks moved materially.
func TestTradeOffersFresh(t *testing.T) {
	t.Parallel()
	o := steelOffers(10000, 8, 100)
	for name, c := range map[string]struct {
		now           domain.Tick
		silver, stack int64
		fresh         bool
	}{
		"same":           {10000 + 100, 1000, 20, true},
		"at max age":     {10000 + TradeOffersMaxAgeTicks, 1000, 20, true},
		"too old":        {10000 + TradeOffersMaxAgeTicks + 1, 1000, 20, false},
		"silver spent":   {10100, 600, 20, false},
		"silver small":   {10100, 900, 20, true},
		"stacks bought":  {10100, 1000, 10, false},
		"recorded later": {9000, 1000, 20, true},
	} {
		if got := TradeOffersFresh(o, c.now, c.silver, c.stack); got != c.fresh {
			t.Errorf("%s: fresh %v, want %v", name, got, c.fresh)
		}
	}
}

// An offer is a candidate for its own resource at the cheapest row, capped by
// the deficit, the trader's stock and the silver above the reserve.
func TestTradeOfferCandidates(t *testing.T) {
	t.Parallel()
	units := func(c []AcquisitionCandidate) int64 {
		if len(c) != 1 {
			t.Fatalf("candidates %v", c)
		}
		return c[0].Yields[0].Count
	}
	o := []TradeOffers{steelOffers(0, 8, 100)}
	if got := units(TradeOfferCandidates("Steel", o, 60, 1000, 200)); got != 60 {
		t.Errorf("deficit cap: %d", got)
	}
	if got := units(TradeOfferCandidates("Steel", o, 500, 1000, 200)); got != 100 {
		t.Errorf("stock cap: %d", got)
	}
	if got := units(TradeOfferCandidates("Steel", o, 500, 520, 200)); got != 40 {
		t.Errorf("afford cap: %d", got)
	}
	if c := TradeOfferCandidates("Steel", o, 500, 200, 200); len(c) != 0 {
		t.Errorf("no silver above the reserve: %v", c)
	}
	if c := TradeOfferCandidates("Steel", []TradeOffers{steelOffers(0, tradeBuyPriceCeiling+1, 100)}, 50, 1e6, 0); len(c) != 0 {
		t.Errorf("price over the ceiling: %v", c)
	}
	if c := TradeOfferCandidates("WoodLog", o, 50, 1000, 200); len(c) != 0 {
		t.Errorf("another resource: %v", c)
	}
	if c := TradeOfferCandidates("Steel", o, 60, 1000, 200); c[0].ID != TradeCandidateID("caravan", "Steel") {
		t.Errorf("id %q", c[0].ID)
	}
}

// A dear caravan loses to a near deposit and a cheap one beats it: the offer
// is arbitrated by the plan like any other candidate.
func TestTradeOfferArbitratedAgainstMining(t *testing.T) {
	t.Parallel()
	mine := MineCandidates("Steel", []ResourceSource{{ThingID: "ore", Yield: 100, Distance: 10, Method: ResourceSourceMine, Safety: "open_surface"}}, domain.Known(int64(500)))
	for price, want := range map[float64]CandidateKind{8: CandidateTrade, 60: CandidateMining} {
		cands := append(TradeOfferCandidates("Steel", []TradeOffers{steelOffers(0, price, 100)}, 100, 100000, 0), mine...)
		plan, err := PlanResourceSupply([]ResourceSupplyInput{{Resource: "Steel", Deficit: 100, Candidates: cands}}, domain.Known(1e6))
		if err != nil {
			t.Fatal(err)
		}
		opened := plan.Opened("Steel")
		if len(opened) == 0 || opened[0].Candidate.Kind != want {
			t.Errorf("price %v: opened %v, want %s first\n%s", price, opened, want, plan.Plan.Explain())
		}
	}
}
