package policy

import (
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// PawnPurchaseFraction is the share of the colony's current silver one pawn
// purchase may spend.
const PawnPurchaseFraction = 0.5

// PawnPurchaseValue scores an offered pawn from its skills and passions: each
// usable skill's level weighted by how fast its passion grows it. A disabled
// skill adds nothing.
func PawnPurchaseValue(skills []ProfileSkill) float64 {
	value := 0.0
	for _, skill := range skills {
		if !skill.Disabled {
			value += float64(skill.Level) * (1 + skill.LearnFactor(TraitEffects{}))
		}
	}
	return value
}

// SelectPawnPurchase picks the slave or prisoner a caravan offers that the
// colony buys: only while capacity (JoinerCapacity) is known true,
// only a violence-capable pawn with readable skills, at most
// min(PawnPurchaseFraction of colony silver, colony silver - reserve - spent)
// where spent is what the resource lines already selected will cost, and
// among those the best skill value per silver (line id on ties). It returns
// false when no pawn qualifies.
func SelectPawnPurchase(capacity bool, rows []TradeSheetRowFact, colonySilver, reserve int64, selected []TradeSelectionLine) (TradeSelectionLine, bool) {
	if !capacity {
		return TradeSelectionLine{}, false
	}
	price := map[string]float64{}
	for _, row := range rows {
		price[row.LineID] = row.BuyPrice
	}
	spent := 0.0
	for _, line := range selected {
		if line.Count > 0 {
			spent += float64(line.Count) * price[line.LineID]
		}
	}
	budget := math.Min(PawnPurchaseFraction*float64(colonySilver), float64(colonySilver-reserve)-spent)
	var best TradeSelectionLine
	bestScore, found := 0.0, false
	for _, row := range rows {
		if !row.PawnKnown || !row.Pawn || !row.TraderWillTradeKnown || !row.TraderWillTrade || row.TraderCount < 1 || row.ColonyCount != 0 {
			continue
		}
		if !row.ViolenceCapableKnown || !row.ViolenceCapable || len(row.Skills) == 0 {
			continue
		}
		if !row.BuyPriceKnown || !finite(row.BuyPrice) || row.BuyPrice <= 0 || row.BuyPrice > budget {
			continue
		}
		score := PawnPurchaseValue(row.Skills) / row.BuyPrice
		if !found || score > bestScore || score == bestScore && row.LineID < best.LineID {
			best, bestScore, found = TradeSelectionLine{LineID: row.LineID, DefName: row.DefName, Count: 1}, score, true
		}
	}
	return best, found
}

// PopulationTradeNeed adds the pawn-purchase need to a measured trade need:
// set only while capacity is known true, so an unknown capacity neither
// opens a caravan nor leaves the rest of the need unknown.
func PopulationTradeNeed(need domain.Fact[TradeNeed], capacity domain.Fact[bool]) domain.Fact[TradeNeed] {
	n, known := need.Value()
	room, roomKnown := capacity.Value()
	if !known || !roomKnown || !room {
		return need
	}
	n.Population = true
	return domain.Known(n)
}
