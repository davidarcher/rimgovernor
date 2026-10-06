package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// CandidateKind names how a candidate obtains its yields: one set covering
// FoodChannelKind and AcquisitionKind, so a kind shared by both (hunt,
// trade) is one value.
type CandidateKind string

const (
	CandidateForage        CandidateKind = "forage"
	CandidateHunt          CandidateKind = "hunt"
	CandidateSlaughter     CandidateKind = "slaughter"
	CandidateCrop          CandidateKind = "crop"
	CandidateAnimalProduct CandidateKind = "animal_product"
	CandidateFishing       CandidateKind = "fishing"
	CandidateTrade         CandidateKind = "trade"
	CandidateTame          CandidateKind = "tame"
	CandidateAnimalBuy     CandidateKind = "animal_buy"
	CandidateCorpse        CandidateKind = "corpse"
	CandidateReserve       CandidateKind = "reserve"
	CandidateCook          CandidateKind = "cook"
	CandidateLoot          CandidateKind = "loot"
	CandidateSalvage       CandidateKind = "salvage"
	CandidateMining        CandidateKind = "mining"
	CandidateProduce       CandidateKind = "produce"
	CandidateDeepDrill     CandidateKind = "deep_drill"
	CandidateChop          CandidateKind = "chop"
	CandidateHarvest       CandidateKind = "harvest"
)

// CandidateState is where a candidate stands in its life: Designated is
// committed but not yet delivering, Delivering is observed delivering, Closed
// is neither. Which states earn credit is the ranker's and ledger's rule,
// never the type's.
type CandidateState string

const (
	CandidateDesignated CandidateState = "designated"
	CandidateDelivering CandidateState = "delivering"
	CandidateClosed     CandidateState = "closed"
)

// CandidateNutrition is the good a food candidate yields.
const CandidateNutrition Resource = "Nutrition"

// CandidateYield is one good a candidate yields. PerDay is the steady rate;
// StockCap bounds the total a finite source holds, so a one-shot source is a
// lead-0 candidate with a stock cap and no rate. Headroom is known destination
// capacity accepting this exact output, in item units.
type CandidateYield struct {
	Good      ResourceKey
	PerDay    domain.Fact[float64]
	StockCap  domain.Fact[int64]
	UnitValue float64
	Headroom  domain.Fact[int64]
}

// CandidateRiskKind names a hazard that discounts a candidate's yield.
type CandidateRiskKind string

const (
	CandidateBlight  CandidateRiskKind = CandidateRiskKind(FoodBlight)
	CandidateFallout CandidateRiskKind = CandidateRiskKind(FoodFallout)
	CandidateFrost   CandidateRiskKind = CandidateRiskKind(FoodFrost)
	CandidatePower   CandidateRiskKind = CandidateRiskKind(FoodPower)
	CandidateRevenge CandidateRiskKind = CandidateRiskKind(FoodRevenge)
)

// CandidateRisk is one hazard's weight in [0,1]; summed risks discount the
// yield, clamped at 100%.
type CandidateRisk struct {
	Kind   CandidateRiskKind
	Weight float64
}

// CandidateTerm is a named number the candidate's builder reports for
// explanations.
type CandidateTerm struct {
	Name  string
	Value float64
}

// CandidateCost is what establishing a candidate costs before it yields:
// labor-equivalent ticks and resources.
type CandidateCost struct {
	LaborTicks domain.Fact[float64]
	Resources  []ResourceQuantity
}

// SupplyCandidate is the one type every supply channel is described by: a food
// channel, a loot or mining source, a bench bill, a trader. Yields are
// independent of other candidates' (a Cook candidate yields the incremental
// nutrition of conversion, not the raw input another candidate counts).
type SupplyCandidate struct {
	Kind  CandidateKind
	ID    string
	State domain.Fact[CandidateState]
	// Yields is the set of goods this candidate delivers (a deer yields
	// nutrition and leather).
	Yields   []CandidateYield
	LeadDays domain.Fact[float64]
	// LaborPerDay is the steady work a delivering candidate takes;
	// UpfrontCost is the work and resources to establish it.
	LaborPerDay domain.Fact[float64]
	UpfrontCost CandidateCost
	Risk        []CandidateRisk
	// PathDistance is native path length; DistanceSquared is the nearest
	// source cell to the colony centre, which ranks equal-lead fishing
	// regions nearest first.
	PathDistance    domain.Fact[float64]
	DistanceSquared domain.Fact[float64]
	// NeedsHaul says the yields must be carried; UnitsPerTrip is the observed
	// carrying capacity. A zero value with NeedsHaul is invalid.
	NeedsHaul    bool
	UnitsPerTrip int64
	Terms        []CandidateTerm
	// Prey is a formation hunt's animals, sorted.
	Prey []string
}

var foodCandidateKinds = map[FoodChannelKind]CandidateKind{
	FoodForage: CandidateForage, FoodHunt: CandidateHunt, FoodSlaughter: CandidateSlaughter, FoodCrop: CandidateCrop,
	FoodAnimalProduct: CandidateAnimalProduct, FoodFishing: CandidateFishing, FoodTrade: CandidateTrade, FoodTame: CandidateTame, FoodAnimalBuy: CandidateAnimalBuy,
	FoodCorpse: CandidateCorpse, FoodReserve: CandidateReserve, FoodCook: CandidateCook,
}

var acquisitionCandidateKinds = map[AcquisitionKind]CandidateKind{
	AcquisitionLoot: CandidateLoot, AcquisitionSalvage: CandidateSalvage, AcquisitionMining: CandidateMining,
	AcquisitionProduce: CandidateProduce, AcquisitionDeepDrill: CandidateDeepDrill, AcquisitionChop: CandidateChop,
	AcquisitionHarvest: CandidateHarvest, AcquisitionHunt: CandidateHunt, AcquisitionTrade: CandidateTrade,
}

// SupplyCandidateOfFood is a food channel as a candidate. An unrecognised kind maps
// to an empty CandidateKind, which FoodChannelOfSupply rejects; SupplyFoodPlan refuses it
// first. Open is Delivering, Designated is Designated, neither is Closed.
func SupplyCandidateOfFood(c FoodChannel) SupplyCandidate {
	out := SupplyCandidate{
		Kind: foodCandidateKinds[c.Kind], ID: c.ID,
		Yields:   append([]CandidateYield{{Good: ResourceKey{Def: CandidateNutrition}, PerDay: c.NutritionPerDay, StockCap: c.StockCap}}, c.Products...),
		LeadDays: c.LeadDays, LaborPerDay: c.WorkPerDay, UpfrontCost: CandidateCost{LaborTicks: c.UpfrontTicks}, DistanceSquared: c.DistanceSquared,
		Prey: append([]string(nil), c.Prey...),
	}
	out.State = c.State()
	for _, r := range c.Risk {
		out.Risk = append(out.Risk, CandidateRisk{Kind: CandidateRiskKind(r.Kind), Weight: r.Weight})
	}
	for _, t := range c.Terms {
		out.Terms = append(out.Terms, CandidateTerm(t))
	}
	return out
}

// State is the channel's standing: Delivering when Open, else Designated when
// committed, else Closed; unknown when Open is.
func (c FoodChannel) State() domain.Fact[CandidateState] {
	open, known := c.Open.Value()
	switch designated, _ := c.Designated.Value(); {
	case !known:
		return domain.Unknown[CandidateState]()
	case open:
		return domain.Known(CandidateDelivering)
	case designated:
		return domain.Known(CandidateDesignated)
	}
	return domain.Known(CandidateClosed)
}

// FoodChannelOfSupply is the food channel of a candidate whose first yield is
// nutrition, the rest its Products; ok is false for any other candidate.
func FoodChannelOfSupply(c SupplyCandidate) (FoodChannel, bool) {
	kind, ok := foodKindOf(c.Kind)
	if !ok || len(c.Yields) == 0 || c.Yields[0].Good != (ResourceKey{Def: CandidateNutrition}) {
		return FoodChannel{}, false
	}
	out := FoodChannel{
		Kind: kind, ID: c.ID, NutritionPerDay: c.Yields[0].PerDay, StockCap: c.Yields[0].StockCap, WorkPerDay: c.LaborPerDay, UpfrontTicks: c.UpfrontCost.LaborTicks, LeadDays: c.LeadDays,
		DistanceSquared: c.DistanceSquared, Prey: append([]string(nil), c.Prey...),
	}
	if len(c.Yields) > 1 {
		out.Products = append([]CandidateYield(nil), c.Yields[1:]...)
	}
	if state, known := c.State.Value(); known {
		out.Open = domain.Known(state == CandidateDelivering)
		if state == CandidateDesignated {
			out.Designated = domain.Known(true)
		}
	}
	for _, r := range c.Risk {
		out.Risk = append(out.Risk, FoodRisk{Kind: FoodRiskKind(r.Kind), Weight: r.Weight})
	}
	for _, t := range c.Terms {
		out.Terms = append(out.Terms, FoodPlanTerm(t))
	}
	return out, true
}

// SupplyCandidateOfAcquisition is an acquisition candidate as a one-shot candidate:
// lead 0, each yield's count its stock cap, the whole source's labor its
// upfront cost and no state beyond Closed (not committed). Hunt revenge risk
// stays inside Labor, as AcquisitionSourceCandidates prices it.
func SupplyCandidateOfAcquisition(c AcquisitionCandidate) SupplyCandidate {
	out := SupplyCandidate{
		Kind: acquisitionCandidateKinds[c.Kind], ID: c.ID, State: domain.Known(CandidateClosed),
		LeadDays: domain.Known(0.0), UpfrontCost: CandidateCost{LaborTicks: c.Labor},
		PathDistance: c.PathDistance, NeedsHaul: c.NeedsHaul, UnitsPerTrip: c.UnitsPerTrip,
	}
	for _, y := range c.Yields {
		out.Yields = append(out.Yields, CandidateYield{Good: y.Key, StockCap: domain.Known(y.Count), UnitValue: y.UnitValue, Headroom: y.Headroom})
	}
	return out
}

func foodKindOf(k CandidateKind) (FoodChannelKind, bool) {
	for food, kind := range foodCandidateKinds {
		if kind == k {
			return food, true
		}
	}
	return "", false
}

func acquisitionKindOf(k CandidateKind) (AcquisitionKind, bool) {
	for acquisition, kind := range acquisitionCandidateKinds {
		if kind == k {
			return acquisition, true
		}
	}
	return "", false
}
