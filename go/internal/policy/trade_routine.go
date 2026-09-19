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
// like ProductionPolicy: a negotiator's conversation, not a development
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
// SilverReserve is the silver a purchase never spends below; ComponentTarget
// (zero disables) is the component stock the trade buys toward and the
// resource family mines toward.
type RoutineTradePolicy struct{ SilverReserve, ComponentTarget int64 }

func (p RoutineTradePolicy) Validate() error {
	if p.SilverReserve < 0 || p.ComponentTarget < 0 || p.SilverReserve > 1<<31 || p.ComponentTarget > 1<<31 {
		return errors.New("invalid routine trade policy")
	}
	return nil
}

// TradeNeed is what the review measured worth trading for: the medicine
// units MaintainMedicalReserves wants, the components short of the target,
// and each MaintainResource target's stock above its floor.
type TradeNeed struct {
	MedicineReplenish  int64
	ComponentShortfall int64
	Surplus            []Amount
}

func (n TradeNeed) Any() bool {
	return n.MedicineReplenish > 0 || n.ComponentShortfall > 0 || len(n.Surplus) > 0
}

// ReviewTradeNeed measures the trade need from the same facts the other
// reviews already produced. It is unknown while the medicine reserve or the
// resource census is unknown: a trade opened on a guessed need is not one.
func ReviewTradeNeed(medicine MedicalReserveReview, resources domain.Fact[[]Amount], targets map[Resource]int64, p RoutineTradePolicy) domain.Fact[TradeNeed] {
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
		if surplus := stock[resource] - targets[resource]; surplus > 0 {
			need.Surplus = append(need.Surplus, Amount{Resource: resource, Count: surplus})
		}
	}
	return domain.Known(need)
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
// targets against one live sheet: medicine first (the cheapest definition
// the trader carries), then components, then each surplus sale. Purchases
// are capped at tradeBuyPriceCeiling per unit; sales take any positive
// price, since the alternative is the surplus sitting unsold.
func RoutineTradeTargets(need TradeNeed, rows []TradeSheetRowFact, targets map[Resource]int64, p RoutineTradePolicy) domain.TradeEconomicPolicy {
	out := domain.TradeEconomicPolicy{SilverReserve: p.SilverReserve}
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
	for _, surplus := range need.Surplus {
		if seen[string(surplus.Resource)] || len(out.Targets) >= tradeRoutineMaximumTargets {
			continue
		}
		out.Targets = append(out.Targets, domain.TradeTarget{Item: string(surplus.Resource), Stock: min(targets[surplus.Resource], tradeRoutineMaximumCount), MaxSell: min(surplus.Count, tradeRoutineMaximumCount), MinSellPrice: math.SmallestNonzeroFloat64})
	}
	return out
}
