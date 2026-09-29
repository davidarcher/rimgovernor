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
		{"machining one", 1, machining, map[Resource]int64{ShellHE: 10, ShellIncendiary: 5}},
		{"machining two", 2, machining, map[Resource]int64{ShellHE: 20, ShellIncendiary: 10}},
		{"fabrication doubles and adds EMP", 1, fab, map[Resource]int64{ShellHE: 20, ShellIncendiary: 10, ShellEMP: 10}},
		{"mech threat without research", 1, fabThreatOnly, map[Resource]int64{ShellHE: 10, ShellIncendiary: 5, ShellEMP: 5}},
	} {
		got := MortarShellTargets(tc.mortars, tc.a)
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
	targets := []Amount{{Resource: ShellHE, Count: 10}}
	if v, k := ShellsShort(nil, domain.Unknown[map[Resource]int64]()).Value(); !k || v {
		t.Fatal("no targets must be known not short")
	}
	if _, k := ShellsShort(targets, domain.Unknown[map[Resource]int64]()).Value(); k {
		t.Fatal("unknown stock must be unknown")
	}
	if v, _ := ShellsShort(targets, domain.Known(map[Resource]int64{ShellHE: 4})).Value(); !v {
		t.Fatal("below half must be short")
	}
	if v, _ := ShellsShort(targets, domain.Known(map[Resource]int64{ShellHE: 5})).Value(); v {
		t.Fatal("at half is not short")
	}
}

func TestSelectShellBill(t *testing.T) {
	t.Parallel()
	recipe := func(def string, p Resource) GearRecipe {
		return GearRecipe{Definition: def, Products: []Resource{p}, Available: domain.Known(true), AvailableOn: domain.Known(true)}
	}
	bench := GearBench{ID: "machining", Bills: domain.Known([]GearBill{}), Recipes: domain.Known([]GearRecipe{recipe("Make_Shell_HighExplosive", ShellHE), recipe("Make_Shell_Incendiary", ShellIncendiary)})}
	targets := MortarShellTargets(1, ArmoryAssessment{Threat: ArmoryTierFabrication, Tier: ArmoryTierMachining})
	if _, ok := SelectShellBill([]GearBench{bench}, nil, nil, nil); ok {
		t.Fatal("no targets must issue no bill")
	}
	got, ok := SelectShellBill([]GearBench{bench}, targets, nil, nil)
	if !ok || got.Recipe != "Make_Shell_HighExplosive" || got.Target != 10 || got.Bench != "machining" {
		t.Fatal("first bill", got, ok)
	}
	bench.Bills = domain.Known([]GearBill{{ID: "b1", Recipe: "Make_Shell_HighExplosive", Products: []Resource{ShellHE}}})
	got, ok = SelectShellBill([]GearBench{bench}, targets, nil, nil)
	if !ok || got.Recipe != "Make_Shell_Incendiary" || got.Target != 5 {
		t.Fatal("second bill", got, ok)
	}
	bench.Bills = domain.Known([]GearBill{{Products: []Resource{ShellHE}}, {Products: []Resource{ShellIncendiary}}})
	if got, ok := SelectShellBill([]GearBench{bench}, targets, nil, nil); ok {
		t.Fatal("EMP has no recipe; nothing to bill", got)
	}
}

// A shell recipe whose steel MaintainResource holds is skipped (#1230).
func TestSelectShellBillLeavesHeldStock(t *testing.T) {
	t.Parallel()
	he := GearRecipe{Definition: "Make_Shell_HighExplosive", Products: []Resource{ShellHE}, Available: domain.Known(true), AvailableOn: domain.Known(true), Ingredients: domain.Known([][]Amount{{{"Steel", 15}}})}
	bench := GearBench{ID: "machining", Bills: domain.Known([]GearBill{}), Recipes: domain.Known([]GearRecipe{he})}
	targets := []Amount{{ShellHE, 10}}
	stock := []Stock{{"Steel", domain.Known(int64(100))}}
	if _, ok := SelectShellBill([]GearBench{bench}, targets, stock, []Amount{{"Steel", 90}}); ok {
		t.Fatal("shell bill spent held steel")
	}
	if _, ok := SelectShellBill([]GearBench{bench}, targets, stock, []Amount{{"Steel", 80}}); !ok {
		t.Fatal("unheld steel did not fund a shell")
	}
	if got := ResourceHolds(map[Resource]int64{"Steel": 5, "Plasteel": 3, "Wood": 0}); len(got) != 2 || got[0].Resource != "Plasteel" {
		t.Fatal(got)
	}
}
