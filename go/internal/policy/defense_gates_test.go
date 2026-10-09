package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Each Go gate stops the count it governs and names itself; with every gate
// open the count follows the larger demand past the old fixed ceilings.
func TestTurretLimitNamesTheGateThatStopped(t *testing.T) {
	for name, tc := range map[string]struct {
		edit func(*DefenseRequest)
		n    int
		gate string
	}{
		"open":      {func(r *DefenseRequest) {}, 2, ""},
		"demand":    {func(r *DefenseRequest) { r.Turret.Max = 1 }, 1, GateTurretThreat},
		"no demand": {func(r *DefenseRequest) { r.Turret.Max = 0 }, 0, GateTurretThreat},
		"power":     {func(r *DefenseRequest) { r.Turret.SpareW = domain.Known(80.0) }, 1, GateTurretPower},
		"stock": {func(r *DefenseRequest) {
			r.Turret.Stock = domain.Known(map[Resource]int64{"Steel": 139, "ComponentIndustrial": 10})
		}, 1, GateTurretStock},
		"unavailable": {func(r *DefenseRequest) { r.Turret.Available = domain.Known(false) }, 0, GateTurretUnavailable},
		"no ceiling":  {func(r *DefenseRequest) { r.Turret.Max = 500 }, 2, ""},
	} {
		r := turretFixture()
		tc.edit(&r)
		if n, gate := turretLimit(r, 2); n != tc.n || gate != tc.gate {
			t.Fatalf("%s: %d %q, want %d %q", name, n, gate, tc.n, tc.gate)
		}
	}
}

func TestMortarLimitNamesTheGateThatStopped(t *testing.T) {
	for name, tc := range map[string]struct {
		edit func(*DefenseRequest)
		n    int
		gate string
	}{
		"open":      {func(r *DefenseRequest) { r.Mortar.Max = 2 }, 2, ""},
		"demand":    {func(r *DefenseRequest) { r.Mortar.Max = 1 }, 1, GateMortarThreat},
		"no demand": {func(r *DefenseRequest) { r.Mortar.Max = 0 }, 0, GateMortarThreat},
		"stock": {func(r *DefenseRequest) {
			r.Mortar.Max, r.Mortar.Stock = 2, domain.Known(map[Resource]int64{"Steel": 150})
		}, 1, GateMortarStock},
		"unavailable": {func(r *DefenseRequest) { r.Mortar.Available = domain.Known(false) }, 0, GateMortarUnavailable},
		"past old cap": {func(r *DefenseRequest) {
			r.Mortar.Max, r.Mortar.Stock = 20, domain.Known(map[Resource]int64{"Steel": 2200})
		}, 12, ""},
	} {
		r := mortarFixture(0)
		tc.edit(&r)
		sites := 2
		if name == "past old cap" {
			sites = 12
		}
		if n, gate := mortarLimit(r, sites); n != tc.n || gate != tc.gate {
			t.Fatalf("%s: %d %q, want %d %q", name, n, gate, tc.n, tc.gate)
		}
	}
}

func TestIEDTierGatedByThreatAndStock(t *testing.T) {
	tier := func(edit func(*DefenseRequest)) DefenseTier {
		r := iedFixture(testHE)
		edit(&r)
		layout, err := DefenseLayouts(r)
		if err != nil {
			t.Fatal(err)
		}
		got, _ := layout.Tier(TierIEDs)
		return got
	}
	if got := tier(func(r *DefenseRequest) {}); len(got.Buildings) != 2 || got.Gated != "" {
		t.Fatal("open", got.Buildings, got.Gated)
	}
	if got := tier(func(r *DefenseRequest) { r.IEDMax = 1 }); len(got.Buildings) != 1 || got.Gated != GateIEDThreat {
		t.Fatal("demand", got.Buildings, got.Gated)
	}
	if got := tier(func(r *DefenseRequest) { r.IEDStock = domain.Known(map[Resource]int64{"Steel": 10}) }); len(got.Buildings) != 1 || got.Gated != GateIEDStock {
		t.Fatal("stock", got.Buildings, got.Gated)
	}
	r := iedFixture(testHE)
	r.IEDMax = 0
	layout, err := DefenseLayouts(r)
	if err != nil || len(layout.Gated) != 1 || layout.Gated[0] != (DefenseGated{Tier: TierIEDs, Reason: GateIEDThreat}) {
		t.Fatal("an emptied tier is still named", layout.Gated, err)
	}
}
