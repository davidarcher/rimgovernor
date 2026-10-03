package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// The Royalty write is dispatched under the routine worker too (#1606).
func TestRoyaltyIsARoutineExecutableKind(t *testing.T) {
	t.Parallel()
	if !routineExecutableKind(domain.RoyaltyAction) {
		t.Fatal("royalty must be routine executable")
	}
}

// The permit intent is the plan the goal commits: one Royalty choose_permit
// action carrying holder, faction and permit, keyed by an attempt count that
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
	royalty, ok := actions[0].Royalty()
	if !ok || royalty.Pawn() != "Alice" || royalty.Faction() != "Empire" || royalty.Permit() != "CallMilitaryAidSmall" || royalty.Verb() != domain.RoyaltyChoosePermit {
		t.Fatal(royalty, ok)
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
