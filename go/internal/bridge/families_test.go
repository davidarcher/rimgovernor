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
