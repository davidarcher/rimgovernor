package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// The silver gap MaintainTrade answers with export orders: a purchase need the
// colony's silver, after TradeSilverReserve, does not cover. Sold goods leave
// colony wealth, so raid headroom does not gate this work. SelectTrade sells
// the goods as surplus.

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

// SilverGap is the silver the colony lacks: the need's rough purchase price
// plus TradeSilverReserve less the silver on hand, zero when the need has no
// purchase or the silver covers it. Unknown while any input is.
func SilverGap(items ItemFacts, need domain.Fact[TradeNeed], silver, colonists domain.Fact[int64]) domain.Fact[float64] {
	n, nk := need.Value()
	s, sk := silver.Value()
	reserve, rk := TradeSilverReserve(colonists)
	if !nk || !sk || !rk {
		return domain.Unknown[float64]()
	}
	price, any, err := n.PurchasePrice(items)
	if err != nil {
		return domain.Unknown[float64]()
	}
	if !any {
		return domain.Known(0.0)
	}
	return domain.Known(max(0, price+float64(reserve)-float64(s)))
}

// SilverShort is the silver runway deficit: a purchase need exists and the
// silver on hand is below its rough price plus TradeSilverReserve (a positive
// SilverGap). MaintainTrade's exports and sale organ harvests answer it;
// unknown while any input is.
func SilverShort(items ItemFacts, need domain.Fact[TradeNeed], silver, colonists domain.Fact[int64]) domain.Fact[bool] {
	gap, known := SilverGap(items, need, silver, colonists).Value()
	if !known {
		return domain.Unknown[bool]()
	}
	return domain.Known(gap > 0)
}

// reviewTradeNeed is the review's purchase need as the silver decisions read it.
func reviewTradeNeed(f RoundsFacts, p RoundsPolicy, medicine MedicalReserveReview, demand DerivedDemand) domain.Fact[TradeNeed] {
	return ReviewTradeNeed(f.Items, medicine, f.Resources, demand.Needs, RoundsTradeFloors(p, nil), f.Wealth, demand.Retained, RoundsTradeFood(f, p))
}

// reviewSilverGap is SilverGap over the review's trade need.
func reviewSilverGap(f RoundsFacts, p RoundsPolicy, medicine MedicalReserveReview, demand DerivedDemand) domain.Fact[float64] {
	return SilverGap(f.Items, reviewTradeNeed(f, p, medicine, demand), f.Silver(), f.Colonists)
}

// reviewSilverShort is SilverShort over the review's trade need.
func reviewSilverShort(f RoundsFacts, p RoundsPolicy, medicine MedicalReserveReview, demand DerivedDemand) domain.Fact[bool] {
	return SilverShort(f.Items, reviewTradeNeed(f, p, medicine, demand), f.Silver(), f.Colonists)
}

// roundsMedicineReview is the seasonal policy and the medical reserve review a
// planner recomputes from its own read, with the review's latch, as
// DetectRounds derives them.
func roundsMedicineReview(f RoundsFacts, p RoundsPolicy, medicineActive bool) (RoundsPolicy, MedicalReserveReview, error) {
	p = p.Seasonal(f.Calendar, f.DisasterConditions)
	facts := f.MedicalReserve
	facts.Colonists = f.Colonists
	medicine, err := ReviewMedicalReserve(facts, medicineActive, p.MedicalReserve)
	return p, medicine, err
}

// RoundsSilverShort is the review's silver runway deficit recomputed by a
// planner from its own read.
func RoundsSilverShort(f RoundsFacts, p RoundsPolicy, medicineActive bool) domain.Fact[bool] {
	p, medicine, err := roundsMedicineReview(f, p, medicineActive)
	if err != nil {
		return domain.Unknown[bool]()
	}
	return reviewSilverShort(f, p, medicine, ResourceDemandOf(f, p, RoundsLatches{}))
}

// RoundsSilverGap is the review's silver gap recomputed by a planner from its
// own read, with the review's derived resource demand beside it.
func RoundsSilverGap(f RoundsFacts, p RoundsPolicy, medicineActive bool) (domain.Fact[float64], DerivedDemand) {
	p, medicine, err := roundsMedicineReview(f, p, medicineActive)
	if err != nil {
		return domain.Unknown[float64](), DerivedDemand{}
	}
	demand := ResourceDemandOf(f, p, RoundsLatches{})
	return reviewSilverGap(f, p, medicine, demand), demand
}

// valuePerWorkTick is the market value a work tick adds
// (StatWorker_MarketValue, 0.0036 silver per tick).
const valuePerWorkTick = 0.0036
