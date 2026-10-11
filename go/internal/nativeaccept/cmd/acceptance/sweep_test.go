package main

import (
	"io"
	"slices"
	"testing"
)

func TestSweepSeeds(t *testing.T) {
	if got, err := sweepSeeds(3, ""); err != nil || !slices.Equal(got, []int{1, 2, 3}) {
		t.Errorf("default seeds %v, %v", got, err)
	}
	if got, err := sweepSeeds(20, "5, 2"); err != nil || !slices.Equal(got, []int{5, 2}) {
		t.Errorf("explicit seeds %v, %v", got, err)
	}
	for _, bad := range []struct {
		n    int
		list string
	}{{0, ""}, {3, "0"}, {3, "x"}, {3, "-1"}} {
		if _, err := sweepSeeds(bad.n, bad.list); err == nil {
			t.Errorf("sweepSeeds(%d, %q) must be refused", bad.n, bad.list)
		}
	}
}

func TestParseSweepRefusals(t *testing.T) {
	for _, args := range [][]string{
		{},
		{"lab-choke"},
		{"lab-choke", "-root", "/r", "-output", "/o", "-rimgovernor", "/b", "-workers", "3"},
	} {
		if _, err := parseSweep(args, io.Discard); err == nil {
			t.Errorf("parseSweep(%v) must be refused", args)
		}
	}
}
