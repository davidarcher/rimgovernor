package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// CandidateKind names how a candidate obtains its yields.
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
	CandidateBlight  CandidateRiskKind = "Blight"
	CandidateFallout CandidateRiskKind = "Fallout"
	CandidateFrost   CandidateRiskKind = "Frost"
	CandidateRevenge CandidateRiskKind = "Revenge"
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
	// Source names the ledger counter group that counts the candidate's
	// deliveries ("crop:<zone>", "fish:<x,z>", "forage:<def>",
	// "animal_product:<race>"); empty for one the ledger does not count.
	Source string
}

// FoodCandidate starts a food candidate: nutrition is its first yield, at
// perDay with no known stock bound. The rest of the yields are its products.
func FoodCandidate(kind CandidateKind, id string, perDay domain.Fact[float64]) SupplyCandidate {
	return SupplyCandidate{Kind: kind, ID: id, Yields: []CandidateYield{{Good: NutritionKey, PerDay: perDay, StockCap: domain.Unknown[int64]()}}}
}

// FoodState is a food candidate's standing: Delivering when open, else
// Designated when committed, else Closed; unknown when open is.
func FoodState(open, designated domain.Fact[bool]) domain.Fact[CandidateState] {
	isOpen, known := open.Value()
	switch committed, _ := designated.Value(); {
	case !known:
		return domain.Unknown[CandidateState]()
	case isOpen:
		return domain.Known(CandidateDelivering)
	case committed:
		return domain.Known(CandidateDesignated)
	}
	return domain.Known(CandidateClosed)
}

// Open is whether the candidate is observed delivering; unknown when its
// state is.
func (c SupplyCandidate) Open() domain.Fact[bool] {
	state, known := c.State.Value()
	if !known {
		return domain.Unknown[bool]()
	}
	return domain.Known(state == CandidateDelivering)
}

// Nutrition is the candidate's nutrition yield: the first yield when it is
// nutrition, else the zero (unknown) yield.
func (c SupplyCandidate) Nutrition() CandidateYield {
	if len(c.Yields) > 0 && c.Yields[0].Good == NutritionKey {
		return c.Yields[0]
	}
	return CandidateYield{}
}

// WithNutritionPerDay is c with its nutrition rate replaced; the yields are
// copied, so a shared candidate is never edited in place.
func (c SupplyCandidate) WithNutritionPerDay(perDay domain.Fact[float64]) SupplyCandidate {
	if len(c.Yields) == 0 || c.Yields[0].Good != NutritionKey {
		return c
	}
	c.Yields = append([]CandidateYield(nil), c.Yields...)
	c.Yields[0].PerDay = perDay
	return c
}

// Products are the goods a food candidate yields beside its nutrition (a
// hunted deer's leather).
func (c SupplyCandidate) Products() []CandidateYield {
	if len(c.Yields) > 1 && c.Yields[0].Good == NutritionKey {
		return c.Yields[1:]
	}
	return nil
}

// SourceYield is count units of a finite source's output: lead-0 stock with
// no rate.
func SourceYield(key ResourceKey, count int64, unitValue float64, headroom domain.Fact[int64]) CandidateYield {
	return CandidateYield{Good: key, StockCap: domain.Known(count), UnitValue: unitValue, Headroom: headroom}
}

// SourceCandidate is a one-shot source (loot, salvage, a deposit, a bill, a
// trader): lead 0, the whole source's labor its upfront cost and no state
// beyond Closed (not committed). Hunt revenge risk stays inside labor.
func SourceCandidate(kind CandidateKind, id string, labor, path domain.Fact[float64], haul bool, unitsPerTrip int64, yields ...CandidateYield) SupplyCandidate {
	return SupplyCandidate{
		Kind: kind, ID: id, State: domain.Known(CandidateClosed), Yields: yields,
		LeadDays: domain.Known(0.0), UpfrontCost: CandidateCost{LaborTicks: labor},
		PathDistance: path, NeedsHaul: haul, UnitsPerTrip: unitsPerTrip,
	}
}
