package policy

import (
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// TradeOffers is what one caravan's open trade sheet priced when the
// negotiator last looked at it (derived state, Go memory only). A caravan's
// prices exist only in a session's sheet, so the supply plan reads these
// records as trade candidates; the live sheet re-prices every purchase when
// it executes. RoundsTradePlanner records one at each sheet read and
// Rounder.tradeOffers hands out the fresh ones.
type TradeOffers struct {
	Trader     string
	Negotiator string
	// Tick is the observed tick of the sheet read.
	Tick domain.Tick
	// Silver is the colony's silver on the sheet and GoodsStacks the number
	// of goods stacks the trader carried: the two facts whose change makes
	// the record stale (TradeOffersFresh).
	Silver      int64
	GoodsStacks int64
	Rows        []TradeOffer
}

// TradeOffer is one priced row the trader sells: the definition, how many
// units it holds and the silver price of one.
type TradeOffer struct {
	Def   string
	Count int64
	Price float64
}

// A record is read again once it is older than TradeOffersMaxAgeTicks (a few
// game hours), or the colony's silver or the trader's goods-stack count moved
// by more than TradeOffersShift of the recorded value.
const (
	TradeOffersMaxAgeTicks domain.Tick = 3 * domain.TicksPerHour
	TradeOffersShift                   = 0.25
)

// TradeOffersFresh reports whether a record still describes the trader at
// tick now, given the colony's current silver and the trader's current goods
// stacks.
func TradeOffersFresh(o TradeOffers, now domain.Tick, silver, goodsStacks int64) bool {
	if o.Tick < now && now-o.Tick > TradeOffersMaxAgeTicks {
		return false
	}
	return !shifted(o.Silver, silver) && !shifted(o.GoodsStacks, goodsStacks)
}

func shifted(was, now int64) bool {
	return math.Abs(float64(now-was)) > TradeOffersShift*math.Max(1, math.Abs(float64(was)))
}

// TradeCandidateID names the candidate of one resource bought from one trader.
func TradeCandidateID(trader string, resource Resource) string {
	return trader + "/" + string(resource)
}

// TradeOfferCandidates are the catalog rows of buying resource from the
// recorded offers: the cheapest priced row of each trader, as many units as
// the deficit, the trader's stock and the silver above the reserve allow.
func TradeOfferCandidates(resource Resource, offers []TradeOffers, deficit, silver, reserve int64) []AcquisitionCandidate {
	var out []AcquisitionCandidate
	for _, record := range offers {
		var best *TradeOffer
		for i := range record.Rows {
			row := &record.Rows[i]
			if Resource(row.Def) != resource || row.Count <= 0 || !finite(row.Price) || row.Price <= 0 || row.Price > tradeBuyPriceCeiling {
				continue
			}
			if best == nil || row.Price < best.Price {
				best = row
			}
		}
		if best == nil {
			continue
		}
		afford := int64(math.Floor(float64(silver-reserve) / best.Price))
		if c, ok := TradeCandidate(resource, record.Trader, min(deficit, best.Count, afford), best.Price); ok {
			out = append(out, c)
		}
	}
	return out
}
