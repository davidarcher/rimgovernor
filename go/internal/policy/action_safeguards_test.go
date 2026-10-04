package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestUnsafeLootSafeguardRefusesOnlyAllowingUnsafe(t *testing.T) {
	cell := domain.Cell{X: 1, Z: 2}
	allow, _ := domain.NewSupplyAllow("fire", "Steel", cell)
	forbid, _ := domain.NewSupplyForbid("fire", "Steel", cell)
	other, _ := domain.NewSupplyAllow("safe", "Steel", cell)
	c := SafeguardContext{Enabled: true, Unsafe: UnsafeLoot([]LootItem{
		{Supply: StartingSupply{Thing: "fire"}, SafetyKnown: true},
		{Supply: StartingSupply{Thing: "safe"}, SafetyKnown: true, SafeToHaul: true},
		{Supply: StartingSupply{Thing: "unknown"}},
	})}
	for _, tc := range []struct {
		supply domain.SupplyAllow
		veto   bool
	}{{allow, true}, {forbid, false}, {other, false}} {
		a, err := domain.NewSupplyAllowAction("a", tc.supply)
		if err != nil {
			t.Fatal(err)
		}
		ref, got := RefuseAction(c, a)
		if got != tc.veto || got && ref.Safeguard != "UnsafeLootSafeguard" {
			t.Fatalf("%v: veto %v by %q", tc.supply, got, ref.Safeguard)
		}
	}
}
