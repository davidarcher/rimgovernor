package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A drop-pod raid on its way down (#908) is a threat before any raider is
// in the census: the ActiveCombat goal opens on it, and the clock admits
// a combat window with nothing to acknowledge once the fight holds a plan,
// so the game ticks to the open instead of parking on no_work.
func TestPendingPodsOpenTheFightAndTheCombatWindow(t *testing.T) {
	f, l := clockWindowFixture(t)
	l.CombatMaxTicks = 30
	census := func(open domain.Tick) {
		t.Helper()
		var err error
		f.Emergency, err = NewEmergencySnapshot(f.Current, f.Tick, EmergencyFacts{ColonistsComplete: domain.Known(true), Colonists: []EmergencyPawn{{ID: "pawn", Dead: domain.Known(false), Downed: domain.Known(false), Bleeding: domain.Known(false), NeedsTend: domain.Known(false)}}, PodsOpen: open})
		if err != nil {
			t.Fatal(err)
		}
	}
	census(530)
	if hostiles, _ := EmergencyNeeds(f.Emergency, f.Current, f.Tick); !positive(measured(hostiles, func(n int64) bool { return n == 1 })) {
		t.Fatalf("pending pods hostiles = %v, want 1", hostiles)
	}
	// No fight yet: the raid must not auto-advance, whatever else is due.
	if d := EvaluateClockWindow(f, l); d.Admitted || len(d.Refused) != 1 || d.Refused[0] != ClockWindowUnsafe {
		t.Fatal(d)
	}
	f.CombatPlan = domain.Unknown[bool]()
	if d := EvaluateClockWindow(f, l); d.Admitted || d.Refused[0] != ClockWindowUnknown {
		t.Fatal(d)
	}
	// The fight's plan admits a combat window with no ids, even with no
	// other work: the open fight is the work.
	f.CombatPlan = domain.Known(true)
	d := EvaluateClockWindow(f, l)
	if !d.Admitted || d.Mode != ClockWindowCombat || d.MaxTicks != 30 || len(d.Hostiles) != 0 {
		t.Fatal(d)
	}
	// Opened pods (or none) are no threat of their own: the census rows
	// are.
	for _, open := range []domain.Tick{0, f.Tick - 1} {
		census(open)
		if hostiles, _ := EmergencyNeeds(f.Emergency, f.Current, f.Tick); !positive(measured(hostiles, func(n int64) bool { return n == 0 })) {
			t.Fatalf("open %d: hostiles = %v, want 0", open, hostiles)
		}
		if d := EvaluateClockWindow(f, l); !d.Admitted || d.Mode != ClockWindowColony {
			t.Fatalf("open %d: %v", open, d)
		}
	}
	// The open tick itself still counts: the raiders spawn as the last pod
	// opens.
	census(f.Tick)
	if !f.Emergency.PodsPending() {
		t.Fatal("pods opening this tick are pending")
	}
}
