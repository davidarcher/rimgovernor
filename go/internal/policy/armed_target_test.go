package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The armed-fighter count scales with colonists through one helper, with both
// call sites (the Foothold exit and the basic-defense deficit) agreeing.
func TestArmedTargetScalesWithColonists(t *testing.T) {
	p := DefaultRoundsPolicy()
	for _, tc := range []struct {
		colonists, armed int64
		met              bool
		deficit          float64
	}{
		{1, 0, false, 1}, {1, 1, true, 0},
		{2, 1, false, .5}, {2, 2, true, 0},
		{5, 1, false, .5}, {5, 2, true, 0},
	} {
		f := RoundsFacts{Colonists: domain.Known(tc.colonists), Armed: domain.Known(tc.armed)}
		if v, k := footholdArmed(f).Value(); !k || v != tc.met {
			t.Fatalf("colonists=%d armed=%d: footholdArmed=%v,%v", tc.colonists, tc.armed, v, k)
		}
		if v, k := RoundsDeficit(EnsureBasicDefense, f, p).Value(); !k || v != tc.deficit {
			t.Fatalf("colonists=%d armed=%d: deficit=%v,%v", tc.colonists, tc.armed, v, k)
		}
	}
}
