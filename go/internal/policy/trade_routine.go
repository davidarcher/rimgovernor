package policy

import (
	"errors"
	"math"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// TradeWithCaravan is the routine trade goal (#234): while a tradeable
// caravan stands on the map and the colony has something to buy from it
// (the medicine shortfall MaintainMedicalReserves already reports, or a
// component shortfall under RoutineTradePolicy.ComponentTarget) or to sell
// to it (stock above a MaintainResource target), RoutineTradePlanner opens
// one bounded session per caravan and settles it. It is config-only work
// like a configuration push: a negotiator's conversation, not a development
// project, so it holds no development slot.

// ComponentResource is the one component definition the trade goal buys and
// the resource family mines toward under RoutineTradePolicy.ComponentTarget.
const ComponentResource Resource = "ComponentIndustrial"

// MedicineResources are the vanilla medicine definitions a caravan may offer,
// best first; the routine trade buys the cheapest one the trader carries.
var MedicineResources = []Resource{"MedicineUltratech", "MedicineIndustrial", "MedicineHerbal"}

// tradeBuyPriceCeiling bounds a routine purchase's unit price. Vanilla
// medicine and components price under 100 silver; anything past this is a
// sheet the colony should not be buying from.
const tradeBuyPriceCeiling = 250.0

// tradeRoutineMaximumTargets and tradeRoutineMaximumCount are
// domain.TradeEconomicPolicy's own bounds, which the routine targets clamp
// to rather than fail validation over a large surplus.
const (
	tradeRoutineMaximumTargets = 30
	tradeRoutineMaximumCount   = 100000
)

// TraderFacts is one map trader from bridge.ListTraders as the routine
// review reads it: CanTrade is native's own verdict that a session can be
// opened with it now (CanTradeNow, not dismissed, arrived), Travelling
// that its caravan is still walking to its trade spot, GoodsStacks the
// size of what it carries.
type TraderFacts struct {
	ID, Kind, Faction    string
	CanTrade, Travelling bool
	GoodsStacks          int64
}

// RoutineTradePolicy is the operator's routine trade configuration.
// ComponentTarget (zero disables) is the component stock the trade buys
// toward and the resource family mines toward. RetainedMinimum is the stock
// each WealthSurplusResources hoard keeps regardless of floor
// (DefaultTradeRetainedMinimum when nil). The silver reserve is derived from
// the colonist count (TradeSilverReserve) and hoard selling keys on the
// constant TradeItemWealthShare (#875).
type RoutineTradePolicy struct {
	ComponentTarget int64
	RetainedMinimum map[Resource]int64
}

// TradeItemWealthShare is the share of total colony wealth held as items
// past which WealthSurplus sells the raw-material hoards down to their floors.
const TradeItemWealthShare = 0.6

// TradeSilverReserve is the silver a purchase never spends below: 100 per
// colonist, at least 200. An unknown or empty count reports false and the
// routine buys nothing (fail closed, #875).
func TradeSilverReserve(colonists domain.Fact[int64]) (int64, bool) {
	n, known := colonists.Value()
	if !known || n <= 0 {
		return 200, false
	}
	return max(200, min(100*n, 100000)), true
}

func (p RoutineTradePolicy) Validate() error {
	if p.ComponentTarget < 0 || p.ComponentTarget > 1<<31 {
		return errors.New("invalid routine trade policy")
	}
	for _, retained := range p.RetainedMinimum {
		if retained < 0 || retained > 1<<31 {
			return errors.New("invalid routine trade policy: retained minimum")
		}
	}
	return nil
}

// WealthSurplusResources are the raw materials WealthSurplus sells down:
// stockpiled ore and refined metal whose only use at hoard size is raid
// points (#341). Silver is currency and components are bought, never sold,
// so both stay out as they do for the target-driven surplus.
var WealthSurplusResources = []Resource{"Steel", "Plasteel", "Gold", "Uranium", "Jade"}

// DefaultTradeRetainedMinimum is the stock each WealthSurplusResources
// hoard keeps when the policy names no table: enough steel and plasteel
// for repairs and a bench, a token of each precious material.
func DefaultTradeRetainedMinimum() map[Resource]int64 {
	return map[Resource]int64{"Steel": 500, "Plasteel": 100, "Gold": 50, "Uranium": 50, "Jade": 50}
}

// RoutineTradeFloors is the per-definition floor set the routine trade
// review sells against: EconomicReserves over native's outstanding
// construction deficits when the caller has read them (nil otherwise);
// stock targets, which carry the derived resource needs, apply separately.
func RoutineTradeFloors(p RoutinePolicy, construction map[string]int64) map[string]int64 {
	floors, _ := EconomicReserves(domain.TradeEconomicPolicy{}, TradeReserveFacts{Construction: construction})
	return floors
}

// WealthFacts is the colony wealth split native's WealthWatcher reports
// (#395): the market value held as items, buildings and pawns, and their
// total. Items count in full toward storyteller wealth where buildings
// count half, which is why the surplus rule keys on the item share.
type WealthFacts struct{ Items, Buildings, Pawns, Total float64 }

// WealthSurplus is the wealth-driven half of the trade surplus: while the
// item share of total wealth exceeds p.ItemWealthShare, every resource in
// WealthSurplusResources stocked above max(target, floor, retained minimum)
// is a surplus of the difference, so a sale lands exactly on the highest
// floor. Unknown wealth, a zero share, an unmet share or a stock at its
// floor yields nothing; the rows come back in WealthSurplusResources order.
func WealthSurplus(stock []Amount, targets map[Resource]int64, floors map[string]int64, wealth domain.Fact[WealthFacts], p RoutineTradePolicy) []Amount {
	facts, known := wealth.Value()
	if !known || p.Validate() != nil {
		return nil
	}
	if !finite(facts.Items) || !finite(facts.Total) || facts.Total <= 0 || facts.Items < 0 || facts.Items/facts.Total <= TradeItemWealthShare {
		return nil
	}
	retained := p.RetainedMinimum
	if retained == nil {
		retained = DefaultTradeRetainedMinimum()
	}
	counts := map[Resource]int64{}
	for _, row := range stock {
		counts[row.Resource] += row.Count
	}
	var out []Amount
	for _, resource := range WealthSurplusResources {
		keep := max(targets[resource], floors[string(resource)], retained[resource])
		if surplus := counts[resource] - keep; surplus > 0 {
			out = append(out, Amount{Resource: resource, Count: surplus})
		}
	}
	return out
}

// TradeNeed is what the review measured worth trading for: the medicine
// units MaintainMedicalReserves wants, the components short of the target,
// each MaintainResource target's stock above its floor, and each raw-material
// hoard WealthSurplus sells down. Retained is the stock every Surplus row
// keeps after its sale (the target, or the wealth rule's floor).
type TradeNeed struct {
	Food               TradeFoodNeed
	MedicineReplenish  int64
	ComponentShortfall int64
	Surplus            []Amount
	Retained           map[Resource]int64
	// Shortfall is each MaintainResource floor stock is below (#728): a
	// caravan selling it is the catalog's trade method.
	Shortfall []Amount
	// Population is set while the colony can host one more colonist
	// (JoinerCapacity): a caravan offering a slave or prisoner is worth
	// opening for (#1037).
	Population bool
	// SurgeryParts are the restore parts no bench can fabricate (#1168),
	// highest priority first: a trader selling one is worth opening for.
	SurgeryParts []SurgeryPart
	// ShedArt is the shed_art reason (#1247): the count of packed art no
	// owed room reserves (SaleSculptures) while the wealth headroom is
	// known and negative. The selection sells it first (ArtFirst).
	ShedArt int64
}

// ShedArtNeed adds the shed_art reason to the need: known negative wealth
// headroom and unreserved packed art. Anything unknown adds nothing.
func ShedArtNeed(need domain.Fact[TradeNeed], headroom domain.Fact[float64], saleArt domain.Fact[int64]) domain.Fact[TradeNeed] {
	n, nk := need.Value()
	h, hk := headroom.Value()
	art, ak := saleArt.Value()
	if !nk || !hk || !ak || !finite(h) || h >= 0 || art <= 0 {
		return need
	}
	n.ShedArt = art
	return domain.Known(n)
}

func (n TradeNeed) Any() bool {
	return n.ShedArt > 0 || n.Population || len(n.SurgeryParts) > 0 || n.MedicineReplenish > 0 || n.ComponentShortfall > 0 || len(n.Surplus) > 0 || len(n.Shortfall) > 0 || n.Food.Nutrition > 0 || len(n.Food.Missing) > 0
}

// ReviewTradeNeed measures the trade need from the same facts the other
// reviews already produced. It is unknown while the medicine reserve or the
// resource census is unknown: a trade opened on a guessed need is not one.
// An unknown wealth fact only leaves the wealth-driven surplus out. Floors
// are EconomicReserves's per-definition floors (reserves plus construction
// deficits); target-driven surplus rows win over wealth-driven ones for the
// same resource.
// A single optional food context adds the shared ledger's food needs; omitting
// it leaves food purchases and protected crop exports disabled.
func ReviewTradeNeed(medicine MedicalReserveReview, resources domain.Fact[[]Amount], targets map[Resource]int64, floors map[string]int64, wealth domain.Fact[WealthFacts], p RoutineTradePolicy, food ...TradeFoodContext) domain.Fact[TradeNeed] {
	replenish, known := medicine.Replenish.Value()
	rows, rowsKnown := resources.Value()
	if !known || !rowsKnown || p.Validate() != nil {
		return domain.Unknown[TradeNeed]()
	}
	stock := map[Resource]int64{}
	for _, row := range rows {
		stock[row.Resource] += row.Count
	}
	need := TradeNeed{MedicineReplenish: max(0, replenish)}
	if len(food) == 1 {
		need.Food = reviewTradeFood(food[0])
	}
	if p.ComponentTarget > 0 {
		need.ComponentShortfall = max(0, p.ComponentTarget-stock[ComponentResource])
	}
	names := make([]string, 0, len(targets))
	for name := range targets {
		names = append(names, string(name))
	}
	sort.Strings(names)
	for _, name := range names {
		resource := Resource(name)
		if resource == ComponentResource || resource == "Silver" {
			continue
		}
		if short := targets[resource] - stock[resource]; short > 0 {
			need.Shortfall = append(need.Shortfall, Amount{Resource: resource, Count: short})
		}
		keep := max(targets[resource], floors[name])
		if surplus := stock[resource] - keep; surplus > 0 {
			need.Surplus = append(need.Surplus, Amount{Resource: resource, Count: surplus})
			need.retain(resource, keep)
		}
	}
	for _, row := range WealthSurplus(rows, targets, floors, wealth, p) {
		if _, targeted := need.Retained[row.Resource]; targeted {
			continue
		}
		need.Surplus = append(need.Surplus, row)
		need.retain(row.Resource, stock[row.Resource]-row.Count)
	}
	return domain.Known(need)
}

func (n *TradeNeed) retain(resource Resource, count int64) {
	if n.Retained == nil {
		n.Retained = map[Resource]int64{}
	}
	n.Retained[resource] = count
}

// TradeRecovered is TradeWithCaravan's recovered fact: known true when no
// tradeable or arriving caravan is present or nothing is worth trading,
// known false while both hold, unknown while the need is. A caravan still
// travelling counts as present so the goal stands (and the planner keeps
// the clock moving) until it arrives. An unknown trader census (a source
// without bridge.ListTraders) also reads as recovered: there is no caravan
// the planner could open with, so the goal stays off rather than standing
// unknown forever.
func TradeRecovered(traders domain.Fact[[]TraderFacts], need domain.Fact[TradeNeed]) domain.Fact[bool] {
	rows, known := traders.Value()
	if !known {
		return domain.Known(true)
	}
	present := false
	for _, row := range rows {
		present = present || row.CanTrade || row.Travelling
	}
	if !present {
		return domain.Known(true)
	}
	n, known := need.Value()
	if !known {
		return domain.Unknown[bool]()
	}
	return domain.Known(!n.Any())
}

// SelectTrader picks the caravan to open with: tradeable, not already
// settled this epoch, most goods first, id order on ties.
func SelectTrader(traders []TraderFacts, settled map[string]bool) (TraderFacts, bool) {
	var best TraderFacts
	found := false
	for _, row := range traders {
		if !row.CanTrade || settled[row.ID] {
			continue
		}
		if !found || row.GoodsStacks > best.GoodsStacks || row.GoodsStacks == best.GoodsStacks && row.ID < best.ID {
			best, found = row, true
		}
	}
	return best, found
}

// RoutineTradeTargets turns the measured need into SelectTrade's ordered
// targets against one live sheet: food first, medicine (the cheapest definition
// the trader carries), then components, then each surplus sale. Purchases
// are capped at tradeBuyPriceCeiling per unit; sales take any positive
// price, since the alternative is the surplus sitting unsold.
func RoutineTradeTargets(need TradeNeed, rows []TradeSheetRowFact, targets map[Resource]int64, p RoutineTradePolicy, colonists domain.Fact[int64]) domain.TradeEconomicPolicy {
	reserve, buy := TradeSilverReserve(colonists)
	out := routineTradeTargets(need, rows, targets, p)
	out.SilverReserve = reserve
	for i := range out.Targets {
		if !buy {
			out.Targets[i].MaxBuy = 0
		}
	}
	return out
}

func routineTradeTargets(need TradeNeed, rows []TradeSheetRowFact, targets map[Resource]int64, p RoutineTradePolicy) domain.TradeEconomicPolicy {
	var out domain.TradeEconomicPolicy
	// Leave room for medicine and components while prioritizing the food bridge.
	foodTargets := tradeFoodTargets(need.Food, rows)
	if len(foodTargets) > tradeRoutineMaximumTargets-2 {
		foodTargets = foodTargets[:tradeRoutineMaximumTargets-2]
	}
	out.Targets = append(out.Targets, foodTargets...)
	if need.MedicineReplenish > 0 {
		var pick *TradeSheetRowFact
		for i := range rows {
			row := &rows[i]
			medicine := false
			for _, name := range MedicineResources {
				medicine = medicine || string(name) == row.DefName
			}
			if !medicine || row.TraderCount <= 0 || !row.BuyPriceKnown || !finite(row.BuyPrice) || row.BuyPrice <= 0 {
				continue
			}
			if pick == nil || row.BuyPrice < pick.BuyPrice {
				pick = row
			}
		}
		if pick != nil {
			out.Targets = append(out.Targets, domain.TradeTarget{Item: pick.DefName, Stock: min(pick.ColonyCount+need.MedicineReplenish, tradeRoutineMaximumCount), MaxBuy: min(need.MedicineReplenish, tradeRoutineMaximumCount), MaxBuyPrice: tradeBuyPriceCeiling})
		}
	}
	if need.ComponentShortfall > 0 {
		out.Targets = append(out.Targets, domain.TradeTarget{Item: string(ComponentResource), Stock: min(p.ComponentTarget, tradeRoutineMaximumCount), MaxBuy: min(need.ComponentShortfall, tradeRoutineMaximumCount), MaxBuyPrice: tradeBuyPriceCeiling})
	}
	seen := map[string]bool{}
	for _, target := range out.Targets {
		seen[target.Item] = true
	}
	for _, target := range surgeryPartTargets(need.SurgeryParts, rows, seen) {
		if len(out.Targets) < tradeRoutineMaximumTargets {
			seen[target.Item] = true
			out.Targets = append(out.Targets, target)
		}
	}
	for _, short := range need.Shortfall {
		if seen[string(short.Resource)] || len(out.Targets) >= tradeRoutineMaximumTargets {
			continue
		}
		seen[string(short.Resource)] = true
		out.Targets = append(out.Targets, domain.TradeTarget{Item: string(short.Resource), Stock: min(targets[short.Resource], tradeRoutineMaximumCount), MaxBuy: min(short.Count, tradeRoutineMaximumCount), MaxBuyPrice: tradeBuyPriceCeiling})
	}
	for _, surplus := range need.Surplus {
		if seen[string(surplus.Resource)] || len(out.Targets) >= tradeRoutineMaximumTargets {
			continue
		}
		out.Targets = append(out.Targets, domain.TradeTarget{Item: string(surplus.Resource), Stock: min(max(targets[surplus.Resource], need.Retained[surplus.Resource]), tradeRoutineMaximumCount), MaxSell: min(surplus.Count, tradeRoutineMaximumCount), MinSellPrice: math.SmallestNonzeroFloat64})
	}
	return out
}

// surgeryPartTargets buys each part's best item the trader carries at a
// known price, one unit per part, capped by surgeryPartPriceCeiling.
func surgeryPartTargets(parts []SurgeryPart, rows []TradeSheetRowFact, seen map[string]bool) []domain.TradeTarget {
	var out []domain.TradeTarget
	index := map[string]int{}
	for _, part := range parts {
		for _, item := range part.Items {
			var row *TradeSheetRowFact
			for i := range rows {
				r := &rows[i]
				if r.DefName == string(item) && r.TraderCount > 0 && r.BuyPriceKnown && finite(r.BuyPrice) && r.BuyPrice > 0 && r.BuyPrice <= surgeryPartPriceCeiling {
					row = r
					break
				}
			}
			if row == nil {
				continue
			}
			if i, ok := index[row.DefName]; ok {
				out[i].Stock = min(out[i].Stock+1, tradeRoutineMaximumCount)
				out[i].MaxBuy = min(out[i].MaxBuy+1, row.TraderCount)
			} else if !seen[row.DefName] {
				index[row.DefName] = len(out)
				out = append(out, domain.TradeTarget{Item: row.DefName, Stock: min(row.ColonyCount+1, tradeRoutineMaximumCount), MaxBuy: 1, MaxBuyPrice: surgeryPartPriceCeiling})
			}
			break
		}
	}
	return out
}
