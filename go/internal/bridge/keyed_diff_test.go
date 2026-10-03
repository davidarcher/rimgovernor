package bridge

import (
	"fmt"
	"math/rand"
	"testing"
)

// TestTableChangedMatchesContent: Changed reports exactly the rows that
// differ between two versions, and no more than the edit touched.
func TestTableChangedMatchesContent(t *testing.T) {
	rng := rand.New(rand.NewSource(2))
	var prev Table[*int]
	for i := 0; i < 2000; i++ {
		v := 0
		prev = prev.Set(fmt.Sprintf("k%d", i), &v)
	}
	for round := 0; round < 40; round++ {
		next := prev
		wantChanged, wantRemoved := map[string]bool{}, map[string]bool{}
		for e := 0; e < 1+rng.Intn(30); e++ {
			k := fmt.Sprintf("k%d", rng.Intn(2300))
			if rng.Intn(3) == 0 {
				if next.Has(k) {
					next = next.Delete(k)
					wantRemoved[k], wantChanged[k] = true, false
				}
				continue
			}
			v := round*1000 + e
			next = next.Set(k, &v)
			wantChanged[k], wantRemoved[k] = true, false
		}
		gotChanged, gotRemoved := map[string]bool{}, map[string]bool{}
		next.Changed(prev, func(id string, _ *int) { gotChanged[id] = true }, func(id string) { gotRemoved[id] = true })
		for k, w := range wantChanged {
			// A key deleted and set again is both removed and changed in
			// the edit history; the diff is of content, so only the final
			// state counts.
			if w != gotChanged[k] {
				t.Fatalf("round %d: %s changed=%v, want %v", round, k, gotChanged[k], w)
			}
		}
		for k, w := range wantRemoved {
			if w != gotRemoved[k] {
				t.Fatalf("round %d: %s removed=%v, want %v", round, k, gotRemoved[k], w)
			}
		}
		if len(gotChanged) > 40 || len(gotRemoved) > 40 {
			t.Fatalf("round %d: %d changed, %d removed reported for a small edit", round, len(gotChanged), len(gotRemoved))
		}
		prev = next
	}
}
