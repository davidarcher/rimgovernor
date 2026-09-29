package snapshot

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// The armour half of the former gear/soldier case (#769, #979): recorded
// from a temporary restore of `acceptance run gear/soldier` (fixture
// test/gear_area_prepare mode soldier, both soldiers drafted) at 342c49198,
// the first review (tick 15). The native census carries each pawn's loadout
// model; drafted, both soldiers plan a flak vest and a helmet.
func TestGearSoldierDraftedPlansFlakVestAndHelmet(t *testing.T) {
	r, err := Load("testdata/gear-soldier-drafted.json.gz")
	if err != nil {
		t.Fatal(err)
	}
	gear, known := r.Facts.Gear.Value()
	if !known {
		t.Fatal("gear census unknown")
	}
	loadouts, demand, err := policy.PlanColonyGear(gear.Pawns)
	if err != nil {
		t.Fatal(err)
	}
	soldiers := 0
	for _, l := range loadouts {
		if l.Role == policy.GearSoldier {
			soldiers++
		}
	}
	if soldiers != 2 {
		t.Fatalf("%d soldier loadouts, want the two drafted soldiers", soldiers)
	}
	rows, known := demand.Value()
	if !known {
		t.Fatal("demand unknown")
	}
	counts := map[policy.Resource]int{}
	for _, d := range rows {
		counts[d.Definition] += d.Count
	}
	if counts["Apparel_FlakVest"] < 2 || counts["Apparel_SimpleHelmet"]+counts["Apparel_AdvancedHelmet"] < 2 {
		t.Fatal("soldier armour demand", rows)
	}
}
