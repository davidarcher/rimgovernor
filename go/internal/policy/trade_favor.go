package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The Royalty tribute collector pays royal favor (honor) instead of silver
// (#1939): a caravan of TraderKind TributeCollectorKind opens a favor-currency
// session (bridge.TradeSheetRead.FavorCurrency), where every row's sell price
// is the item's favor value and the trader's count is a placeholder, never a
// silver budget. The bot sells gold to it and nothing else; prisoners are #1942.

// TributeCollectorKind is the trader kind of the Empire's tribute collector.
const TributeCollectorKind = "Empire_Caravan_TributeCollector"

// FavorSaleResource is the one resource sold for favor.
const FavorSaleResource Resource = "Gold"

// IsTributeCollector reports whether a trader is the tribute collector.
func IsTributeCollector(t TraderFacts) bool { return t.Kind == TributeCollectorKind }

// FavorGoldKeep is the gold a favor sale never sells below: the largest of
// the resource target, the economic floor (construction commitments) and the
// retained minimum the wealth-surplus rule already keeps.
func FavorGoldKeep(targets map[Resource]int64, floors map[string]int64, p RoutineTradePolicy) int64 {
	retained := p.RetainedMinimum
	if retained == nil {
		retained = DefaultTradeRetainedMinimum()
	}
	return max(targets[FavorSaleResource], floors[string(FavorSaleResource)], retained[FavorSaleResource])
}

// FavorGoldNeed adds the favor-sale reason to the need: a tribute collector
// is present or arriving and the known gold stock exceeds FavorGoldKeep.
// Unknown traders, stock or need add nothing.
func FavorGoldNeed(need domain.Fact[TradeNeed], traders domain.Fact[[]TraderFacts], resources domain.Fact[[]Amount], targets map[Resource]int64, floors map[string]int64, p RoutineTradePolicy) domain.Fact[TradeNeed] {
	n, nk := need.Value()
	rows, tk := traders.Value()
	stock, sk := resources.Value()
	if !nk || !tk || !sk || p.Validate() != nil {
		return need
	}
	present := false
	for _, row := range rows {
		present = present || IsTributeCollector(row) && (row.CanTrade || row.Travelling)
	}
	if !present {
		return need
	}
	gold := int64(0)
	for _, row := range stock {
		if row.Resource == FavorSaleResource {
			gold += row.Count
		}
	}
	if surplus := gold - FavorGoldKeep(targets, floors, p); surplus > 0 {
		n.FavorGold = surplus
		return domain.Known(n)
	}
	return need
}

// SelectFavorSale is SelectTrade for a favor session: it sells the gold row's
// stock above keep and buys nothing. There is no silver budget and no
// trader-cash cap; the favor price must be known and positive, the row
// tradeable and the stock known. Anything else selects nothing (not a
// refusal) so the session is cancelled.
func SelectFavorSale(facts TradeSelectionFacts, keep int64) TradeSelection {
	if !facts.Complete {
		return TradeSelection{Refused: true, Reason: "Economic selection requires an unfiltered complete trade sheet"}
	}
	var out TradeSelection
	for _, row := range facts.Rows {
		if row.DefName != string(FavorSaleResource) {
			continue
		}
		evidence := TradeSelectionEvidence{Item: row.DefName}
		switch {
		case !row.TraderWillTradeKnown || !row.TraderWillTrade || !row.PawnKnown || row.Pawn || !row.CurrencyKnown || row.Currency:
			evidence.Blocker = tradeBlockerRow
		case !row.ProtectedExportKnown || row.ProtectedExport:
			evidence.Blocker = tradeBlockerProtected
		case row.ColonyCount < 0 || !row.SellPriceKnown || !finite(row.SellPrice) || row.SellPrice <= 0:
			evidence.Blocker = tradeBlockerUnknown
		}
		if evidence.Blocker == "" {
			sell := row.ColonyCount - keep
			evidence.Matched, evidence.EligibleStock, evidence.RetainedTarget, evidence.ExportCapacity = true, row.ColonyCount, keep, max(0, sell)
			if sell > 0 {
				evidence.Count = -sell
				out.Selected = append(out.Selected, TradeSelectionLine{LineID: row.LineID, DefName: row.DefName, Count: -sell})
			}
		}
		out.Evidence = append(out.Evidence, evidence)
	}
	return out
}
