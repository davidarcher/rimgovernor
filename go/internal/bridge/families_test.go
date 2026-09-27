package bridge

import (
	"testing"

	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
)

func TestFactFamilyFromWire(t *testing.T) {
	for _, wire := range []k.FactFamily{k.FactFamily_FACT_FAMILY_DEFINITIONS, k.FactFamily_FACT_FAMILY_RESEARCH} {
		if _, ok := FactFamilyFromWire(wire); !ok {
			t.Fatal(wire)
		}
	}
	if _, ok := FactFamilyFromWire(k.FactFamily_FACT_FAMILY_UNSPECIFIED); ok {
		t.Fatal("unspecified family accepted")
	}
}

// TestFactFamilyTickTolerance is the per-family staleness contract (#243):
// a row serves a later scope while the advance is within the family's
// tolerance, never a scope behind it, and the tolerances order as the
// facts change: identity and research on the scale of a day, colony and
// rooms an hour, pawns and the emergency census minutes.
func TestFactFamilyTickTolerance(t *testing.T) {
	for _, family := range FactFamilies() {
		tolerance := family.TickTolerance()
		if !family.Fresh(100, 100) || family.Fresh(100, 99) {
			t.Fatal(family, "same tick or rewind")
		}
		if tolerance == FactTickUnbounded {
			if !family.Fresh(0, 1<<40) {
				t.Fatal(family, "bounded")
			}
			continue
		}
		if tolerance < 0 || !family.Fresh(100, 100+tolerance) || family.Fresh(100, 101+tolerance) {
			t.Fatal(family, tolerance)
		}
	}
	if FactTickToleranceIdentity != 0 || FactTickToleranceResearch < FactTickToleranceColony || FactTickToleranceColony < FactTickTolerancePawns || FactTickToleranceRooms < FactTickTolerancePawns || FactTickToleranceEmergency > FactTickTolerancePawns {
		t.Fatal("tolerances out of order")
	}
	if FactFamily("other").TickTolerance() != 0 || FactFamily("other").Fresh(1, 2) {
		t.Fatal("unknown family tolerates an advance")
	}
}
