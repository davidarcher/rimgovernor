package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// Art for sale (#1193, epic #1172): while the colony wants silver it lacks,
// MaintainArt's artists sculpt for sale; no raid-headroom gate applies (#1849:
// raid points follow what the colony holds and sold art leaves). "Wants silver" is stock and need combined: a purchase need exists
// (food, medicine, components or a MaintainResource shortfall) and the
// silver on hand is below that need's rough price plus the trade silver
// reserve. The sculpture sells through SelectTrade like any surplus.

// SilverStock is the colony's silver from the resource census; unknown
// while the census or the catalog's currency is.
func SilverStock(items ItemFacts, resources domain.Fact[[]Amount]) domain.Fact[int64] {
	rows, known := resources.Value()
	if !known || items.Currency == "" {
		return domain.Unknown[int64]()
	}
	var n int64
	for _, row := range rows {
		if row.Resource == items.Currency {
			n += row.Count
		}
	}
	return domain.Known(n)
}

// Silver is the colony silver stock (SilverStock over f.Resources).
func (f RoundsFacts) Silver() domain.Fact[int64] { return SilverStock(f.Items, f.Resources) }

// PurchasePrice is the rough silver the need's purchases cost at the game's
// market values (ItemFacts): the cheapest medicine a replenish buys, a
// component, a unit of nutrition as the cheapest raw plant food, and each
// shortfall resource. false when the need has no purchase (a surplus or a
// pawn purchase alone is no silver need); a price the catalog lacks is an
// error.
func (n TradeNeed) PurchasePrice(items ItemFacts) (float64, bool, error) {
	any := n.MedicineReplenish > 0 || n.ComponentShortfall > 0 || n.Food.Nutrition > 0 || len(n.Food.Missing) > 0 || len(n.Shortfall) > 0
	price := 0.0
	if n.MedicineReplenish > 0 {
		_, medicine, err := items.CheapestMedicine()
		if err != nil {
			return 0, false, err
		}
		price += float64(n.MedicineReplenish) * medicine
	}
	if n.ComponentShortfall > 0 {
		component, err := items.MarketValue(ComponentResource)
		if err != nil {
			return 0, false, err
		}
		price += float64(n.ComponentShortfall) * component
	}
	if nutrition := max(0, n.Food.Nutrition) + float64(len(n.Food.Missing))*max(0, n.Food.IngredientNutrition); nutrition > 0 {
		perNutrition, err := items.RawFoodNutritionPrice()
		if err != nil {
			return 0, false, err
		}
		price += nutrition * perNutrition
	}
	for _, row := range n.Shortfall {
		value, err := items.MarketValue(row.Resource)
		if err != nil {
			return 0, false, err
		}
		price += float64(row.Count) * value
	}
	return price, any, nil
}

// SilverShort is the silver runway deficit (#1169): a purchase need exists
// and the silver on hand is below its rough price plus TradeSilverReserve.
// Sale sculptures (#1193) and sale organ harvests (#1169) answer it; unknown
// while any input is.
func SilverShort(items ItemFacts, need domain.Fact[TradeNeed], silver, colonists domain.Fact[int64]) domain.Fact[bool] {
	n, nk := need.Value()
	s, sk := silver.Value()
	reserve, rk := TradeSilverReserve(colonists)
	if !nk || !sk || !rk {
		return domain.Unknown[bool]()
	}
	price, any, err := n.PurchasePrice(items)
	if err != nil {
		return domain.Unknown[bool]()
	}
	return domain.Known(any && float64(s) < price+float64(reserve))
}

// ArtSaleWanted reports whether sale sculpting runs: a known SilverShort.
// Anything unknown wants nothing.
func ArtSaleWanted(items ItemFacts, need domain.Fact[TradeNeed], silver, colonists domain.Fact[int64]) bool {
	return positive(SilverShort(items, need, silver, colonists))
}

// reviewSilverShort is SilverShort over the review's trade need.
func reviewSilverShort(f RoundsFacts, p RoundsPolicy, medicine MedicalReserveReview) domain.Fact[bool] {
	need := ReviewTradeNeed(f.Items.Currency, medicine, f.Resources, f.ResourceNeeds, RoundsTradeFloors(p, nil), f.Wealth, p.Trade, RoundsTradeFood(f, p))
	return SilverShort(f.Items, need, f.Silver(), f.Colonists)
}

// artForSale is ArtSaleWanted over the review's trade need.
func artForSale(f RoundsFacts, p RoundsPolicy, medicine MedicalReserveReview) bool {
	return positive(reviewSilverShort(f, p, medicine))
}

// RoundsSilverShort is the review's silver runway deficit recomputed by a
// planner from its own read: the seasonal policy and the medical reserve
// review with the review's latch, as DetectRounds derives them.
func RoundsSilverShort(f RoundsFacts, p RoundsPolicy, medicineActive bool) domain.Fact[bool] {
	p = p.Seasonal(f.Calendar, f.DisasterConditions)
	facts := f.MedicalReserve
	facts.Colonists = f.Colonists
	medicine, err := ReviewMedicalReserve(facts, medicineActive, p.MedicalReserve)
	if err != nil {
		return domain.Unknown[bool]()
	}
	return reviewSilverShort(f, p, medicine)
}

// RoundsArtForSale is the review's sale decision recomputed by the art
// bill planner from its own read.
func RoundsArtForSale(f RoundsFacts, p RoundsPolicy, medicineActive bool) bool {
	return positive(RoundsSilverShort(f, p, medicineActive))
}

// valuePerWorkTick is the market value a work tick adds
// (StatWorker_MarketValue, 0.0036 silver per tick).
const valuePerWorkTick = 0.0036

// selectSaleSculpture picks the sale sculpture for an artist: among the
// available sizes their skill allows, stocked stuffs with enough for one,
// the best market value per work tick (stuff value plus work, quality
// ignored).
func selectSaleSculpture(available map[string][]BillSelection, artist PawnID, demand ArtDemand) (BillSelection, bool) {
	var best BillSelection
	bestScore := 0.0
	for rank := len(demand.Items.Sculptures) - 1; rank >= 0; rank-- {
		size := demand.Items.Sculptures[rank]
		options := available[size.Recipe]
		if len(options) == 0 || demand.Skill[artist] < sculptureGate(rank).MinSkill {
			continue
		}
		for _, stuff := range demand.Items.StuffsFor(Resource(size.Def)) {
			value, err := demand.Items.MarketValue(stuff)
			if err != nil || demand.Stock[stuff] < size.Cost {
				continue
			}
			work := size.Work
			score := (float64(size.Cost)*value + work*valuePerWorkTick) / work
			if score > bestScore || score == bestScore && best.Recipe == size.Recipe && Resource(best.Ingredients[0]) > stuff {
				best, bestScore = options[0], score
				best.Ingredients = []string{string(stuff)}
			}
		}
	}
	return best, bestScore > 0
}
