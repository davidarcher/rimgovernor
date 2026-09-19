package policy

import (
	"math"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func finite(v float64) bool { return !math.IsNaN(v) && !math.IsInf(v, 0) }

// TradeSheetRowFact is one live trade sheet row as SelectTrade reads it. Each
// optional native field carries its own Known flag: Python's select_trade
// tests `is not True` / `is not False` precisely so an absent field is never
// silently read as a usable value, and this mirrors that. It deliberately
// mirrors bridge.TradeSheetRow without this package depending on the bridge
// package, the same discipline every other policy fact type here follows.
type TradeSheetRowFact struct {
	Food        domain.Fact[TradeFoodGood]
	LineID      string
	DefName     string
	ColonyCount int64
	TraderCount int64

	BuyPrice       float64
	BuyPriceKnown  bool
	SellPrice      float64
	SellPriceKnown bool

	TraderWillTrade      bool
	TraderWillTradeKnown bool
	Currency             bool
	CurrencyKnown        bool
	Pawn                 bool
	PawnKnown            bool
	ProtectedExport      bool
	ProtectedExportKnown bool
}

// TradeSelectionFacts is everything SelectTrade reads: the complete unfiltered
// sheet, both sides' current silver, the economic floors and production-halted
// definitions EconomicReserves computed, and this request's own net-spend
// ceiling.
//
// Complete is the caller's assertion that the sheet it passed was read whole
// and unfiltered (bridge.ReadTradeSheet returns an error rather than a partial
// sheet, so a successful read is exactly that assertion). SelectTrade refuses
// outright when it is false, matching Python's refusal on a nonzero
// omittedByRowCap/omittedByFilter: an incomplete inventory view is not a
// best-effort approximation of an economic decision, it is unusable for one.
type TradeSelectionFacts struct {
	CropSurplusFloors map[string]int64
	Complete          bool
	Rows              []TradeSheetRowFact
	ColonySilver      int64
	TraderSilver      int64
	SilverKnown       bool
	Floors            map[string]int64
	Stopped           []string
	MaxSilverSpend    int64
}

// TradeSelectionLine is one selected row adjustment: the native line id
// SetTradeLines addresses, the definition it came from, and the signed count
// (positive buys, negative sells) domain.TradeLine carries as its
// AbsoluteCount.
type TradeSelectionLine struct {
	LineID  string
	DefName string
	Count   int64
}

// TradeSelectionEvidence is one target's decision row, mirroring Python's
// evidence dictionaries one for one: either a blocker naming why the target
// was skipped, or the matched quantities that produced its count.
type TradeSelectionEvidence struct {
	Item           string
	Blocker        string
	Matched        bool
	EligibleStock  int64
	RetainedTarget int64
	Count          int64
	ExportCapacity int64
}

// TradeSelection is one whole economic decision: the lines to stage, the
// per-target evidence behind them, and the silver reserve the AcceptTrade
// floors must carry. Refused is set when the facts themselves were unusable,
// in which case Selected and Evidence are empty and Reason explains why --
// distinct from an admissible decision that simply selected nothing.
type TradeSelection struct {
	Refused       bool
	Reason        string
	Selected      []TradeSelectionLine
	Evidence      []TradeSelectionEvidence
	SilverReserve int64
	Budget        int64
}

const (
	tradeBlockerAmbiguous = "Unavailable or ambiguous native definition"
	tradeBlockerRow       = "Ineligible trade row"
	tradeBlockerProtected = "Protected equipment, food, medicine or unknown classification"
	tradeBlockerUnknown   = "Economic stock or price evidence is unknown"
)

// SelectTrade ports trade_policy.py's select_trade: a pure decision over one
// complete trade sheet and the already-computed economic floors. Explicit
// target order sets purchase priority. Sales use only observed eligible
// surplus, capped by the buyer's current cash; purchases never rely on
// anticipated sale proceeds or future production, and spend a budget that is
// fixed before the first line is chosen.
//
// It decides nothing about which trader to open with, whether a deal is worth
// taking, or whether the colony may afford it -- native's own preview remains
// the authority on all three (see trading.py's post-staging checks, ported in
// buildingruntime/trade_economy.go).
func SelectTrade(p domain.TradeEconomicPolicy, facts TradeSelectionFacts) TradeSelection {
	refuse := func(reason string) TradeSelection { return TradeSelection{Refused: true, Reason: reason} }
	if err := p.Validate(); err != nil {
		return refuse(err.Error())
	}
	if !facts.Complete {
		return refuse("Economic selection requires an unfiltered complete trade sheet")
	}
	if !facts.SilverKnown || facts.ColonySilver < 0 || facts.TraderSilver < 0 || facts.MaxSilverSpend < 0 {
		return refuse(tradeBlockerUnknown)
	}
	stopped := make(map[string]bool, len(facts.Stopped))
	for _, item := range facts.Stopped {
		stopped[item] = true
	}
	reserve := max(p.SilverReserve, facts.Floors["Silver"])
	budgetCap := min(facts.MaxSilverSpend, max(0, facts.ColonySilver-reserve))
	if stopped["Silver"] {
		budgetCap = 0
	}
	// Budget and the trader's cash are carried as exact running floats, the
	// way Python carries them, because prices are fractional: rounding either
	// to whole silver between lines would let a later line spend money an
	// earlier one had already committed.
	budget, traderCash := float64(budgetCap), float64(facts.TraderSilver)
	out := TradeSelection{SilverReserve: reserve, Budget: budgetCap}
	for _, target := range p.Targets {
		var row *TradeSheetRowFact
		ambiguous := false
		for i := range facts.Rows {
			if facts.Rows[i].DefName != target.Item {
				continue
			}
			if row != nil {
				ambiguous = true
				break
			}
			row = &facts.Rows[i]
		}
		if row == nil || ambiguous {
			out.Evidence = append(out.Evidence, TradeSelectionEvidence{Item: target.Item, Blocker: tradeBlockerAmbiguous})
			continue
		}
		// Classification comes from ThingDef, never localized names or model
		// guesses; an unknown flag is never read as a permissive default.
		if !row.TraderWillTradeKnown || !row.TraderWillTrade || !row.PawnKnown || row.Pawn || !row.CurrencyKnown || row.Currency {
			out.Evidence = append(out.Evidence, TradeSelectionEvidence{Item: target.Item, Blocker: tradeBlockerRow})
			continue
		}
		if row.ColonyCount < 0 || row.TraderCount < 0 {
			out.Evidence = append(out.Evidence, TradeSelectionEvidence{Item: target.Item, Blocker: tradeBlockerUnknown})
			continue
		}
		stock, supply := row.ColonyCount, row.TraderCount
		floor := max(target.Stock, facts.Floors[target.Item])
		cropFloor, cropAuthorized := facts.CropSurplusFloors[target.Item]
		food, foodKnown := row.Food.Value()
		cropAuthorized = cropAuthorized && cropFloor > 0 && foodKnown && validTradeFood(food) && food.Crop
		if cropAuthorized {
			floor = max(floor, cropFloor)
		}
		count := int64(0)
		switch {
		case stock < floor && target.MaxBuy > 0:
			if !row.BuyPriceKnown || !finite(row.BuyPrice) || row.BuyPrice < 0 {
				out.Evidence = append(out.Evidence, TradeSelectionEvidence{Item: target.Item, Blocker: tradeBlockerUnknown})
				continue
			}
			if price := row.BuyPrice; price > 0 && price <= target.MaxBuyPrice {
				count = min(floor-stock, supply, target.MaxBuy, int64(math.Floor(budget/price)))
				if count < 0 {
					count = 0
				}
				budget -= float64(count) * price
			}
		case stock > floor && target.MaxSell > 0 && !stopped[target.Item]:
			if cropAuthorized && row.ProtectedExport {
				cropAuthorized = selectedProteinPurchase(out.Selected, facts.Rows)
			}
			if !row.ProtectedExportKnown || row.ProtectedExport && !cropAuthorized {
				out.Evidence = append(out.Evidence, TradeSelectionEvidence{Item: target.Item, Blocker: tradeBlockerProtected})
				continue
			}
			if !row.SellPriceKnown || !finite(row.SellPrice) || row.SellPrice < 0 {
				out.Evidence = append(out.Evidence, TradeSelectionEvidence{Item: target.Item, Blocker: tradeBlockerUnknown})
				continue
			}
			if price := row.SellPrice; price > 0 && price >= target.MinSellPrice {
				count = -min(stock-floor, target.MaxSell, int64(math.Floor(traderCash/price)))
				if count > 0 {
					count = 0
				}
				traderCash += float64(count) * price
			}
		}
		out.Evidence = append(out.Evidence, TradeSelectionEvidence{
			Item: target.Item, Matched: true, EligibleStock: stock, RetainedTarget: floor,
			Count: count, ExportCapacity: max(0, stock-floor),
		})
		if count != 0 {
			out.Selected = append(out.Selected, TradeSelectionLine{LineID: row.LineID, DefName: row.DefName, Count: count})
		}
	}
	return out
}

// TradeReserveFacts is what EconomicReserves folds into economic floors: the
// player's own per-resource reserves and spending restrictions
// (store.ResourcePolicies, Python's plan.control['resource_policy']) and
// native's outstanding construction commitments
// (bridge.ReadConstructionDeficits, Python's buildings['resourceDeficit']).
type TradeReserveFacts struct {
	Reserves     map[string]int64
	Restricted   []string
	Construction map[string]int64
}

// EconomicReserves ports trade_policy.py's economic_reserves: the per-def
// stock a trade must never sell below, and the production-halted defs a trade
// must never sell at all.
//
// Two of Python's four floor sources are ported verbatim -- the resource
// policy's own reserves, and each economic target's own stock level raised
// over them -- and so is the construction-commitment addition. Python's fourth
// source, an active colony goal's target quantity, is deliberately omitted:
// domain.Goal carries no target quantity at all (see domain/goal_kind.go's own
// doc comment on why MaintainResource's resource/quantity is not stored in
// Go), so there is no such figure to read rather than invent. The two ported
// sources are the ones a player actually sets for this purpose.
//
// Python's production_budgets also folds in each in-flight plan step's own
// reserved build costs; Go's equivalent commitment is native's live
// blueprint/frame deficit census, which this reads directly instead of
// re-deriving a parallel cost ledger.
func EconomicReserves(p domain.TradeEconomicPolicy, facts TradeReserveFacts) (map[string]int64, []string) {
	bases := map[string]int64{}
	floors := map[string]int64{}
	for resource, reserve := range facts.Reserves {
		if reserve <= 0 {
			continue
		}
		bases[resource], floors[resource] = reserve, reserve
	}
	targets := map[string]int64{}
	for resource, reserve := range bases {
		targets[resource] = reserve
	}
	for _, target := range p.Targets {
		targets[target.Item] = max(targets[target.Item], target.Stock)
	}
	for resource, target := range targets {
		floors[resource] = floors[resource] + target - bases[resource]
	}
	for resource, needed := range facts.Construction {
		if needed <= 0 {
			continue
		}
		floors[resource] += needed
	}
	for resource, floor := range floors {
		if floor <= 0 {
			delete(floors, resource)
		}
	}
	stopped := append([]string(nil), facts.Restricted...)
	sort.Strings(stopped)
	return floors, stopped
}
