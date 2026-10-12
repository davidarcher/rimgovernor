package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func raisedEnvelope(t *testing.T, owed domain.Fact[bool]) bool {
	t.Helper()
	f := stableRounds()
	f.EnvelopeOwed = owed
	for _, c := range needs(t, f, RoundsLatches{}).Concerns {
		if c.ID == MaintainClimateEnvelope {
			return true
		}
	}
	return false
}

func TestEnvelopeOwedRaisesOnlyWhileSomethingIsMissing(t *testing.T) {
	if !raisedEnvelope(t, domain.Known(true)) {
		t.Fatal("an owed vent or roof raised no MaintainClimateEnvelope")
	}
	if raisedEnvelope(t, domain.Known(false)) {
		t.Fatal("a complete envelope raised MaintainClimateEnvelope")
	}
	if raisedEnvelope(t, domain.Unknown[bool]()) {
		t.Fatal("an unknown envelope raised MaintainClimateEnvelope")
	}
}

func TestEnvelopeOpsKeepsVentsAndRoofsOnly(t *testing.T) {
	ops := []Operation{{Kind: OpWallIn}, {Kind: OpVentIn}, {Kind: OpFloorIn}, {Kind: OpRoofIn}, {Kind: OpBuild}}
	got := EnvelopeOps(ops)
	if len(got) != 2 || got[0].Kind != OpVentIn || got[1].Kind != OpRoofIn {
		t.Fatalf("EnvelopeOps = %+v", got)
	}
}
