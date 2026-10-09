package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// DerivedDemand is the one resource-demand value a review derives from its
// facts. The detectors read it during the review and the planners read the
// same value afterwards (architecture rule 7).
type DerivedDemand struct {
	// Needs is every stock level some source asks for, per resource: the
	// targets MaintainResource and the trade stock against.
	Needs map[Resource]int64 `json:",omitempty"`
	// Works is the demand of the colony's own works alone (construction, open
	// bills, clothing, fuel, animal feed), without the evidence needs and the
	// runway targets: stock a chop or a mine provides.
	Works map[Resource]int64 `json:"-"`
	// Serves maps the stuffs that may serve a clothing demand to the stuff it
	// names (ClothingRunway).
	Serves map[Resource]Resource `json:"-"`
	// Retained is the stock a sale keeps of each resource (TradeRetained):
	// the runways' protected lines plus Works. Unknown while the consumption
	// is.
	Retained domain.Fact[map[Resource]int64] `json:"-"`
}

// ResourceDemandOf derives the review's resource demand from its facts, in
// one merge: the evidence needs other goals recorded (f.ResourceNeeds), then
// construction (with the wood latch's floor), open bills, clothing, fuel,
// animal feed and the runways' targets, each raising a level and never
// lowering one. A new demand source is one entry here.
func ResourceDemandOf(f RoundsFacts, p RoundsPolicy, l RoundsLatches) DerivedDemand {
	clothing := f.ClothingRunway()
	works := ConstructionDemandOf(f, p, l)
	for _, source := range []map[Resource]int64{
		OpenBillDemand(f.OpenBills, StockReader{f.Resources, f.Wood}),
		clothing.Needs,
		f.FuelRunway().Needs,
		f.AnimalFeedRunway().Needs,
	} {
		works = ResourceConcernTargets(works, source)
	}
	needs := ResourceConcernTargets(ResourceConcernTargets(f.ResourceNeeds, works), ResourceRunwayTargets(f.ResourceRunways))
	return DerivedDemand{Needs: needs, Works: works, Serves: clothing.Serves, Retained: TradeRetained(f.ResourceConsumption, f.ResourceRunways, works)}
}

// ResourceConcernTargets merges two stock-floor maps by maximum: derived
// raises a level of base, never lowers it, and a non-positive or invalid
// resource is left out. It returns base itself when derived is empty.
func ResourceConcernTargets(base, derived map[Resource]int64) map[Resource]int64 {
	if len(derived) == 0 {
		return base
	}
	out := make(map[Resource]int64, len(base)+len(derived))
	for r, v := range base {
		out[r] = v
	}
	for r, v := range derived {
		if v > 0 && v > out[r] && validResource(r) {
			out[r] = v
		}
	}
	return out
}
