package bridge

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// An eat trigger setting builds the eat_trigger arm.
func TestPawnSettingsBuildsEatTriggerIntent(t *testing.T) {
	value, err := domain.NewEatTriggerSetting("Human1", 0.125)
	if err != nil {
		t.Fatal(err)
	}
	action, err := domain.NewPawnSettingsAction("a1", value)
	if err != nil {
		t.Fatal(err)
	}
	wire, err := IntentAction("plan/1", action)
	if err != nil {
		t.Fatal(err)
	}
	if s := wire.GetPawnSettings(); s.GetPawnId() != "Human1" || s.GetEatTrigger() != 0.125 || s.GetSetting() == nil {
		t.Fatalf("%v", wire)
	}
}

// The observed eat trigger rides the work detail and stays within 0..1.
func TestValidateSettingsEatTrigger(t *testing.T) {
	level := float32(0.3)
	if err := validateSettings(&o.PawnSettings{EatTrigger: &level}, true, false, false); err != nil {
		t.Fatal(err)
	}
	if err := validateSettings(&o.PawnSettings{EatTrigger: &level}, false, true, true); err == nil {
		t.Fatal("eat trigger accepted without work detail")
	}
	level = 1.5
	if err := validateSettings(&o.PawnSettings{EatTrigger: &level}, true, false, false); err == nil {
		t.Fatal("eat trigger above 1 accepted")
	}
}
