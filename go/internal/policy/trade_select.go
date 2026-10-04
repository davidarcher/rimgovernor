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

	// Pawn rows only (#1037): what SelectPawnPurchase ranks.
	Skills               []ProfileSkill
	ViolenceCapable      bool
	ViolenceCapableKnown bool

	// ThingID is a non-pawn row's first colony thing (#1194): a packed
	// sculpture's row names its packed item.
	ThingID string
	// HitPoints is a gear row's hit-point fraction and ZoneID the stockpile
	// its thing lies in (#1831); what SaleGear matches.
	HitPoints      float64
	HitPointsKnown bool
	ZoneID         string

	// PawnID is a pawn row's load id (the animal census id for a colony
	// animal): the key a live-animal sale matches (#1632) and a purchase
	// can name (#1636).
	PawnID string
	// PawnGender is a pawn row's gender ("Male"/"Female"), as an animal's
	// census gender: what a purchase of a founder's missing sex matches.
	PawnGender string

	// A prisoner row's sale facts (#1969): guest status, the secure and downed
	// tests, and the extra home / host faction ids a sale angers.
	GuestStatus         string
	PrisonerSecure      bool
	PrisonerSecureKnown bool
	PawnDowned          bool
	PawnDownedKnown     bool
	ExtraHomeFaction    string
	ExtraHostFaction    string
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

	// SaleArt is the packed art the colony may sell (SaleSculptures, by
	// packed item id, #1194); ArtFirst sells it before the targets, when
	// the wealth headroom is negative.
	SaleArt  map[string]bool
	ArtFirst bool

	// SaleAnimals are the colony animals the herd plan lets go (#1632,
	// HerdSaleAnimals), by pawn id, set only while the colony wants silver.
	// Each sells through its own pawn row.
	SaleAnimals map[string]bool

	// SaleGear is the worn-dump gear the colony sells (SaleGear, by thing id,
	// #1831): protected natively, so the accept names these ids.
	SaleGear map[string]bool

	// HerdWants are the animals the herd plan lacks (HerdWants, #1636),
	// best first: the pawn-purchase line buys the first affordable one.
	HerdWants []HerdWant

	// Favor marks a favor-currency session (the tribute collector, #1939):
	// SelectFavorSale decides it, selling gold above FavorKeep.
	Favor     bool
	FavorKeep int64
	// FavorPrisoners are the surplus prisoners (RoutineFacts.SurplusPrisoners,
	// by pawn id) a favor session sells (#1971).
	FavorPrisoners map[string]bool
}

// TradeCurrency is the definition of the sheet's currency row
// (TradeRow.currency): the coin both sides' cash and every price are in. A
// sheet with no known currency row has none.
func TradeCurrency(rows []TradeSheetRowFact) (string, bool) {
	for _, row := range rows {
		if row.CurrencyKnown && row.Currency {
			return row.DefName, true
		}
	}
	return "", false
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
	// Art or animals alone (#1194, #1632) sell without a catalog target.
	if len(p.Targets) > 0 || len(facts.SaleArt)+len(facts.SaleAnimals)+len(facts.SaleGear) == 0 {
		if err := p.Validate(); err != nil {
			return refuse(err.Error())
		}
	} else if p.SilverReserve < 0 {
		return refuse("economic silver reserve out of range")
	}
	if !facts.Complete {
		return refuse("Economic selection requires an unfiltered complete trade sheet")
	}
	currency, hasCurrency := TradeCurrency(facts.Rows)
	if !hasCurrency || !facts.SilverKnown || facts.ColonySilver < 0 || facts.TraderSilver < 0 || facts.MaxSilverSpend < 0 {
		return refuse(tradeBlockerUnknown)
	}
	stopped := make(map[string]bool, len(facts.Stopped))
	for _, item := range facts.Stopped {
		stopped[item] = true
	}
	reserve := max(p.SilverReserve, facts.Floors[currency])
	budgetCap := min(facts.MaxSilverSpend, max(0, facts.ColonySilver-reserve))
	if stopped[currency] {
		budgetCap = 0
	}
	// Budget and the trader's cash are carried as exact running floats, the
	// way Python carries them, because prices are fractional: rounding either
	// to whole silver between lines would let a later line spend money an
	// earlier one had already committed.
	budget, traderCash := float64(budgetCap), float64(facts.TraderSilver)
	out := TradeSelection{SilverReserve: reserve, Budget: budgetCap}
	if facts.ArtFirst {
		sellArt(&out, facts, stopped, &traderCash)
	}
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
	if !facts.ArtFirst {
		sellArt(&out, facts, stopped, &traderCash)
	}
	sellAnimals(&out, facts, stopped, &traderCash)
	sellGear(&out, facts, stopped, &traderCash)
	return out
}

// sellArt is the art-sale step (#1194): each row whose thing is sale art
// sells that one piece, line by line, under the same rules as a surplus
// sale -- a tradeable unprotected row, a known positive price (surplus
// MinSellPrice) and the trader's remaining cash. A line already selected
// is left alone.
func sellArt(out *TradeSelection, facts TradeSelectionFacts, stopped map[string]bool, traderCash *float64) {
	if len(facts.SaleArt) == 0 {
		return
	}
	sellRows(out, facts.Rows, stopped, traderCash, false, false, func(row TradeSheetRowFact) bool {
		return row.ThingID != "" && facts.SaleArt[row.ThingID]
	})
}

// sellAnimals is the live-animal sale step (#1632): each pawn row whose
// pawn id is a sale animal sells that one animal under the same rules. A
// pawn row is always ProtectedExport natively; the herd plan that chose the
// animal is the authorization, so the flag is not read.
func sellAnimals(out *TradeSelection, facts TradeSelectionFacts, stopped map[string]bool, traderCash *float64) {
	if len(facts.SaleAnimals) == 0 {
		return
	}
	sellRows(out, facts.Rows, stopped, traderCash, true, false, func(row TradeSheetRowFact) bool {
		return row.PawnID != "" && facts.SaleAnimals[row.PawnID]
	})
}

// sellGear is the gear-sale step (#1831): each row whose thing is sale gear
// sells that one piece under the same rules. Gear is ProtectedExport natively;
// the accept names the thing ids it authorizes, so the flag is not read.
func sellGear(out *TradeSelection, facts TradeSelectionFacts, stopped map[string]bool, traderCash *float64) {
	if len(facts.SaleGear) == 0 {
		return
	}
	sellRows(out, facts.Rows, stopped, traderCash, false, true, func(row TradeSheetRowFact) bool {
		return row.ThingID != "" && facts.SaleGear[row.ThingID]
	})
}

// SaleGear is the gear the colony sells: a thing lying in a worn dump (wornDumps,
// by zone id) above the hit-point floor the incinerator burns below, so
// serviceable-but-unwanted gear is sold, not burned. The trader's willingness
// to trade a row (biocoded gear is refused natively) is checked at selection.
func SaleGear(rows []TradeSheetRowFact, wornDumps map[string]bool) map[string]bool {
	out := map[string]bool{}
	for _, row := range rows {
		if row.ThingID != "" && !row.Pawn && wornDumps[row.ZoneID] && row.HitPointsKnown && row.HitPoints > domain.GearHitPointFloor {
			out[row.ThingID] = true
		}
	}
	return out
}

// sellRows sells each wanted single-item row; pawns says the rows are pawn
// rows and authorized that a protected row may sell.
func sellRows(out *TradeSelection, rows []TradeSheetRowFact, stopped map[string]bool, traderCash *float64, pawns, authorized bool, wanted func(TradeSheetRowFact) bool) {
	selected := map[string]bool{}
	for _, line := range out.Selected {
		selected[line.LineID] = true
	}
	for _, row := range rows {
		if !wanted(row) || selected[row.LineID] {
			continue
		}
		evidence := TradeSelectionEvidence{Item: row.DefName}
		switch {
		case !row.TraderWillTradeKnown || !row.TraderWillTrade || !row.PawnKnown || row.Pawn != pawns || !row.CurrencyKnown || row.Currency || stopped[row.DefName]:
			evidence.Blocker = tradeBlockerRow
		case !pawns && !authorized && (!row.ProtectedExportKnown || row.ProtectedExport):
			evidence.Blocker = tradeBlockerProtected
		case row.ColonyCount < 1 || !row.SellPriceKnown || !finite(row.SellPrice) || row.SellPrice < 0:
			evidence.Blocker = tradeBlockerUnknown
		}
		if evidence.Blocker != "" {
			out.Evidence = append(out.Evidence, evidence)
			continue
		}
		evidence.Matched, evidence.EligibleStock, evidence.ExportCapacity = true, 1, 1
		if price := row.SellPrice; price >= artMinSellPrice && price <= *traderCash {
			evidence.Count = -1
			*traderCash -= price
			out.Selected = append(out.Selected, TradeSelectionLine{LineID: row.LineID, DefName: row.DefName, Count: -1})
		}
		out.Evidence = append(out.Evidence, evidence)
	}
}

// artMinSellPrice is the surplus sale's MinSellPrice (RoutineTradeTargets):
// any positive price.
const artMinSellPrice = math.SmallestNonzeroFloat64

// TradeReserveFacts is what EconomicReserves folds into economic floors: the
// player's own per-resource reserves and spending restrictions
// (the autopilot's configured production policy) and
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
// domain.Standard carries no target quantity at all (see domain/goal_kind.go's own
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
