package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestPrisonerInteractionModesRoundTrip(t *testing.T) {
	for _, mode := range domain.PrisonerInteractionModes {
		wire := prisonerInteractionModeWire(mode)
		if wire == bridge.PrisonerInteractionModeUnspecified {
			t.Fatal(mode)
		}
		back, ok := prisonerInteractionModeDomain(wire)
		if !ok || back != mode {
			t.Fatal(mode, wire, back)
		}
	}
	if prisonerInteractionModeWire("execution") != bridge.PrisonerInteractionModeUnspecified {
		t.Fatal("unsupported mode wired")
	}
	if _, ok := prisonerInteractionModeDomain(bridge.PrisonerInteractionModeUnspecified); ok {
		t.Fatal("unspecified mode decoded")
	}
}
