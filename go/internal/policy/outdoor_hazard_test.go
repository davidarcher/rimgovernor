package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func hazardFacts(temp float64, conditions ...string) RoundsFacts {
	var cs []DisasterCondition
	for _, c := range conditions {
		cs = append(cs, DisasterCondition{ID: c, Definition: c})
	}
	return RoundsFacts{DisasterConditions: domain.Known(cs), OutdoorTemperature: domain.Known(temp)}
}

func TestOutdoorHazardsPerKind(t *testing.T) {
	cases := []struct {
		name  string
		facts RoundsFacts
		want  []HazardKind
	}{
		{"temperate", hazardFacts(15), nil},
		{"cold", hazardFacts(-20), []HazardKind{HazardExtremeCold}},
		{"heat", hazardFacts(45), []HazardKind{HazardExtremeHeat}},
		{"fallout", hazardFacts(15, ConditionToxicFallout), []HazardKind{HazardToxicFallout}},
		{"cold snap alone is not extreme", hazardFacts(-5, ConditionColdSnap), nil},
	}
	for _, c := range cases {
		hazards, known := OutdoorHazardsOf(c.facts).Value()
		if !known || len(hazards) != len(c.want) {
			t.Fatalf("%s: hazards = %v, %v", c.name, hazards, known)
		}
		for i, k := range c.want {
			if hazards[i].Kind != k {
				t.Fatalf("%s: hazards = %v", c.name, hazards)
			}
		}
		if got := OutdoorAcquisitionHeld(c.facts); got != (len(c.want) > 0) {
			t.Fatalf("%s: acquisition held = %v", c.name, got)
		}
	}
}

func TestOutdoorHazardUnknownReadsNeverHold(t *testing.T) {
	if OutdoorAcquisitionHeld(RoundsFacts{}) {
		t.Fatal("unknown reads held acquisition")
	}
	if OutdoorAcquisitionHeld(RoundsFacts{OutdoorTemperature: domain.Known(-60.0)}) {
		t.Fatal("unknown condition census held acquisition")
	}
}

func TestOutdoorHazardUnrecognisedKindFailsClosed(t *testing.T) {
	h := domain.Known([]OutdoorHazard{{Kind: "meteor_storm"}})
	if !OutdoorHazardHeld(h) {
		t.Fatal("unrecognised kind did not hold")
	}
}

func TestOutdoorHazardHoldsRemoteRequests(t *testing.T) {
	cold := domain.Known([]OutdoorHazard{{Kind: HazardExtremeCold}})
	if got := remoteThreatHold(RemoteSafety{Threat: domain.Known(false), Hazard: cold}); got != RemoteHoldHazard {
		t.Fatalf("remote hold = %q", got)
	}
	if got := remoteThreatHold(RemoteSafety{Threat: domain.Known(false), Hazard: domain.Known([]OutdoorHazard(nil))}); got != "" {
		t.Fatalf("clear hazard held: %q", got)
	}
	if got := recoveryHold(RecoveryThing{Evidenced: true}, RecoveryRequest{Hazard: cold}); got != RemoteHoldHazard {
		t.Fatalf("recovery hold = %q", got)
	}
	threat := RecoveryRequest{Hazard: cold, Threat: domain.Known(true)}
	if got := recoveryHold(RecoveryThing{Evidenced: true}, threat); got != RemoteHoldThreat {
		t.Fatalf("threat did not explain first: %q", got)
	}
}
