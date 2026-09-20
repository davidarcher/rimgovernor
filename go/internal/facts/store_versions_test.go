package facts

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
)

// A section's version moves on the evidence that it changed (an
// invalidation, narrowed or whole, a scope change) and not on a refresh
// at cadence (#624).
func TestStoreVersionsMoveOnInvalidationOnly(t *testing.T) {
	t.Parallel()
	s := NewStore()
	scope := Scope{Load: "l", Generation: 1}
	Put(s, scope, Buildings, Held[int]{Value: 1, AsOf: 10, Complete: true})
	Put(s, scope, Bills, Held[int]{Value: 1, AsOf: 10, Complete: true})
	base := s.Versions()
	Put(s, scope, Buildings, Held[int]{Value: 2, AsOf: 20, Complete: true})
	if got := s.Versions(); got["buildings"] != base["buildings"] {
		t.Fatal("a refresh moved the version", got)
	}
	s.Apply(Invalidation{Families: []bridge.FactFamily{bridge.FactColony}, IDs: []string{"b1"}})
	got := s.Versions()
	if got["buildings"] != base["buildings"]+1 || got["bills"] != base["bills"]+1 || got["pawns"] != base["pawns"] {
		t.Fatal("a narrowed invalidation moves the marked sections only", base, got)
	}
	s.InvalidateFamily(bridge.FactPawns)
	if s.Versions()["pawns"] != base["pawns"]+1 {
		t.Fatal("an unheld section of an invalidated family still moves")
	}
	s.InvalidateAll()
	all := s.Versions()
	for _, section := range Sections() {
		if all[string(section)] <= got[string(section)] {
			t.Fatal("a whole invalidation moves every section", section)
		}
	}
	Put(s, Scope{Load: "l2", Generation: 1}, Buildings, Held[int]{Value: 3, AsOf: 5, Complete: true})
	if s.Versions()["research"] <= all["research"] {
		t.Fatal("a scope change moves every section")
	}
	if (*Store)(nil).Versions() != nil {
		t.Fatal("nil store")
	}
}
