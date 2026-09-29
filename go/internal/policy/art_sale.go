package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// Art for sale (#1193, epic #1172): while the defense affords more wealth
// and the colony wants silver it lacks, MaintainArt's artists sculpt for
// sale. "Wants silver" is stock and need combined: a purchase need exists
// (food, medicine, components or a MaintainResource shortfall) and the
// silver on hand is below that need's rough price plus the trade silver
// reserve. The sculpture sells through SelectTrade like any surplus.

// SilverStock is the colony's silver from the resource census; unknown
// while the census is.
func SilverStock(resources domain.Fact[[]Amount]) domain.Fact[int64] {
	rows, known := resources.Value()
	if !known {
		return domain.Unknown[int64]()
	}
	var n int64
	for _, row := range rows {
		if row.Resource == "Silver" {
			n += row.Count
		}
	}
	return domain.Known(n)
}

// Silver is the colony silver stock (SilverStock over f.Resources).
func (f RoutineFacts) Silver() domain.Fact[int64] { return SilverStock(f.Resources) }

// Rough purchase prices in silver (vanilla base market values): industrial
// medicine, a component, and a unit of nutrition as raw food (rice, 1.1
// silver per 0.05 nutrition). A shortfall resource is priced at its stuff
// market value, 1 when unlisted.
const (
	roughMedicinePrice  = 18
	roughComponentPrice = 32
	roughNutritionPrice = 22
)

// PurchasePrice is the rough silver the need's purchases cost, false when
// it has none (a surplus or a pawn purchase alone is no silver need).
func (n TradeNeed) PurchasePrice() (float64, bool) {
	price := float64(n.MedicineReplenish*roughMedicinePrice + n.ComponentShortfall*roughComponentPrice)
	price += (max(0, n.Food.Nutrition) + float64(len(n.Food.Missing))*max(0, n.Food.IngredientNutrition)) * roughNutritionPrice
	for _, row := range n.Shortfall {
		value := 1.0
		if f, ok := bedStuffFactors[row.Resource]; ok {
			value = f.Value
		}
		price += float64(row.Count) * value
	}
	any := n.MedicineReplenish > 0 || n.ComponentShortfall > 0 || n.Food.Nutrition > 0 || len(n.Food.Missing) > 0 || len(n.Shortfall) > 0
	return price, any
}

// ArtSaleWanted reports whether sale sculpting runs: known positive wealth
// headroom, a purchase need, and silver below its rough price plus
// TradeSilverReserve. Anything unknown wants nothing.
func ArtSaleWanted(headroom domain.Fact[float64], need domain.Fact[TradeNeed], silver, colonists domain.Fact[int64]) bool {
	h, hk := headroom.Value()
	n, nk := need.Value()
	s, sk := silver.Value()
	reserve, rk := TradeSilverReserve(colonists)
	if !hk || !nk || !sk || !rk || !finite(h) || h <= 0 {
		return false
	}
	price, any := n.PurchasePrice()
	return any && float64(s) < price+float64(reserve)
}

// artForSale is ArtSaleWanted over the review's trade need.
func artForSale(f RoutineFacts, p RoutinePolicy, medicine MedicalReserveReview) bool {
	need := ReviewTradeNeed(medicine, f.Resources, p.ResourceTargets, RoutineTradeFloors(p, nil), f.Wealth, p.Trade, RoutineTradeFood(f, p))
	return ArtSaleWanted(f.WealthBudget(), need, f.Silver(), f.Colonists)
}

// RoutineArtForSale is the review's sale decision recomputed by the art
// bill planner from its own read: the seasonal policy and the medical
// reserve review with the review's latch, as DetectRoutine derives them.
func RoutineArtForSale(f RoutineFacts, p RoutinePolicy, medicineActive bool) bool {
	p = p.Seasonal(f.Calendar, f.DisasterConditions)
	facts := f.MedicalReserve
	facts.Colonists = f.Colonists
	medicine, err := ReviewMedicalReserve(facts, medicineActive, p.MedicalReserve)
	if err != nil {
		return false
	}
	return artForSale(f, p, medicine)
}

// Sculpture WorkToMake (vanilla Buildings_Art.xml) and the market value a
// work tick adds (StatWorker_MarketValue, 0.0036 silver per tick).
var sculptureWork = map[string]float64{"Make_SculptureGrand": 105000, "Make_SculptureLarge": 30000, SculptureRecipe: 18000}

const valuePerWorkTick = 0.0036

// selectSaleSculpture picks the sale sculpture for an artist: among the
// available sizes their skill allows, stocked stuffs with enough for one,
// the best market value per work tick (stuff value plus work, quality
// ignored).
func selectSaleSculpture(available map[string][]BillSelection, artist PawnID, demand ArtDemand) (BillSelection, bool) {
	var best BillSelection
	bestScore := 0.0
	for _, size := range sculptureSizes {
		options := available[size.Recipe]
		if len(options) == 0 || demand.Skill[artist] < size.MinSkill {
			continue
		}
		for stuff, f := range bedStuffFactors {
			if demand.Stock[stuff] < size.Cost {
				continue
			}
			work := sculptureWork[size.Recipe]
			score := (float64(size.Cost)*f.Value + work*valuePerWorkTick) / work
			if score > bestScore || score == bestScore && best.Recipe == size.Recipe && Resource(best.Ingredients[0]) > stuff {
				best, bestScore = options[0], score
				best.Ingredients = []string{string(stuff)}
			}
		}
	}
	return best, bestScore > 0
}
