package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// A bill selector that chose nothing reports why, never nothing_to_do for a
// cause that is not an absent deficit (#1882).
func TestBillGapVerdictNamesEachCause(t *testing.T) {
	for _, c := range []struct {
		gap  policy.BillGap
		want string
	}{
		{policy.BillGapNothingWanted, "nothing_to_do"},
		{policy.BillGapInProduction, "already_working_on_it:gestation_bill"},
		{policy.BillGapNoRecipe, "awaiting_plan:gestation_bill:no_usable_recipe"},
		{policy.BillGapBenchFull, "awaiting_plan:gestation_bill:bench_bills_full"},
		{policy.BillGapWaste, "awaiting_plan:mech_waste"},
		{policy.BillGapNoCharger, "awaiting_plan:mech_charger"},
	} {
		v := billGapVerdict(c.gap, "gestation_bill")
		if v.Validate() != nil || v.String() != c.want {
			t.Errorf("%s: %q (%v)", c.gap, v.String(), v.Validate())
		}
	}
	defer func() {
		if recover() == nil {
			t.Fatal("an unknown gap was accepted")
		}
	}()
	billGapVerdict("bogus", "gestation_bill")
}
