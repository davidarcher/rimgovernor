package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A colony with no general store: the haul goals' deficit outranks the small
// stockpile deficit, so one hauler went to hauling with nowhere to haul on
// every review. The structural edge orders the stockpiles first at equal
// priority; once the store stands the edge is gone.
func TestMissingStoreOrdersStockpilesBeforeHauls(t *testing.T) {
	request := func(review StockpileReview) DevelopmentRequest {
		return DevelopmentRequest{
			Snapshot: domain.GenerationSnapshot{Colony: "colony", Map: 1, Load: "load", Plan: "plan"}, Tick: 100,
			Workers: domain.Known(1),
			Census:  census(worker("a", WorkHauling)),
			Goals: []DevelopmentGoal{
				{ID: MaintainStorage, Source: AutopilotGoal, Priority: 3, Deficit: domain.Known(1.0), Labor: LaborProfile{WorkHauling}},
				{ID: SecureSupplies, Source: AutopilotGoal, Priority: 3, Deficit: domain.Known(.9), Labor: LaborProfile{WorkHauling}},
				{ID: MaintainStockpiles, Source: AutopilotGoal, Priority: 3, Deficit: domain.Known(stockpileDeficit), Labor: LaborProfile{WorkHauling}},
			},
			Dependencies: StockpileDependencies(domain.Known(review)),
		}
	}
	missing := StockpileReview{Known: true, Active: true, Edits: []StockpileEdit{{Kind: StockpileCreate, Role: domain.GeneralRole}}}
	s := rankDep(t, request(missing))
	if row := rowOf(s, MaintainStockpiles); !row.Selected || row.Donation == nil {
		t.Fatalf("stockpiles should take the hauler: %+v", s.Rows)
	}
	grow := StockpileReview{Known: true, Active: true, Edits: []StockpileEdit{{Kind: StockpileGrow, Role: domain.GeneralRole}}}
	s = rankDep(t, request(grow))
	if !rowOf(s, MaintainStorage).Selected || rowOf(s, MaintainStockpiles).Selected {
		t.Fatalf("a standing store keeps score order: %+v", s.Rows)
	}
	// A resource edge still donates only to a strictly more urgent origin.
	r := woodShortage()
	r.Goals[0].Priority = 3
	r.Dependencies = []DevelopmentDependency{shelterWood(domain.Known[int64](0), DependencyCost{"wall-1", 60})}
	if w := rowOf(rankDep(t, r), MaintainResource); w.Donation != nil {
		t.Fatalf("equal-priority resource edge donated: %+v", w)
	}
}
