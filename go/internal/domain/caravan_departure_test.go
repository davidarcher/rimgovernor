package domain

import (
	"fmt"
	"strings"
	"testing"
)

func TestCaravanDepartureIntentAndClosedVariants(t *testing.T) {
	intent, err := NewCaravanDeparture([]PawnID{"beta", "alpha"}, []CargoItem{{"MealSimple", 10}}, 42)
	if err != nil {
		t.Fatal(err)
	}
	if got := intent.Crew(); len(got) != 2 || got[0] != "alpha" || got[1] != "beta" {
		t.Fatal("crew not canonically sorted", got)
	}
	if got := intent.Cargo(); len(got) != 1 || got[0].Definition != "MealSimple" || got[0].Count != 10 {
		t.Fatal("cargo not preserved", got)
	}
	if intent.DestinationTile() != 42 {
		t.Fatal("destination tile not preserved")
	}
	action, err := NewCaravanDepartureAction("caravan", intent)
	if err != nil || action.ID() != "caravan" || action.Kind() != CaravanDepartureAction {
		t.Fatal(action, err)
	}
	if got, ok := action.CaravanDeparture(); !ok || got != intent {
		t.Fatal(got, ok)
	}
	if _, ok := action.Building(); ok {
		t.Fatal("caravan departure exposed building")
	}
	if _, ok := action.GearReplace(); ok {
		t.Fatal("caravan departure exposed gear replace")
	}
	if _, err := NewCaravanDepartureAction("caravan", CaravanDeparture{}); err == nil {
		t.Fatal("zero intent accepted")
	}
}

func TestCaravanDepartureCanonicalizesAndDeduplicatesCrewAndCargo(t *testing.T) {
	a, err := NewCaravanDeparture([]PawnID{"alpha", "beta"}, []CargoItem{{"MealSimple", 5}, {"Silver", 100}}, 1)
	if err != nil {
		t.Fatal(err)
	}
	b, err := NewCaravanDeparture([]PawnID{"beta", "alpha"}, []CargoItem{{"Silver", 100}, {"MealSimple", 5}}, 1)
	if err != nil {
		t.Fatal(err)
	}
	if a != b {
		t.Fatal("differently ordered crew/cargo did not canonicalize equal")
	}
	if _, err := NewCaravanDeparture([]PawnID{"alpha", "alpha"}, []CargoItem{{"MealSimple", 5}}, 1); err == nil {
		t.Fatal("duplicate crew accepted")
	}
	if _, err := NewCaravanDeparture([]PawnID{"alpha"}, []CargoItem{{"MealSimple", 5}, {"MealSimple", 3}}, 1); err == nil {
		t.Fatal("duplicate cargo definition accepted")
	}
}

func TestCaravanDepartureRejectsInvalidInputs(t *testing.T) {
	if _, err := NewCaravanDeparture(nil, nil, 1); err == nil {
		t.Fatal("empty crew accepted")
	}
	if _, err := NewCaravanDeparture([]PawnID{"alpha"}, nil, -1); err == nil {
		t.Fatal("negative destination tile accepted")
	}
	for _, invalid := range []string{"", " ", "x\x00y", strings.Repeat("x", 257), string([]byte{0xff})} {
		if _, err := NewCaravanDeparture([]PawnID{PawnID(invalid)}, nil, 1); err == nil {
			t.Fatal("invalid crew pawn accepted")
		}
		if _, err := NewCaravanDeparture([]PawnID{"alpha"}, []CargoItem{{invalid, 1}}, 1); err == nil {
			t.Fatal("invalid cargo definition accepted")
		}
	}
	if _, err := NewCaravanDeparture([]PawnID{"alpha"}, []CargoItem{{"Silver", 0}}, 1); err == nil {
		t.Fatal("zero cargo count accepted")
	}
	many := make([]PawnID, 65)
	for i := range many {
		many[i] = PawnID(fmt.Sprintf("pawn-%d", i))
	}
	if _, err := NewCaravanDeparture(many, nil, 1); err == nil {
		t.Fatal("oversized crew accepted")
	}
}

func TestCaravanDeparturePlanDoesNotRequireADraftPrerequisite(t *testing.T) {
	intent, _ := NewCaravanDeparture([]PawnID{"alpha"}, []CargoItem{{"MealSimple", 5}}, 7)
	action, _ := NewCaravanDepartureAction("caravan", intent)
	plan, err := NewPlan("plan", 1, []Action{action})
	if err != nil {
		t.Fatal(err)
	}
	progress, err := NewProgress(plan, "caravan")
	if err != nil || progress.Action() != action || progress.View().Stage != Pending || progress.View().Unresolved {
		t.Fatal(progress, err)
	}
}

func TestCaravanDepartureHandlerCoverageIsRequired(t *testing.T) {
	if err := ValidateHandlerCoverage([]ActionKind{BuildingAction, OwnedDraftAction, MeleeAttackAction, SupplyAllowAction}); err == nil {
		t.Fatal("missing caravan departure handler accepted")
	}
	if err := ValidateHandlerCoverage([]ActionKind{BuildingAction, OwnedDraftAction, MeleeAttackAction, SupplyAllowAction, CaravanDepartureAction, CaravanDepartureAction}); err == nil {
		t.Fatal("duplicate caravan departure handler accepted")
	}
	if err := ValidateHandlerCoverage(SupportedActionKinds()); err != nil {
		t.Fatal(err)
	}
}
