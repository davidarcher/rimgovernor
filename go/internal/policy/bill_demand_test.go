package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A placed bill whose stock falls short raises its ingredient shortfall at
// the cheapest filtered member of each slot, scaled by the batch count; a
// funded bill raises nothing, and the demand merges with construction's.
func TestOpenBillDemandRaisesShortfall(t *testing.T) {
	slots := domain.Known([][]Amount{{{"Steel", 60}, {"Plasteel", 40}}, {{"ComponentIndustrial", 2}}})
	bill := OpenBill{Recipe: "Make_Apparel_FlakVest", Count: 2, Slots: slots}
	stock := StockReader{Resources: census(Amount{"Steel", 100}, Amount{"Plasteel", 10}, Amount{"ComponentIndustrial", 9})}
	// Unfiltered: plasteel (40) is the cheapest member; 80 needed, 10 held.
	got := OpenBillDemand([]OpenBill{bill}, stock)
	if want := map[Resource]int64{"Plasteel": 80}; !reflect.DeepEqual(got, want) {
		t.Fatalf("unfiltered = %v, want %v", got, want)
	}
	// The loadout's stuff is the only member the filter allows.
	bill.Filter = []string{"ComponentIndustrial", "Steel"}
	got = OpenBillDemand([]OpenBill{bill}, stock)
	if want := map[Resource]int64{"Steel": 120}; !reflect.DeepEqual(got, want) {
		t.Fatalf("filtered = %v, want %v", got, want)
	}
	if merged := ResourceConcernTargets(map[Resource]int64{"Steel": 25}, got); merged["Steel"] != 120 {
		t.Fatalf("merged = %v", merged)
	}
	stock = StockReader{Resources: census(Amount{"Steel", 120}, Amount{"ComponentIndustrial", 4})}
	if got = OpenBillDemand([]OpenBill{bill}, stock); got != nil {
		t.Fatalf("funded bill = %v", got)
	}
}

// Bills share their stock: two bills for one resource are summed before the
// shortfall is judged. Unknown slots and an unknown census demand nothing.
func TestOpenBillDemandSumsBillsAndKeepsUnknownUnknown(t *testing.T) {
	slots := domain.Known([][]Amount{{{"Steel", 50}}})
	bills := []OpenBill{{Recipe: "a", Count: 1, Slots: slots}, {Recipe: "b", Count: 1, Slots: slots}, {Recipe: "c", Count: 1, Slots: domain.Unknown[[][]Amount]()}}
	stock := StockReader{Resources: census(Amount{"Steel", 80})}
	if got := OpenBillDemand(bills, stock); !reflect.DeepEqual(got, map[Resource]int64{"Steel": 100}) {
		t.Fatalf("summed = %v", got)
	}
	if got := OpenBillDemand(bills, StockReader{}); got != nil {
		t.Fatalf("unknown census = %v", got)
	}
}
