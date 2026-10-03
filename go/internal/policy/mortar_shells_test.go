package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A load with no shell def of the aimed kind fires what the mortar holds
// instead of naming a shell (#1723).
func TestMortarFiresLoadedWhenTheKindHasNoShell(t *testing.T) {
	view := mortarView()
	view.Shells = MortarShells{}
	view.Mortars[0].Loaded = "Shell_Modded"
	view.Structures = view.Structures[:1]
	for i := range view.Positional {
		view.Positional[i].LordJobClass = domain.Known("LordJob_AssaultColony")
	}
	orders, _ := decideStop(t, view, StopEvent{}, CombatMemory{})
	if got := mortarOrders(orders); len(got) != 1 || got[0].Shell != "Shell_Modded" {
		t.Fatalf("%+v", got)
	}
}

// The Core mortar shells the planning tests name; bridge
// TestMortarShellsFromDamageRows derives them from the rows.
const (
	testShellHE         = "Shell_HighExplosive"
	testShellIncendiary = "Shell_Incendiary"
	testShellEMP        = "Shell_EMP"
)

var testShells = MortarShells{HE: testShellHE, Incendiary: testShellIncendiary, EMP: testShellEMP}

// A load with no shell of a kind stocks none of it.
func TestMortarShellTargetsSkipUnknownKinds(t *testing.T) {
	fab := ArmoryAssessment{Threat: ArmoryTierFabrication, Research: ArmoryTierFabrication, Tier: ArmoryTierFabrication}
	got := MortarShellTargets(1, fab, MortarShells{HE: testShellHE})
	if len(got) != 1 || got[0].Resource != testShellHE {
		t.Fatalf("targets = %v", got)
	}
	if got := MortarShellTargets(1, fab, MortarShells{}); len(got) != 0 {
		t.Fatalf("targets = %v", got)
	}
}
