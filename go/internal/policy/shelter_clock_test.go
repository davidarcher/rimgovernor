package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A threat's sheltering response is complete only once every undrafted
// colonist is restricted to the Safe area (#1560).
func TestShelterHeldNeedsEveryUndraftedColonistInSafe(t *testing.T) {
	f := shelterFacts("safe")
	f.Hostiles = domain.Known(int64(3))
	if !ShelterHeld(f) {
		t.Fatal("both colonists in Safe under a threat")
	}
	// A drafted colonist is not sheltered and does not count.
	f = shelterFacts("")
	f.Hostiles = domain.Known(int64(3))
	f.RecoverySafety = domain.Known(RecoverySafety{Restrictions: []RecoveryRestriction{{Pawn: "a", Area: domain.Known("")}, {Pawn: "b", Area: domain.Known("safe")}}})
	if ShelterHeld(f) {
		t.Fatal("a is outside Safe")
	}
	workers, _ := f.RecoveryWorkers.Value()
	workers = append([]RecoveryWorker(nil), workers...)
	workers[0].Drafted = domain.Known(true)
	f.RecoveryWorkers = domain.Known(workers)
	if !ShelterHeld(f) {
		t.Fatal("drafted a is exempt")
	}
	workers[0].Drafted = domain.Unknown[bool]()
	if ShelterHeld(f) {
		t.Fatal("unknown draft state is not sheltered")
	}
	// Nobody undrafted: nothing is sheltering.
	workers[0].Drafted, workers[1].Drafted = domain.Known(true), domain.Known(true)
	if ShelterHeld(f) {
		t.Fatal("no undrafted colonist")
	}
	// Fallout alone is not a threat; no hostiles, no shelter hold.
	f = shelterFacts("safe")
	f.DisasterConditions = domain.Known([]DisasterCondition{{ID: "c", Definition: ConditionToxicFallout}})
	if ShelterHeld(f) {
		t.Fatal("fallout is not a threat")
	}
	f = shelterFacts("safe")
	f.Hostiles = domain.Known(int64(3))
	f.ShelterArea = domain.Known("")
	if ShelterHeld(f) {
		t.Fatal("no Safe area")
	}
}

// David Archer's decision on #1560: a threat that only needs sheltering lets
// the clock run once every undrafted colonist is in the Safe area. The
// window is a combat watch acknowledging the hostile pawns; without the
// shelter it is still refused unsafe_colony.
func TestClockWindowShelteredThreatIsWatched(t *testing.T) {
	f, l := clockWindowFixture(t)
	l.CombatMaxTicks = 30
	var err error
	live := func(id PawnID, kind ThreatKind) EmergencyThreat {
		return EmergencyThreat{ID: id, Kind: kind, Dead: domain.Known(false), Downed: domain.Known(false)}
	}
	f.Emergency, err = NewEmergencySnapshot(f.Current, f.Tick, EmergencyFacts{ColonistsComplete: domain.Known(true), Threats: []EmergencyThreat{live("wolf", HuntingPredator), live("boar", Hostile)}})
	if err != nil {
		t.Fatal(err)
	}
	for _, sheltered := range []domain.Fact[bool]{domain.Unknown[bool](), domain.Known(false)} {
		f.Sheltered = sheltered
		if d := EvaluateClockWindow(f, l); d.Admitted || !reflect.DeepEqual(d.Refused, []ClockWindowReason{ClockWindowUnsafe}) {
			t.Fatal(d)
		}
	}
	f.Sheltered = domain.Known(true)
	d := EvaluateClockWindow(f, l)
	if !d.Admitted || d.Mode != ClockWindowCombat || d.MaxTicks != 30 || !reflect.DeepEqual(d.Hostiles, []PawnID{"boar", "wolf"}) {
		t.Fatal(d)
	}
	// A hostile building is not waited out by sheltering.
	f.Emergency, err = NewEmergencySnapshot(f.Current, f.Tick, EmergencyFacts{ColonistsComplete: domain.Known(true), Threats: []EmergencyThreat{{ID: "hive", Kind: HostileBuilding, Dead: domain.Known(false), Downed: domain.Known(false), Animal: domain.Known(false), SnapshotToken: "cas", Definition: "Hive", Cells: []domain.Cell{{X: 5, Z: 5}}}}})
	if err != nil {
		t.Fatal(err)
	}
	if d := EvaluateClockWindow(f, l); d.Admitted || !reflect.DeepEqual(d.Refused, []ClockWindowReason{ClockWindowUnsafe}) {
		t.Fatal(d)
	}
}
