package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestClockWindowHuntFightWatchesPrey(t *testing.T) {
	f, l := clockWindowFixture(t)
	l.CombatMaxTicks = 30
	// No hunt fight: an ordinary window is unchanged.
	if d := EvaluateClockWindow(f, l); !d.Admitted || d.Mode != ClockWindowColony || len(d.Hostiles) != 0 || d.MaxTicks != 100 {
		t.Fatal(d)
	}
	// An open hunt fight with live prey and no hostile is a combat window
	// whose watched ids are the prey, under the combat budget.
	f.CombatPlan = domain.Known(true)
	f.HuntPrey = []domain.PawnID{"deer1", "deer2"}
	if d := EvaluateClockWindow(f, l); !d.Admitted || d.Mode != ClockWindowCombat || d.MaxTicks != 30 || !reflect.DeepEqual(d.Hostiles, []PawnID{"deer1", "deer2"}) {
		t.Fatal(d)
	}
	// Prey without an admitted plan watch nothing.
	f.CombatPlan = domain.Known(false)
	if d := EvaluateClockWindow(f, l); !d.Admitted || d.Mode != ClockWindowColony || len(d.Hostiles) != 0 {
		t.Fatal(d)
	}
}
