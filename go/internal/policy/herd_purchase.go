package policy

import (
	"math"
	"slices"
)

// HerdWant is a race the herd plan lacks and the sexes it wants: a target
// with no animals wants either, a founder its missing sex.
type HerdWant struct {
	Race         Resource
	Male, Female bool
}

// HerdWants lists what the plan lacks, best first: each job's target
// race in job order, then each founder's missing sex by race. A race is
// listed once.
func HerdWants(plan HerdPlan) []HerdWant {
	var out []HerdWant
	listed := map[Resource]bool{}
	add := func(race Resource) {
		role, ok := plan.Roles[race]
		if !ok || listed[race] || !role.WantMale && !role.WantFemale {
			return
		}
		listed[race] = true
		out = append(out, HerdWant{Race: race, Male: role.WantMale, Female: role.WantFemale})
	}
	for _, job := range herdWorkJobs {
		if jp, ok := plan.Jobs[job]; ok && jp.Target != "" {
			add(jp.Target)
		}
	}
	founders := make([]Resource, 0, len(plan.Roles))
	for race, role := range plan.Roles {
		if role.Founder {
			founders = append(founders, race)
		}
	}
	slices.Sort(founders)
	for _, race := range founders {
		add(race)
	}
	return out
}

// HerdOffers are the catalog races a trader's pawn rows offer; the plan
// counts them as obtainable.
func HerdOffers(rows []TradeSheetRowFact, races AnimalRaceCatalog) []Resource {
	var out []Resource
	for _, row := range rows {
		race := Resource(row.DefName)
		if _, ok := races.Race(race); ok && row.PawnKnown && row.Pawn && row.TraderCount >= 1 && !slices.Contains(out, race) {
			out = append(out, race)
		}
	}
	slices.Sort(out)
	return out
}

// SelectAnimalPurchase picks the live animal a trader offers that the herd
// plan wants: a pawn row the trader holds and will trade, with none
// on the colony side, of the first want that has an affordable row of its
// race and a sex it wants. The budget is SelectPawnPurchase's:
// min(PawnPurchaseFraction of colony silver, colony silver - reserve -
// spent). Cheapest row, then line id, within a want. It returns false when
// nothing is wanted or affordable.
func SelectAnimalPurchase(wants []HerdWant, rows []TradeSheetRowFact, colonySilver, reserve int64, selected []TradeSelectionLine) (TradeSelectionLine, bool) {
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
	for _, want := range wants {
		var best *TradeSheetRowFact
		for i := range rows {
			row := &rows[i]
			if !row.PawnKnown || !row.Pawn || !row.TraderWillTradeKnown || !row.TraderWillTrade || row.TraderCount < 1 || row.ColonyCount != 0 || Resource(row.DefName) != want.Race {
				continue
			}
			if !(want.Male && row.PawnGender == "Male" || want.Female && row.PawnGender == "Female") {
				continue
			}
			if !row.BuyPriceKnown || !finite(row.BuyPrice) || row.BuyPrice <= 0 || row.BuyPrice > budget {
				continue
			}
			if best == nil || row.BuyPrice < best.BuyPrice || row.BuyPrice == best.BuyPrice && row.LineID < best.LineID {
				best = row
			}
		}
		if best != nil {
			return TradeSelectionLine{LineID: best.LineID, DefName: best.DefName, Count: 1}, true
		}
	}
	return TradeSelectionLine{}, false
}
