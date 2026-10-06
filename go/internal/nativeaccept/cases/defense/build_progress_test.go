package defense

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/store"
)

func tiers(built, pending int) store.DefenseLayoutRecord {
	var r store.DefenseLayoutRecord
	for i := 0; i < built; i++ {
		r.Tiers = append(r.Tiers, store.DefenseTierRecord{Built: true, Attempts: 1})
	}
	for i := 0; i < pending; i++ {
		r.Tiers = append(r.Tiers, store.DefenseTierRecord{})
	}
	return r
}

// A tier whose blueprints are placed is built over game hours with every
// stage already completed, and a raid between two tiers parks the layout with
// no plan open (#2134): the advancing tick is progress either way, bounded by
// layoutBuildTicks after the open plans or the built tiers last changed.
func TestBuildProgressCountsTicksUntilTheLayoutStopsMoving(t *testing.T) {
	var b buildProgress
	open := []string{"plan-perimeter"}
	first := b.signature(open, tiers(1, 3), 1000)
	second := b.signature(open, tiers(1, 3), 5000)
	if first == "" || first == second {
		t.Fatalf("open tier under construction: %q then %q, want advancing", first, second)
	}
	// The tier's plan retired and the next one is not admitted yet (a raid).
	if got := b.signature(nil, tiers(2, 2), 9000); got == "" {
		t.Fatal("a newly built tier restarts the bound")
	}
	if a, c := b.signature(nil, tiers(2, 2), 20000), b.signature(nil, tiers(2, 2), 30000); a == "" || a == c {
		t.Fatalf("no plan open between tiers: %q then %q, want advancing", a, c)
	}
	if got := b.signature(nil, tiers(2, 2), 9000+layoutBuildTicks+1); got != "" {
		t.Fatalf("layout unchanged past its bound: %q, want empty", got)
	}
	if got := b.signature(nil, tiers(3, 1), 9000+layoutBuildTicks+2); got == "" {
		t.Fatal("a newly built tier restarts the bound")
	}
}
