package store

import (
	"os"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/store/clock"
)

// The compaction tests cross the retained-page tail, and CompactHistory only
// runs when a test calls it, so the whole binary runs at a short tail: the
// boundary tests keep their coverage at 16 pages instead of paying for 128
// real transactions each, which ran past the per-test budget under CPU
// contention. Tests read clock.HistoryTail rather than assuming a number.
func TestMain(m *testing.M) {
	clock.HistoryTail = 16
	os.Exit(m.Run())
}
