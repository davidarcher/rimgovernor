package defense

import "testing"

// A tier whose blueprints are placed is built over game hours with every
// stage already completed: the advancing tick is its progress, bounded by
// layoutBuildTicks, and nothing open is no progress at all.
func TestBuildProgressCountsTicksWhileATierIsOpen(t *testing.T) {
	var b buildProgress
	if got := b.signature(nil, 100); got != "" {
		t.Fatalf("no open tier: %q, want empty", got)
	}
	open := []string{"plan-perimeter"}
	first := b.signature(open, 1000)
	second := b.signature(open, 5000)
	if first == "" || first == second {
		t.Fatalf("open tier under construction: %q then %q, want advancing", first, second)
	}
	if got := b.signature(open, 1000+layoutBuildTicks+1); got != "" {
		t.Fatalf("open tier past its build bound: %q, want empty", got)
	}
	if got := b.signature([]string{"plan-next"}, 1000+layoutBuildTicks+2); got == "" {
		t.Fatal("a newly opened tier restarts the bound")
	}
}
