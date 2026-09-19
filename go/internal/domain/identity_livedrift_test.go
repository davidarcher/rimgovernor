package domain

import "testing"

func TestFreshForWidensByLiveDrift(t *testing.T) {
	t.Cleanup(func() { SetLiveDrift(0) })
	if Tick(1000).FreshFor(100) {
		t.Fatal("ahead by 900 must not be fresh without drift")
	}
	SetLiveDrift(1000)
	if !Tick(1000).FreshFor(100) || !Tick(100).Covers(1000) {
		t.Fatal("drift must widen the tolerance both ways")
	}
	if Tick(1400).FreshFor(100) || Tick(50).FreshFor(100) {
		t.Fatal("drift widens the tolerance, never the rewind rule")
	}
	SetLiveDrift(-5)
	if LiveDrift() != 0 {
		t.Fatalf("negative drift must clear, got %d", LiveDrift())
	}
}
