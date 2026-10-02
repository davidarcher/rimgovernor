package snapshot

import (
	"slices"
	"testing"
)

// A recording job never holds its submitter: Later returns while the first
// job is still running, the jobs run in submission order, and Flush returns
// only after all of them.
func TestLaterRunsInOrderWithoutBlockingSubmitter(t *testing.T) {
	release := make(chan struct{})
	var ran []int
	Later(func() { <-release; ran = append(ran, 1) })
	Later(func() { ran = append(ran, 2) })
	Later(func() { ran = append(ran, 3) })
	close(release)
	Flush()
	if !slices.Equal(ran, []int{1, 2, 3}) {
		t.Fatalf("jobs ran %v, want [1 2 3]", ran)
	}
	Flush()
}
