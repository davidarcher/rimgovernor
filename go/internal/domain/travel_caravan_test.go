package domain

import (
	"strings"
	"testing"
)

func TestTravelCaravanIntentAndClosedVariants(t *testing.T) {
	intent, err := NewTravelCaravan("caravan-1", TravelMove, 42)
	if err != nil {
		t.Fatal(err)
	}
	if intent.Caravan() != "caravan-1" || intent.Kind() != TravelMove || intent.DestinationTile() != 42 {
		t.Fatal("travel facts not preserved", intent)
	}
	action, err := NewTravelCaravanAction("travel", intent)
	if err != nil || action.ID() != "travel" || action.Kind() != TravelCaravanAction {
		t.Fatal(action, err)
	}
	if got, ok := action.TravelCaravan(); !ok || got != intent {
		t.Fatal(got, ok)
	}
	if _, ok := action.CaravanDeparture(); ok {
		t.Fatal("travel caravan exposed caravan departure")
	}
	if _, ok := action.Building(); ok {
		t.Fatal("travel caravan exposed building")
	}
	if _, err := NewTravelCaravanAction("travel", TravelCaravan{}); err == nil {
		t.Fatal("zero intent accepted")
	}
}

func TestTravelCaravanDestinationSemanticsPerKind(t *testing.T) {
	for _, kind := range []TravelKind{TravelMove, TravelVisit} {
		if _, err := NewTravelCaravan("caravan-1", kind, -1); err == nil {
			t.Fatalf("%s accepted a missing destination", kind)
		}
		if _, err := NewTravelCaravan("caravan-1", kind, -2); err == nil {
			t.Fatalf("%s accepted a negative destination", kind)
		}
		if _, err := NewTravelCaravan("caravan-1", kind, 0); err != nil {
			t.Fatalf("%s rejected a valid destination: %v", kind, err)
		}
	}
	for _, kind := range []TravelKind{TravelReturnHome, TravelStop} {
		if _, err := NewTravelCaravan("caravan-1", kind, 0); err == nil {
			t.Fatalf("%s accepted a destination", kind)
		}
		if _, err := NewTravelCaravan("caravan-1", kind, -1); err != nil {
			t.Fatalf("%s rejected its sentinel destination: %v", kind, err)
		}
	}
}

func TestTravelCaravanRejectsInvalidInputs(t *testing.T) {
	if _, err := NewTravelCaravan("", TravelStop, -1); err == nil {
		t.Fatal("empty caravan id accepted")
	}
	for _, invalid := range []string{"", " ", "x\x00y", strings.Repeat("x", 257), string([]byte{0xff})} {
		if _, err := NewTravelCaravan(CaravanID(invalid), TravelStop, -1); err == nil {
			t.Fatal("invalid caravan id accepted")
		}
	}
	if _, err := NewTravelCaravan("caravan-1", TravelKind("bogus"), -1); err == nil {
		t.Fatal("invalid travel kind accepted")
	}
}

func TestTravelCaravanPlanDoesNotRequireADraftPrerequisite(t *testing.T) {
	intent, _ := NewTravelCaravan("caravan-1", TravelStop, -1)
	action, _ := NewTravelCaravanAction("travel", intent)
	plan, err := NewPlan("plan", 1, []Action{action})
	if err != nil {
		t.Fatal(err)
	}
	progress, err := NewProgress(plan, "travel")
	if err != nil || progress.Action() != action || progress.View().Stage != Pending || progress.View().Unresolved {
		t.Fatal(progress, err)
	}
}

func TestTravelCaravanHandlerCoverageIsRequired(t *testing.T) {
	if err := ValidateHandlerCoverage([]ActionKind{BuildingAction, OwnedDraftAction, MeleeAttackAction, SupplyAllowAction}); err == nil {
		t.Fatal("missing travel caravan handler accepted")
	}
	if err := ValidateHandlerCoverage([]ActionKind{BuildingAction, OwnedDraftAction, MeleeAttackAction, SupplyAllowAction, TravelCaravanAction, TravelCaravanAction}); err == nil {
		t.Fatal("duplicate travel caravan handler accepted")
	}
	if err := ValidateHandlerCoverage(SupportedActionKinds()); err != nil {
		t.Fatal(err)
	}
}
