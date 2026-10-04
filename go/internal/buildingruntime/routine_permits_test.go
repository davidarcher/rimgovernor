package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// The permit intent is the plan the goal commits: one choose_permit
// pawn setting carrying holder, faction and permit, keyed by an attempt count that
// stops retrying a refused permit.
func TestPermitMethodRecordsTheIntentAndBoundsAttempts(t *testing.T) {
	t.Parallel()
	intent := policy.PermitIntent{Holder: "Alice", Faction: "Empire", Permit: "CallMilitaryAidSmall"}
	method, plan, exhausted, err := permitMethod(intent, nil, 1)
	if err != nil || exhausted || method != "permit-Alice-CallMilitaryAidSmall-0" {
		t.Fatal(method, exhausted, err)
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
	var history []domain.GoalMethod
	for i := 0; i < maxMedicalAttemptsPerPatient; i++ {
		history = append(history, domain.GoalMethod{Method: domain.MethodID("permit-Alice-CallMilitaryAidSmall-" + string(rune('0'+i))), Epoch: 1})
	}
	if _, _, exhausted, err := permitMethod(intent, history, 1); err != nil || !exhausted {
		t.Fatal("attempts must be bounded", exhausted, err)
	}
	// Another permit, or another epoch, starts fresh.
	if _, _, exhausted, _ := permitMethod(policy.PermitIntent{Holder: "Alice", Faction: "Empire", Permit: "TradeSettlement"}, history, 1); exhausted {
		t.Fatal("another permit is not spent")
	}
	if _, _, exhausted, _ := permitMethod(intent, history, 2); exhausted {
		t.Fatal("a new goal epoch retries")
	}
}
