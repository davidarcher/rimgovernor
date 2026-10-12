package main

import (
	"io"
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/combatsim/calibrate"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/combatlab"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
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

// A -variant names a non-default combatlab variant and hands its tuning to the
// service as the env override; the default and unknown names are refused.
func TestSweepVariantEnv(t *testing.T) {
	for _, v := range combatlab.Variants() {
		raw, err := variantEnv(v.Name)
		if v.Name == calibrate.DefaultVariant {
			if err == nil {
				t.Errorf("variant %s is the baseline and must be refused", v.Name)
			}
			continue
		}
		if err != nil {
			t.Errorf("variant %s: %v", v.Name, err)
			continue
		}
		got, err := policy.ParseCombatTuning(raw)
		if err != nil || got == nil || *got != v.Tuning {
			t.Errorf("variant %s env %q parses to %v, %v; want %+v", v.Name, raw, got, err, v.Tuning)
		}
	}
	if _, err := variantEnv("no-such-variant"); err == nil {
		t.Error("an unknown variant must be refused")
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
