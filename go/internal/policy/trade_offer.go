package policy

import (
	"math"
	"sort"

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
	// Pawn is a live pawn row (Def is its race, Gender its sex), what an
	// animal purchase candidate prices.
	Pawn   bool
	Gender string
	// Food classifies a food row (TradeFoodGood); unknown for any other row.
	Food domain.Fact[TradeFoodGood]
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
func TradeOfferCandidates(resource Resource, offers []TradeOffers, deficit, silver, reserve int64) []SupplyCandidate {
	var out []SupplyCandidate
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

// TradeFoodChannels are the food candidates of buying from the recorded
// offers of the traders present: one one-shot channel per trader (ID is the
// trader id) holding the nutrition its cheapest-per-nutrition food rows give,
// as far as the silver above the reserve and want (nutrition) allow. It has no
// lead (the walk is a fraction of a day), no steady work and the silver as its
// upfront cost, priced as labor (tradeLaborPerSilver). A trader without a
// record, or whose record prices no food, has no candidate: an arrival the
// colony has not looked at is never planned for.
func TradeFoodChannels(offers []TradeOffers, silver, reserve int64, want float64) []SupplyCandidate {
	var out []SupplyCandidate
	for _, record := range offers {
		var rows []TradeOffer
		for _, row := range record.Rows {
			if g, known := row.Food.Value(); known && validTradeFood(g) && row.Count > 0 && finite(row.Price) && row.Price > 0 && row.Price <= tradeBuyPriceCeiling {
				rows = append(rows, row)
			}
		}
		perNutrition := func(r TradeOffer) float64 { g, _ := r.Food.Value(); return r.Price / g.Nutrition }
		sort.Slice(rows, func(i, j int) bool {
			if a, b := perNutrition(rows[i]), perNutrition(rows[j]); a != b {
				return a < b
			}
			return rows[i].Def < rows[j].Def
		})
		budget, wanted, nutrition, spent := float64(silver-reserve), want, 0.0, 0.0
		for _, row := range rows {
			g, _ := row.Food.Value()
			count := math.Min(float64(row.Count), math.Min(math.Floor(budget/row.Price), math.Ceil(wanted/g.Nutrition)))
			if !(count > 0) {
				continue
			}
			nutrition += count * g.Nutrition
			spent += count * row.Price
			budget -= count * row.Price
			wanted -= count * g.Nutrition
		}
		if stock := int64(math.Floor(nutrition)); stock >= 1 {
			c := FoodCandidate(CandidateTrade, record.Trader, domain.Unknown[float64]())
			c.Yields[0].StockCap = domain.Known(stock)
			c.LaborPerDay, c.UpfrontCost.LaborTicks, c.LeadDays = domain.Known(0.0), domain.Known(spent*tradeLaborPerSilver), domain.Known(0.0)
			c.State = FoodState(domain.Known(false), domain.Unknown[bool]())
			c.Terms = []CandidateTerm{{Name: "trade_silver", Value: spent}}
			out = append(out, c)
		}
	}
	return out
}

// PlannedTradeNutrition is the nutrition the plan opened to buy from trader.
func PlannedTradeNutrition(plan domain.Fact[FoodPlan], trader string) float64 {
	p, known := plan.Value()
	if !known {
		return 0
	}
	total := 0.0
	for _, e := range p.Portfolio {
		if e.Channel.Kind == CandidateTrade && e.Channel.ID == trader && e.Decision == FoodPlanOpen {
			stock, _ := e.Channel.Nutrition().StockCap.Value()
			total += float64(stock)
		}
	}
	return total
}
