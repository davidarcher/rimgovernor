package policy

import (
	goflag "flag"
	"os"
	"testing"
)

// TestMain sites fresh plans from 24 seeds instead of the production 1000:
// each seed grows a full plan, so 1000 makes single tests take minutes.
// Benchmarks keep the production count.
func TestMain(m *testing.M) {
	goflag.Parse()
	if f := goflag.Lookup("test.bench"); f == nil || f.Value.String() == "" {
		siteCandidates = 24
	}
	os.Exit(m.Run())
}
