package policy

import (
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The acquisition method catalog: every way the colony can obtain a resource
// is a SupplyCandidate of one kind, ranked by PlanSupply against the demand
// instead of each goal hardcoding its own order (produce before mine, forage
// before hunt).

// AcquisitionLaborPerUnit is each kind's estimated pawn work ticks per unit
// yielded: a planning prior for the kinds whose per-unit work native does
// not report, so a bill and a deposit covering the same deficit compare.
// Measured distance, trips and risk refine a candidate on top of it.
var AcquisitionLaborPerUnit = map[CandidateKind]float64{
	CandidateHarvest:   10,
	CandidateChop:      15,
	CandidateMining:    20,
	CandidateSalvage:   25,
	CandidateLoot:      5,
	CandidateHunt:      30,
	CandidateProduce:   60,
	CandidateDeepDrill: 100,
	CandidateTrade:     0,
}

// acquisitionUnitsPerTrip is one pawn's haul of a stackable resource when
// no carry capacity was observed.
const acquisitionUnitsPerTrip = 75

func catalogLabor(kind CandidateKind, units int64) domain.Fact[float64] {
	return domain.Known(AcquisitionLaborPerUnit[kind] * float64(max(units, 1)))
}

// ResourceDeficitDemand is the one-row demand a MaintainResource floor
// ranks candidates against.
func ResourceDeficitDemand(resource Resource, deficit int64) domain.Fact[[]ResourceDemand] {
	if deficit <= 0 {
		return domain.Known([]ResourceDemand{})
	}
	return domain.Known([]ResourceDemand{{Key: ResourceKey{Def: resource}, Count: deficit, Priority: 1}})
}

// MineCandidates are the catalog rows of selected native mine sources;
// headroom is the storage accepting the output.
func MineCandidates(resource Resource, sources []ResourceSource, headroom domain.Fact[int64]) []SupplyCandidate {
	var out []SupplyCandidate
	for _, s := range sources {
		if s.Method != ResourceSourceMine || s.Yield <= 0 {
			continue
		}
		out = append(out, SourceCandidate(CandidateMining, s.ThingID, catalogLabor(CandidateMining, s.Yield), domain.Known(s.Distance), true, acquisitionUnitsPerTrip,
			SourceYield(ResourceKey{Def: resource}, s.Yield, 0, headroom)))
	}
	return out
}

// ProduceCandidate is the catalog row of a chosen production bill covering
// units: the product drops at the bench, so no haul is charged. Every
// ingredient of the recipe is its upfront resource cost, so the plan prices
// the bill against the stock the colony holds (UsableIngredients).
func ProduceCandidate(m ResourceMethod, units int64) (SupplyCandidate, bool) {
	if m.Kind != ResourceMethodProduce || units <= 0 {
		return SupplyCandidate{}, false
	}
	c := SourceCandidate(CandidateProduce, m.Bench+"/"+m.Recipe, catalogLabor(CandidateProduce, units), domain.Known(0.0), false, 0,
		SourceYield(ResourceKey{Def: m.Resource}, units, 0, domain.Unknown[int64]()))
	for _, in := range m.Ingredients {
		c.UpfrontCost.Resources = append(c.UpfrontCost.Resources, ResourceQuantity{Key: ResourceKey{Def: in.Resource}, Count: in.Count * units})
	}
	return c, true
}

// UsableIngredients is, per supplied ingredient, the stock a bill may spend:
// the usable census (Available), or the runway row's own stock when lower,
// less the protected line: the row's reserve plus its observed use over the
// projection horizon, and nothing for an ingredient without a row or with an
// unread rate. An ingredient whose census is unobserved is left out, so a
// bill that draws on it is unknown. Prospective ore never funds a bill.
func UsableIngredients(runways []ResourceRunway, supply []Stock) []ResourceQuantity {
	var out []ResourceQuantity
	for _, s := range supply {
		have, ok := s.Available.Value()
		if !ok || have < 0 {
			continue
		}
		line := 0.0
		for _, row := range runways {
			if row.Resource != s.Resource {
				continue
			}
			if stock, known := row.Stock.Value(); known && stock >= 0 {
				have = min(have, stock)
			}
			line = float64(max(0, row.Reserve))
			if rate, known := row.ConsumptionPerDay.Value(); known && finite(rate) && rate > 0 {
				line += math.Ceil(rate * ProjectionHorizonDays)
			}
		}
		out = append(out, ResourceQuantity{Key: ResourceKey{Def: s.Resource}, Count: int64(math.Max(0, float64(have)-line))})
	}
	return out
}

// AcquisitionSourceCandidates are the catalog rows of the colony-facts
// acquisition census (trees, wild plants, animals) yielding resource: a
// tree is chop, a hunt is hunt, anything else harvest. A hunt's revenge
// chance is its risk: labor scales by 1+2*chance. Distance is straight
// from home, the colony's reference cell.
func AcquisitionSourceCandidates(resource Resource, sources []AcquisitionSource, home domain.Cell, headroom domain.Fact[int64]) []SupplyCandidate {
	var out []SupplyCandidate
	for _, s := range sources {
		units := int64(math.Round(s.Yield))
		if Resource(s.Resource) != resource || units <= 0 {
			continue
		}
		kind, risk := CandidateHarvest, 0.0
		switch {
		case s.Hunt:
			kind, risk = CandidateHunt, math.Min(1, math.Max(0, s.RevengeChance))
		case s.Tree:
			kind = CandidateChop
		}
		labor, _ := catalogLabor(kind, units).Value()
		out = append(out, SourceCandidate(kind, s.ID, domain.Known(labor*(1+2*risk)), domain.Known(math.Hypot(float64(s.Cell.X-home.X), float64(s.Cell.Z-home.Z))), true, acquisitionUnitsPerTrip,
			SourceYield(ResourceKey{Def: resource}, units, 0, headroom)))
	}
	return out
}

// DeepDrillCandidate is the catalog row of a drill over a deep lump: units
// is the lump's yield toward the deficit, distance from home.
func DeepDrillCandidate(resource Resource, id string, units int64, distance float64, headroom domain.Fact[int64]) (SupplyCandidate, bool) {
	if units <= 0 {
		return SupplyCandidate{}, false
	}
	return SourceCandidate(CandidateDeepDrill, id, catalogLabor(CandidateDeepDrill, units), domain.Known(distance), true, acquisitionUnitsPerTrip,
		SourceYield(ResourceKey{Def: resource}, units, 0, headroom)), true
}

// tradeLaborPerSilver prices a purchase's silver as labor, so a caravan's
// dear goods can lose to a near deposit.
const tradeLaborPerSilver = 1.0

// TradeCandidate is the catalog row of buying units of resource from a caravan
// at price silver each; the goods drop at the colony, so no haul is charged.
// Its ID names the trader and the resource (TradeCandidateID), one row per
// pair.
func TradeCandidate(resource Resource, trader string, units int64, price float64) (SupplyCandidate, bool) {
	if units <= 0 || !(price >= 0) {
		return SupplyCandidate{}, false
	}
	return SourceCandidate(CandidateTrade, TradeCandidateID(trader, resource), domain.Known(price*float64(units)*tradeLaborPerSilver), domain.Known(0.0), false, 0,
		SourceYield(ResourceKey{Def: resource}, units, 0, domain.Unknown[int64]())), true
}

// MaxCatalogSelection bounds one acquisition method, matching
// SelectResourceSources' native selection cap.
const MaxCatalogSelection = 8
