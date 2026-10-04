package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The Royalty tribute collector pays royal favor (honor) instead of silver
// (#1939): a caravan of TraderKind TributeCollectorKind opens a favor-currency
// session (bridge.TradeSheetRead.FavorCurrency), where every row's sell price
// is the item's favor value and the trader's count is a placeholder, never a
// silver budget. The bot sells it surplus gold and surplus prisoners (#1971).

// TributeCollectorKind is the trader kind of the Empire's tribute collector.
const TributeCollectorKind = "Empire_Caravan_TributeCollector"

// FavorSaleResource is the one resource sold for favor.
const FavorSaleResource Resource = "Gold"

// IsTributeCollector reports whether a trader is the tribute collector.
func IsTributeCollector(t TraderFacts) bool { return t.Kind == TributeCollectorKind }

// FavorGoldKeep is the gold a favor sale never sells below: the largest of
// the resource target, the economic floor (construction commitments) and the
// retained minimum the wealth-surplus rule already keeps.
func FavorGoldKeep(targets map[Resource]int64, floors map[string]int64, p RoundsTradePolicy) int64 {
	retained := p.RetainedMinimum
	if retained == nil {
		retained = DefaultTradeRetainedMinimum()
	}
	return max(targets[FavorSaleResource], floors[string(FavorSaleResource)], retained[FavorSaleResource])
}

// FavorGoldNeed adds the favor-sale reason to the need: a tribute collector
// is present or arriving and the known gold stock exceeds FavorGoldKeep.
// Unknown traders, stock or need add nothing.
func FavorGoldNeed(need domain.Fact[TradeNeed], traders domain.Fact[[]TraderFacts], resources domain.Fact[[]Amount], targets map[Resource]int64, floors map[string]int64, p RoundsTradePolicy) domain.Fact[TradeNeed] {
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

// SoldPrisonerEvent is the history event selling a prisoner raises; the
// precept rule answers it (#1971).
const SoldPrisonerEvent = "SoldPrisoner"

// FavorPrisonersHeld reports that the ideoligion does not plainly allow
// selling a prisoner: disapproved (a mood cost or refusal), or unread with
// Ideology installed. Without the expansion, or when precepts only approve,
// the sale proceeds.
func FavorPrisonersHeld(ideology IdeologyRead) bool {
	switch ideology.ActionStance(PreceptAction{HistoryEvent: SoldPrisonerEvent}, PreceptSubject{}).Stance {
	case PreceptAllowed, PreceptApproved:
		return false
	}
	return true
}

// SurplusPrisoners are the prisoners (by pawn id) the colony can sell to the
// tribute collector: living, not recruitable-wanted (HarvestEligible), no
// creepjoiner, no queued surgery or execution, not one MaintainPopulation
// would enslave and not one the organ harvest or part recovery would cut
// (judged row by row against the surgery planner's needs). Empty while any
// needed fact is unknown or the precepts do not plainly allow the sale.
func (f RoundsFacts) SurplusPrisoners(silverShort bool) map[string]bool {
	rows, rk := f.Prisoners.Value()
	colony, ck := f.PrisonerColony.Value()
	if _, mk := f.MedicalPawns.Value(); !rk || !ck || !mk || FavorPrisonersHeld(f.IdeologyRead()) {
		return nil
	}
	stock := map[Resource]int64{}
	resources, _ := f.Resources.Value()
	for _, row := range resources {
		stock[row.Resource] += row.Count
	}
	wants := SelectSurgery(f.MedicalPawns, nil, f.SurgeryContext()).Wants
	needs := OrganNeeds(f.MedicalPawns, wants, silverShort, stock)
	parts := PartRecoveryNeeds(f.MedicalPawns, wants)
	out := map[string]bool{}
	for _, row := range rows {
		if !surplusPrisoner(row, colony) {
			continue
		}
		one, col := domain.Known([]PrisonerFacts{row}), domain.Known(colony)
		_, harvest := SelectOrganHarvest(one, col, needs, nil)
		_, recovery := SelectPartRecovery(one, col, parts, nil)
		if !harvest && !recovery {
			out[string(row.Pawn)] = true
		}
	}
	return out
}

func surplusPrisoner(row PrisonerFacts, colony PrisonerColony) bool {
	dead, dk := row.Dead.Value()
	prisoner, pk := row.Prisoner.Value()
	eligible, ek := row.HarvestEligible(colony).Value()
	joiner, jk := row.CreepJoiner.Value()
	queued, qk := row.QueuedSurgeries.Value()
	prospect, _ := row.Prospect.Value()
	if !dk || dead || !pk || !prisoner || !ek || !eligible || !jk || joiner || !qk || queued > 0 || row.Executing {
		return false
	}
	return !(colony.SlaveryAllowed() && !row.WildMan && canLabor(prospect))
}

// FavorPrisonerNeed adds the prisoner-sale reason to the need: a tribute
// collector is present or arriving and some prisoner is surplus.
func FavorPrisonerNeed(need domain.Fact[TradeNeed], traders domain.Fact[[]TraderFacts], surplus map[string]bool) domain.Fact[TradeNeed] {
	n, nk := need.Value()
	rows, tk := traders.Value()
	if !nk || !tk || len(surplus) == 0 {
		return need
	}
	for _, row := range rows {
		if IsTributeCollector(row) && (row.CanTrade || row.Travelling) {
			n.FavorPrisoners = int64(len(surplus))
			return domain.Known(n)
		}
	}
	return need
}

// favorPrisonerRow reports a prisoner row the favor session may sell: a surplus
// prisoner, secure and not downed, holding no extra home or host faction (a
// sale would anger it), and a known positive favor price. An unknown flag
// holds.
func favorPrisonerRow(facts TradeSelectionFacts, row TradeSheetRowFact) bool {
	return row.PawnKnown && row.Pawn && row.PawnID != "" && facts.FavorPrisoners[row.PawnID] &&
		row.GuestStatus == "Prisoner" && row.PrisonerSecureKnown && row.PrisonerSecure && row.PawnDownedKnown && !row.PawnDowned &&
		row.ExtraHomeFaction == "" && row.ExtraHostFaction == "" &&
		row.TraderWillTradeKnown && row.TraderWillTrade && row.CurrencyKnown && !row.Currency && row.ProtectedExportKnown && !row.ProtectedExport &&
		row.ColonyCount >= 1 && row.SellPriceKnown && finite(row.SellPrice) && row.SellPrice > 0
}

// SelectFavorSale is SelectTrade for a favor session: it sells the gold row's
// stock above keep and each surplus prisoner (facts.FavorPrisoners), and buys
// nothing. There is no silver budget and no trader-cash cap; the favor price
// must be known and positive, the row tradeable and the stock known. Anything
// else selects nothing (not a refusal) so the session is cancelled. A staged
// prisoner's evidence is matched with a zero retained target: AcceptTrade
// needs a floor entry for the row's definition.
func SelectFavorSale(facts TradeSelectionFacts, keep int64) TradeSelection {
	if !facts.Complete {
		return TradeSelection{Refused: true, Reason: "Economic selection requires an unfiltered complete trade sheet"}
	}
	var out TradeSelection
	for _, row := range facts.Rows {
		if favorPrisonerRow(facts, row) {
			out.Evidence = append(out.Evidence, TradeSelectionEvidence{Item: row.DefName, Matched: true, EligibleStock: 1, ExportCapacity: 1, Count: -1})
			out.Selected = append(out.Selected, TradeSelectionLine{LineID: row.LineID, DefName: row.DefName, Count: -1})
			continue
		}
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
