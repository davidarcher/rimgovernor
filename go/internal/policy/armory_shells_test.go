package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestMortarShellTargets(t *testing.T) {
	t.Parallel()
	machining := ArmoryAssessment{Threat: ArmoryTierMachining, Research: ArmoryTierMachining, Tier: ArmoryTierMachining}
	fab := ArmoryAssessment{Threat: ArmoryTierFabrication, Research: ArmoryTierFabrication, Tier: ArmoryTierFabrication}
	fabThreatOnly := ArmoryAssessment{Threat: ArmoryTierFabrication, Research: ArmoryTierMachining, Tier: ArmoryTierMachining}
	for _, tc := range []struct {
		name    string
		mortars int
		a       ArmoryAssessment
		want    map[Resource]int64
	}{
		{"no mortar", 0, fab, nil},
		{"unknown threat", 2, ArmoryAssessment{}, nil},
		{"machining one", 1, machining, map[Resource]int64{testShellHE: 10, testShellIncendiary: 5}},
		{"machining two", 2, machining, map[Resource]int64{testShellHE: 20, testShellIncendiary: 10}},
		{"fabrication doubles and adds EMP", 1, fab, map[Resource]int64{testShellHE: 20, testShellIncendiary: 10, testShellEMP: 10}},
		{"mech threat without research", 1, fabThreatOnly, map[Resource]int64{testShellHE: 10, testShellIncendiary: 5, testShellEMP: 5}},
	} {
		got := MortarShellTargets(tc.mortars, tc.a, testShells)
		if len(got) != len(tc.want) {
			t.Fatalf("%s: got %v", tc.name, got)
		}
		for _, a := range got {
			if tc.want[a.Resource] != a.Count {
				t.Fatalf("%s: got %v", tc.name, got)
			}
		}
	}
}

func TestShellsShort(t *testing.T) {
	t.Parallel()
	targets := []Amount{{Resource: testShellHE, Count: 10}}
	if v, k := ShellsShort(nil, domain.Unknown[map[Resource]int64]()).Value(); !k || v {
		t.Fatal("no targets must be known not short")
	}
	if _, k := ShellsShort(targets, domain.Unknown[map[Resource]int64]()).Value(); k {
		t.Fatal("unknown stock must be unknown")
	}
	if v, _ := ShellsShort(targets, domain.Known(map[Resource]int64{testShellHE: 4})).Value(); !v {
		t.Fatal("below half must be short")
	}
	if v, _ := ShellsShort(targets, domain.Known(map[Resource]int64{testShellHE: 5})).Value(); v {
		t.Fatal("at half is not short")
	}
}
