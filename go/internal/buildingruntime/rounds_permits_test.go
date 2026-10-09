package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// The permit intent is the plan the goal commits: one choose_permit pawn
// setting carrying holder, faction and permit, keyed by holder and permit.
func TestPermitPlanRecordsTheIntent(t *testing.T) {
	t.Parallel()
	intent := policy.PermitIntent{Holder: "Alice", Faction: "Empire", Permit: "CallMilitaryAidSmall"}
	if prefix := permitMethodPrefix(intent); prefix != "permit-Alice-CallMilitaryAidSmall-" {
		t.Fatal(prefix)
	}
	plan, err := permitPlan(intent)
	if err != nil {
		t.Fatal(err)
	}
	actions := plan.Actions()
	if len(actions) != 1 {
		t.Fatal(actions)
	}
	setting, _ := actions[0].PawnSettings()
	faction, permit, ok := setting.ChoosePermit()
	if !ok || setting.Pawn() != "Alice" || faction != "Empire" || permit != "CallMilitaryAidSmall" {
		t.Fatal(setting, ok)
	}
}
