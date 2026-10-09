package policy

import (
	"fmt"
	"testing"
)

// A routine with more shortfalls than the old thirty-target bound and a count
// past the old 100000 bound stages every target at its full count; nothing is
// truncated or clamped before the selector's own budget.
func TestRoundsTradeTargetsAreNotTruncatedOrClamped(t *testing.T) {
	var need TradeNeed
	targets := map[Resource]int64{}
	for i := range 40 {
		r := Resource(fmt.Sprintf("Item%d", i))
		need.Shortfall = append(need.Shortfall, Amount{Resource: r, Count: 250000})
		targets[r] = 300000
	}
	out := roundsTradeTargets(ItemFacts{}, need, nil, targets)
	if len(out.Targets) != 40 {
		t.Fatalf("targets %d, want 40", len(out.Targets))
	}
	if got := out.Targets[39]; got.MaxBuy != 250000 || got.Stock != 300000 {
		t.Fatalf("target %+v clamped", got)
	}
}
